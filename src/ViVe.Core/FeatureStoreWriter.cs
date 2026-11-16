// SPDX-License-Identifier: GPL-3.0-or-later
using System;
using System.Collections.Generic;
using Albacore.ViVe.NativeEnums;
using Albacore.ViVe.NativeStructs;

namespace ViVe.Core
{
    public enum BootStatusIssueKind
    {
        /// <summary>The boot status data file did not exist and could not be created.</summary>
        InitializeFailed,
        /// <summary>The current LKG/boot status could not be queried.</summary>
        QueryFailed,
        /// <summary>The boot status could not be updated.</summary>
        UpdateFailed,
    }

    /// <summary>A non-fatal problem updating the Last Known Good boot status after a boot-store write.</summary>
    public sealed record BootStatusIssue(BootStatusIssueKind Kind, int Status, BSD_FEATURE_CONFIGURATION_STATE CurrentState);

    /// <summary>Outcome of writing update batches to the runtime and/or boot store.</summary>
    public sealed record StoreWriteResult(
        bool Success,
        FeatureStore? FailedStore,
        int Status,
        string? ArgumentError,
        bool BootStoreWritten,
        IReadOnlyList<BootStatusIssue> BootStatusIssues);

    /// <summary>
    /// Writes updates to the feature stores in the same order and with the same LKG handling as
    /// ViVeTool's original FinalizeSet/UpdateLKGStatus. Shared by the CLI and the GUI engine.
    /// </summary>
    public sealed class FeatureStoreWriter
    {
        private readonly IFeatureConfigurationApi _api;

        public FeatureStoreWriter(IFeatureConfigurationApi api) => _api = api ?? throw new ArgumentNullException(nameof(api));

        /// <summary>
        /// Applies <paramref name="runtimeUpdates"/> (if not null) then <paramref name="bootUpdates"/> (if not null).
        /// Stops at the first store that fails. After a successful boot write the LKG status is set to BootPending.
        /// </summary>
        public StoreWriteResult Apply(IReadOnlyList<RTL_FEATURE_CONFIGURATION_UPDATE>? runtimeUpdates, IReadOnlyList<RTL_FEATURE_CONFIGURATION_UPDATE>? bootUpdates)
        {
            var noIssues = new BootStatusIssue[0];
            if (runtimeUpdates != null)
            {
                int result;
                try { result = _api.SetConfigurations(runtimeUpdates, FeatureStore.Runtime); }
                catch (ArgumentException ae) { return new StoreWriteResult(false, FeatureStore.Runtime, NtStatus.InvalidParameter, ae.Message, false, noIssues); }
                if (result != NtStatus.Success)
                    return new StoreWriteResult(false, FeatureStore.Runtime, result, null, false, noIssues);
            }
            if (bootUpdates != null)
            {
                int result;
                try { result = _api.SetConfigurations(bootUpdates, FeatureStore.Boot); }
                catch (ArgumentException ae) { return new StoreWriteResult(false, FeatureStore.Boot, NtStatus.InvalidParameter, ae.Message, false, noIssues); }
                if (result != NtStatus.Success)
                    return new StoreWriteResult(false, FeatureStore.Boot, result, null, false, noIssues);

                var issues = UpdateBootStatus(BSD_FEATURE_CONFIGURATION_STATE.BootPending);
                return new StoreWriteResult(true, null, NtStatus.Success, null, true, issues);
            }
            return new StoreWriteResult(true, null, NtStatus.Success, null, false, noIssues);
        }

        /// <summary>Sets the Last Known Good boot status, creating the boot status data file if needed.</summary>
        public IReadOnlyList<BootStatusIssue> UpdateBootStatus(BSD_FEATURE_CONFIGURATION_STATE newStatus)
        {
            var issues = new List<BootStatusIssue>();
            var result = _api.GetBootStatus(out var currentStatus);
            if (result != NtStatus.Success)
            {
                if (result == NtStatus.ObjectNameNotFound)
                {
                    result = _api.InitializeBootStatusDataFile();
                    if (result != NtStatus.Success)
                    {
                        issues.Add(new BootStatusIssue(BootStatusIssueKind.InitializeFailed, result, currentStatus));
                        return issues;
                    }
                }
                else
                {
                    issues.Add(new BootStatusIssue(BootStatusIssueKind.QueryFailed, result, currentStatus));
                }
            }

            if (currentStatus != newStatus)
            {
                result = _api.SetBootStatus(newStatus);
                if (result != NtStatus.Success)
                    issues.Add(new BootStatusIssue(BootStatusIssueKind.UpdateFailed, result, currentStatus));
            }
            return issues;
        }
    }
}
