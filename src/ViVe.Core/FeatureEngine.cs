// SPDX-License-Identifier: GPL-3.0-or-later
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Albacore.ViVe.Exceptions;
using Albacore.ViVe.NativeEnums;
using Albacore.ViVe.NativeStructs;

namespace ViVe.Core
{
    /// <summary>Default <see cref="IFeatureManager"/> built on <see cref="IFeatureConfigurationApi"/>.</summary>
    public sealed class FeatureEngine : IFeatureManager
    {
        private readonly IFeatureConfigurationApi _api;
        private readonly FeatureStoreWriter _writer;
        private readonly Func<DateTimeOffset> _clock;
        // Native calls are serialized so overlapping UI actions can't interleave snapshot/write/verify.
        private readonly SemaphoreSlim _gate = new(1, 1);

        public FeatureEngine(IFeatureConfigurationApi api, Func<DateTimeOffset>? clock = null)
        {
            _api = api ?? throw new ArgumentNullException(nameof(api));
            _writer = new FeatureStoreWriter(api);
            _clock = clock ?? (() => DateTimeOffset.UtcNow);
        }

        public bool IsPlatformSupported => _api.IsPlatformSupported;

        public int OsBuild => _api.OsBuild;

        #region Planning
        public FeatureOperationPlan Plan(FeatureOperationRequest request)
        {
            if (request == null) throw new ArgumentNullException(nameof(request));
            var errors = new List<string>();
            var warnings = new List<string>();
            var risk = OperationRisk.Standard;

            if (!_api.IsPlatformSupported)
                errors.Add($"Feature management requires Windows build {NativeFeatureConfigurationApi.MinimumSupportedBuild} or newer.");
            if (request.FeatureId == 0)
                errors.Add("Feature ID 0 is not valid.");
            if (request.Stores == FeatureStores.None)
                errors.Add("Select at least one store (runtime and/or boot).");

            switch (request.Kind)
            {
                case FeatureOperationKind.Enable:
                case FeatureOperationKind.Disable:
                    ValidateWritablePriority(request.EffectiveSetPriority, errors);
                    if (request.Variant > 63)
                        errors.Add("Variant must be between 0 and 63.");
                    if (!Enum.IsDefined(typeof(RTL_FEATURE_VARIANT_PAYLOAD_KIND), request.VariantPayloadKind))
                        errors.Add("Variant payload kind must be None, Resident or External.");
                    if (request.EffectiveSetPriority == RTL_FEATURE_CONFIGURATION_PRIORITY.UserPolicy &&
                        (request.Variant != 0 || request.VariantPayload != 0 || request.VariantPayloadKind != RTL_FEATURE_VARIANT_PAYLOAD_KIND.None || request.Experiment))
                        errors.Add("The UserPolicy priority only supports the enabled state (no variant, payload or experiment flag).");
                    if (FeaturePriorities.IsHigherRisk(request.EffectiveSetPriority))
                    {
                        risk = OperationRisk.High;
                        warnings.Add($"Priority {request.EffectiveSetPriority} overrides configuration normally managed by Windows servicing or policy.");
                    }
                    if (request.Experiment)
                    {
                        risk = OperationRisk.High;
                        warnings.Add("The experiment (WexpConfig) flag marks this as a Windows experiment configuration, which Windows may later replace.");
                    }
                    if (request.Kind == FeatureOperationKind.Disable)
                        warnings.Add("Disabling a feature Windows relies on can break functionality.");
                    break;

                case FeatureOperationKind.Reset:
                    if (request.ResetAllPriorities)
                    {
                        risk = OperationRisk.High;
                        warnings.Add("Resetting every writable priority also removes configuration delivered by Windows servicing (like ViVeTool /reset without /priority).");
                    }
                    else
                    {
                        ValidateWritablePriority(request.EffectiveSetPriority, errors);
                        if (FeaturePriorities.IsHigherRisk(request.EffectiveSetPriority))
                            risk = OperationRisk.High;
                    }
                    break;

                case FeatureOperationKind.Restore:
                    var snapshot = request.RestoreSnapshot;
                    if (snapshot == null)
                        errors.Add("No snapshot was provided to restore.");
                    else
                    {
                        if (snapshot.FeatureId != request.FeatureId)
                            errors.Add("The snapshot belongs to a different feature.");
                        if (!snapshot.IsCompleteFor(request.Stores))
                            errors.Add("The previous state was not captured completely, so it cannot be restored reliably.");
                        if (snapshot.OsBuild != 0 && _api.OsBuild != 0 && snapshot.OsBuild != _api.OsBuild)
                            warnings.Add($"The snapshot was captured on build {snapshot.OsBuild}; this system is build {_api.OsBuild}.");
                    }
                    if (request.RestorePriorities == null || request.RestorePriorities.Count == 0)
                        errors.Add("No priorities to restore were provided.");
                    else
                    {
                        foreach (var p in request.RestorePriorities)
                        {
                            ValidateWritablePriority(p, errors);
                            if (FeaturePriorities.IsHigherRisk(p))
                                risk = OperationRisk.High;
                        }
                    }
                    break;

                default:
                    errors.Add($"Unsupported operation: {request.Kind}.");
                    break;
            }

            if (request.Stores.Includes(FeatureStore.Boot))
                warnings.Add("Boot store changes take full effect after a restart.");
            if (request.Kind == FeatureOperationKind.Enable)
                warnings.Add("Enabling a feature does not guarantee a visible change in Windows.");

            return new FeatureOperationPlan(request, errors, warnings, risk, request.Describe());
        }

        private static void ValidateWritablePriority(RTL_FEATURE_CONFIGURATION_PRIORITY priority, List<string> errors)
        {
            if ((uint)priority > 15)
                errors.Add($"Priority {(uint)priority} is out of range (0-15).");
            else if (FeaturePriorities.IsImmutable(priority))
                errors.Add($"{priority} ({(int)priority}) is an immutable priority and can't be written to.");
        }
        #endregion

        #region Queries
        public Task<FeatureQueryResult> QueryAsync(uint featureId, CancellationToken cancellationToken = default) =>
            RunGatedAsync(() => QueryCore(featureId), cancellationToken);

        public Task<FeatureSnapshot> CaptureSnapshotAsync(uint featureId, CancellationToken cancellationToken = default) =>
            RunGatedAsync(() => CaptureSnapshotCore(featureId), cancellationToken);

        public Task<FeatureSystemStatus> GetSystemStatusAsync(CancellationToken cancellationToken = default) =>
            RunGatedAsync(GetSystemStatusCore, cancellationToken);

        public Task<IReadOnlyList<uint>?> GetConfiguredFeatureIdsAsync(CancellationToken cancellationToken = default) =>
            RunGatedAsync(GetConfiguredFeatureIdsCore, cancellationToken);

        private IReadOnlyList<uint>? GetConfiguredFeatureIdsCore()
        {
            if (!_api.IsPlatformSupported) return null;
            try
            {
                return _api.QueryAll(FeatureStore.Runtime)?.Select(c => c.FeatureId).Distinct().OrderBy(id => id).ToArray();
            }
            catch (Exception ex) when (IsNativeFailure(ex)) { return null; }
        }

        private FeatureQueryResult QueryCore(uint featureId)
        {
            var now = _clock();
            if (!_api.IsPlatformSupported)
                return new FeatureQueryResult(featureId, false, "Feature management is not supported on this system.") { QueriedAtUtc = now };
            if (featureId == 0)
                return new FeatureQueryResult(featureId, false, "Feature ID 0 is not valid.") { QueriedAtUtc = now };
            try
            {
                var all = _api.QueryAll(FeatureStore.Runtime);
                var effective = _api.Query(featureId, FeatureStore.Runtime);
                var boot = _api.ReadBootOverrides(featureId);
                return new FeatureQueryResult(featureId, all != null, all == null ? "The runtime feature store could not be queried." : null)
                {
                    Effective = effective,
                    RuntimeEntries = all?.Where(c => c.FeatureId == featureId).OrderByDescending(c => (uint)c.Priority).ToArray() ?? Lists.Empty<FeatureConfiguration>(),
                    BootEntriesAvailable = boot.Success,
                    BootEntries = boot.Entries.OrderByDescending(c => (uint)c.Priority).ToArray(),
                    QueriedAtUtc = now,
                };
            }
            catch (Exception ex) when (IsNativeFailure(ex))
            {
                return new FeatureQueryResult(featureId, false, ex.Message) { QueriedAtUtc = now };
            }
        }

        private FeatureSnapshot CaptureSnapshotCore(uint featureId)
        {
            var now = _clock();
            IReadOnlyList<FeatureConfiguration>? runtime = null;
            string? error = null;
            try
            {
                runtime = _api.QueryAll(FeatureStore.Runtime)?
                    .Where(c => c.FeatureId == featureId && !FeaturePriorities.IsImmutable(c.Priority))
                    .ToArray();
                if (runtime == null)
                    error = "The runtime feature store could not be queried.";
            }
            catch (Exception ex) when (IsNativeFailure(ex)) { error = ex.Message; }

            BootOverrideReadResult boot;
            try { boot = _api.ReadBootOverrides(featureId); }
            catch (Exception ex) when (IsNativeFailure(ex)) { boot = BootOverrideReadResult.Failed(ex.Message); }
            if (!boot.Success)
                error = error == null ? boot.Error : error + " " + boot.Error;

            return new FeatureSnapshot(
                featureId, now, _api.OsBuild,
                runtime != null, runtime ?? Lists.Empty<FeatureConfiguration>(),
                boot.Success, boot.Entries.Where(c => !FeaturePriorities.IsImmutable(c.Priority)).ToArray(),
                error);
        }

        private FeatureSystemStatus GetSystemStatusCore()
        {
            if (!_api.IsPlatformSupported)
                return new FeatureSystemStatus(false, _api.OsBuild, null, null, "Not supported on this system.", null, null);
            ulong? stamp = null;
            BSD_FEATURE_CONFIGURATION_STATE? bootStatus = null;
            string? bootError = null;
            int? count = null, userCount = null;
            try { stamp = _api.QueryChangeStamp(); } catch (Exception ex) when (IsNativeFailure(ex)) { }
            try
            {
                var status = _api.GetBootStatus(out var state);
                if (status == NtStatus.Success) bootStatus = state;
                else bootError = NtStatus.DescribeWithCode(status);
            }
            catch (Exception ex) when (IsNativeFailure(ex)) { bootError = ex.Message; }
            try
            {
                var all = _api.QueryAll(FeatureStore.Runtime);
                if (all != null)
                {
                    count = all.Count;
                    userCount = all.Count(c => c.Priority == RTL_FEATURE_CONFIGURATION_PRIORITY.User);
                }
            }
            catch (Exception ex) when (IsNativeFailure(ex)) { }
            return new FeatureSystemStatus(true, _api.OsBuild, stamp, bootStatus, bootError, count, userCount);
        }
        #endregion

        #region Execution
        public Task<FeatureOperationResult> ExecuteAsync(FeatureOperationRequest request, CancellationToken cancellationToken = default) =>
            RunGatedAsync(() => ExecuteCore(request), cancellationToken);

        public async Task<IReadOnlyList<FeatureOperationResult>> ExecuteBatchAsync(
            IEnumerable<FeatureOperationRequest> requests,
            IProgress<FeatureOperationResult>? progress = null,
            CancellationToken cancellationToken = default)
        {
            if (requests == null) throw new ArgumentNullException(nameof(requests));
            var results = new List<FeatureOperationResult>();
            foreach (var request in requests)
            {
                if (cancellationToken.IsCancellationRequested)
                    break;
                // The item itself is not cancellable once started: a half-applied change is worse than finishing it.
                var result = await ExecuteAsync(request, CancellationToken.None).ConfigureAwait(false);
                results.Add(result);
                progress?.Report(result);
            }
            return results;
        }

        private FeatureOperationResult ExecuteCore(FeatureOperationRequest request)
        {
            var started = _clock();
            var plan = Plan(request);
            if (!plan.IsValid)
                return Finish(new FeatureOperationResult(request, FeatureOperationStatus.Rejected, string.Join(" ", plan.ValidationErrors)), started);

            var before = CaptureSnapshotCore(request.FeatureId);
            var includeRuntime = request.Stores.Includes(FeatureStore.Runtime);
            var includeBoot = request.Stores.Includes(FeatureStore.Boot);

            RTL_FEATURE_CONFIGURATION_UPDATE[]? runtimeUpdates = null, bootUpdates = null;
            try
            {
                switch (request.Kind)
                {
                    case FeatureOperationKind.Enable:
                    case FeatureOperationKind.Disable:
                        var updates = FeatureUpdateBuilder.CreateSetUpdates(
                            new[] { request.FeatureId },
                            request.Kind == FeatureOperationKind.Enable ? RTL_FEATURE_ENABLED_STATE.Enabled : RTL_FEATURE_ENABLED_STATE.Disabled,
                            new FeatureSetOptions
                            {
                                EnabledStateOptions = request.Experiment ? RTL_FEATURE_ENABLED_STATE_OPTIONS.WexpConfig : RTL_FEATURE_ENABLED_STATE_OPTIONS.None,
                                Priority = request.EffectiveSetPriority,
                                Variant = request.Variant,
                                VariantPayloadKind = request.VariantPayloadKind,
                                VariantPayload = request.VariantPayload,
                            });
                        if (includeRuntime) runtimeUpdates = updates;
                        if (includeBoot) bootUpdates = updates;
                        break;

                    case FeatureOperationKind.Reset:
                        if (request.ResetAllPriorities)
                        {
                            if ((includeRuntime && !before.RuntimeCaptured) || (includeBoot && !before.BootCaptured))
                                return Finish(new FeatureOperationResult(request, FeatureOperationStatus.Failed,
                                    "Could not determine which priorities to reset: " + (before.CaptureError ?? "state query failed.")) { Before = before }, started);
                            if (includeRuntime) runtimeUpdates = ResetsFor(before.RuntimeEntries, request.FeatureId);
                            if (includeBoot) bootUpdates = ResetsFor(before.BootEntries, request.FeatureId);
                        }
                        else
                        {
                            var p = request.EffectiveSetPriority;
                            if (includeRuntime) runtimeUpdates = new[] { FeatureUpdateBuilder.CreateReset(request.FeatureId, p) };
                            if (includeBoot) bootUpdates = new[] { FeatureUpdateBuilder.CreateReset(request.FeatureId, p) };
                            // Skip stores where we know for certain there is nothing at that priority.
                            if (includeRuntime && before.RuntimeCaptured && before.RuntimeEntries.All(e => e.Priority != p)) runtimeUpdates = new RTL_FEATURE_CONFIGURATION_UPDATE[0];
                            if (includeBoot && before.BootCaptured && before.BootEntries.All(e => e.Priority != p)) bootUpdates = new RTL_FEATURE_CONFIGURATION_UPDATE[0];
                        }
                        if ((runtimeUpdates?.Length ?? 0) == 0 && (bootUpdates?.Length ?? 0) == 0)
                            return Finish(new FeatureOperationResult(request, FeatureOperationStatus.NoChange,
                                "The feature has no override to reset in the selected store(s).") { Before = before, After = QueryCore(request.FeatureId) }, started);
                        if (runtimeUpdates?.Length == 0) runtimeUpdates = null;
                        if (bootUpdates?.Length == 0) bootUpdates = null;
                        break;

                    case FeatureOperationKind.Restore:
                        var snapshot = request.RestoreSnapshot!;
                        if (includeRuntime) runtimeUpdates = RestoreUpdatesFor(snapshot.RuntimeEntries, request.FeatureId, request.RestorePriorities!);
                        if (includeBoot) bootUpdates = RestoreUpdatesFor(snapshot.BootEntries, request.FeatureId, request.RestorePriorities!);
                        break;
                }
            }
            catch (FeaturePropertyOverflowException ex)
            {
                return Finish(new FeatureOperationResult(request, FeatureOperationStatus.Rejected, ex.Message) { Before = before }, started);
            }

            var touched = (runtimeUpdates ?? Enumerable.Empty<RTL_FEATURE_CONFIGURATION_UPDATE>())
                .Concat(bootUpdates ?? Enumerable.Empty<RTL_FEATURE_CONFIGURATION_UPDATE>())
                .Select(u => u.Priority).Distinct().OrderBy(p => (uint)p).ToArray();

            StoreWriteResult write;
            try { write = _writer.Apply(runtimeUpdates, bootUpdates); }
            catch (Exception ex) when (IsNativeFailure(ex))
            {
                return Finish(new FeatureOperationResult(request, FeatureOperationStatus.Failed, "The feature store could not be written: " + ex.Message)
                { Before = before, PrioritiesTouched = touched }, started);
            }

            var warnings = new List<string>();
            foreach (var issue in write.BootStatusIssues)
                warnings.Add(DescribeBootIssue(issue));

            if (!write.Success)
            {
                var reason = write.ArgumentError ?? NtStatus.DescribeWithCode(write.Status);
                if (write.FailedStore == FeatureStore.Boot && runtimeUpdates != null)
                    warnings.Add("The runtime store was already changed before the boot store write failed.");
                return Finish(new FeatureOperationResult(request, FeatureOperationStatus.Failed,
                    $"Windows rejected the change to the {write.FailedStore?.ToString().ToLowerInvariant()} store: {reason}")
                {
                    Before = before,
                    NtStatusCode = write.Status,
                    FailedStore = write.FailedStore,
                    PrioritiesTouched = touched,
                    Warnings = warnings,
                    After = QueryCore(request.FeatureId),
                }, started);
            }

            // Never trust a zero status alone: re-read what was written.
            var verification = Verify(request.FeatureId, runtimeUpdates, bootUpdates);
            var after = QueryCore(request.FeatureId);
            var status = verification.Mismatch != null ? FeatureOperationStatus.Failed
                : verification.Complete ? FeatureOperationStatus.Succeeded
                : FeatureOperationStatus.SucceededUnverified;
            var message = status switch
            {
                FeatureOperationStatus.Succeeded => "Change applied and verified by re-querying the feature store.",
                FeatureOperationStatus.SucceededUnverified => "Windows reported success, but the new state could not be re-queried to confirm it.",
                _ => "Windows reported success, but the re-queried state does not match the request: " + verification.Mismatch,
            };
            return Finish(new FeatureOperationResult(request, status, message)
            {
                Before = before,
                After = after,
                PrioritiesTouched = touched,
                Warnings = warnings,
                RebootRequired = bootUpdates != null,
            }, started);
        }

        private static RTL_FEATURE_CONFIGURATION_UPDATE[] ResetsFor(IEnumerable<FeatureConfiguration> entries, uint featureId)
        {
            var list = new List<RTL_FEATURE_CONFIGURATION_UPDATE>();
            FeatureUpdateBuilder.AddResettables(entries, false, new[] { featureId }, null, list, new HashSet<string>());
            return list.ToArray();
        }

        private static RTL_FEATURE_CONFIGURATION_UPDATE[] RestoreUpdatesFor(
            IReadOnlyList<FeatureConfiguration> entries, uint featureId, IReadOnlyList<RTL_FEATURE_CONFIGURATION_PRIORITY> priorities) =>
            priorities.Distinct().Select(p =>
            {
                var previous = entries.FirstOrDefault(e => e.Priority == p);
                return previous != null ? previous.ToRestoreUpdate() : FeatureUpdateBuilder.CreateReset(featureId, p);
            }).ToArray();

        private (bool Complete, string? Mismatch) Verify(uint featureId, RTL_FEATURE_CONFIGURATION_UPDATE[]? runtimeUpdates, RTL_FEATURE_CONFIGURATION_UPDATE[]? bootUpdates)
        {
            var complete = true;
            var mismatches = new List<string>();
            try
            {
                if (runtimeUpdates != null)
                {
                    var runtime = _api.QueryAll(FeatureStore.Runtime)?.Where(c => c.FeatureId == featureId).ToArray();
                    if (runtime == null) complete = false;
                    else mismatches.AddRange(Compare(runtime, runtimeUpdates, "runtime"));
                }
                if (bootUpdates != null)
                {
                    var boot = _api.ReadBootOverrides(featureId);
                    if (!boot.Success) complete = false;
                    else mismatches.AddRange(Compare(boot.Entries, bootUpdates, "boot"));
                }
            }
            catch (Exception ex) when (IsNativeFailure(ex)) { complete = false; }
            return (complete, mismatches.Count > 0 ? string.Join("; ", mismatches) : null);
        }

        private static IEnumerable<string> Compare(IReadOnlyList<FeatureConfiguration> actual, IEnumerable<RTL_FEATURE_CONFIGURATION_UPDATE> expected, string storeName)
        {
            foreach (var update in expected)
            {
                var entry = actual.FirstOrDefault(c => c.Priority == update.Priority);
                if (update.Operation.HasFlag(RTL_FEATURE_CONFIGURATION_OPERATION.ResetState))
                {
                    if (entry != null)
                        yield return $"{storeName} store still has an override at priority {update.Priority}";
                }
                else if (entry == null)
                    yield return $"{storeName} store has no override at priority {update.Priority}";
                else if (entry.EnabledState != update.EnabledState)
                    yield return $"{storeName} store reports {entry.EnabledState} at priority {update.Priority}, expected {update.EnabledState}";
                else if (update.Priority != RTL_FEATURE_CONFIGURATION_PRIORITY.UserPolicy && entry.Variant != update.Variant)
                    yield return $"{storeName} store reports variant {entry.Variant} at priority {update.Priority}, expected {update.Variant}";
            }
        }

        private static string DescribeBootIssue(BootStatusIssue issue) => issue.Kind switch
        {
            BootStatusIssueKind.InitializeFailed => "Could not create the boot status data file: " + NtStatus.DescribeWithCode(issue.Status),
            BootStatusIssueKind.QueryFailed => "Could not query the Last Known Good status: " + NtStatus.DescribeWithCode(issue.Status),
            _ => $"Could not update the Last Known Good status (still {issue.CurrentState}): " + NtStatus.DescribeWithCode(issue.Status),
        };

        private FeatureOperationResult Finish(FeatureOperationResult result, DateTimeOffset started) =>
            result with { StartedAtUtc = started, CompletedAtUtc = _clock(), OsBuild = _api.OsBuild };
        #endregion

        private async Task<T> RunGatedAsync<T>(Func<T> work, CancellationToken cancellationToken)
        {
            await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                return await Task.Run(work, CancellationToken.None).ConfigureAwait(false);
            }
            finally { _gate.Release(); }
        }

        private static bool IsNativeFailure(Exception ex) =>
            ex is DllNotFoundException or EntryPointNotFoundException or UnauthorizedAccessException
               or System.Security.SecurityException or System.IO.IOException or System.ComponentModel.Win32Exception;
    }
}
