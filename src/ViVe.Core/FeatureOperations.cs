// SPDX-License-Identifier: GPL-3.0-or-later
using System;
using System.Collections.Generic;
using System.Linq;
using Albacore.ViVe.NativeEnums;

namespace ViVe.Core
{
    public enum FeatureOperationKind
    {
        Enable,
        Disable,
        Reset,
        /// <summary>Restores a previously captured <see cref="FeatureSnapshot"/> (rollback).</summary>
        Restore,
    }

    public enum OperationRisk
    {
        Standard,
        High,
    }

    /// <summary>A requested change to one feature.</summary>
    public sealed record FeatureOperationRequest(FeatureOperationKind Kind, uint FeatureId)
    {
        public FeatureStores Stores { get; init; } = FeatureStores.Both;

        /// <summary>
        /// Enable/Disable: the priority to write (default User).
        /// Reset: the priority to reset; null with <see cref="ResetAllPriorities"/> resets every mutable priority found (ViVeTool /reset behaviour).
        /// </summary>
        public RTL_FEATURE_CONFIGURATION_PRIORITY? Priority { get; init; }

        public bool ResetAllPriorities { get; init; }

        public uint Variant { get; init; }
        public RTL_FEATURE_VARIANT_PAYLOAD_KIND VariantPayloadKind { get; init; }
        public uint VariantPayload { get; init; }

        /// <summary>Marks the configuration as a Windows experiment (ViVeTool /experiment).</summary>
        public bool Experiment { get; init; }

        /// <summary>Restore only: the snapshot to restore.</summary>
        public FeatureSnapshot? RestoreSnapshot { get; init; }

        /// <summary>Restore only: the priorities the original operation touched.</summary>
        public IReadOnlyList<RTL_FEATURE_CONFIGURATION_PRIORITY>? RestorePriorities { get; init; }

        public RTL_FEATURE_CONFIGURATION_PRIORITY EffectiveSetPriority => Priority ?? FeaturePriorities.Default;

        public string Describe()
        {
            var stores = Stores == FeatureStores.Both ? "runtime and boot stores" : Stores == FeatureStores.Runtime ? "runtime store" : "boot store";
            switch (Kind)
            {
                case FeatureOperationKind.Enable:
                case FeatureOperationKind.Disable:
                    var extras = new List<string>();
                    if (Variant != 0) extras.Add($"variant {Variant}");
                    if (VariantPayloadKind != RTL_FEATURE_VARIANT_PAYLOAD_KIND.None || VariantPayload != 0) extras.Add($"payload {VariantPayloadKind}:{VariantPayload}");
                    if (Experiment) extras.Add("as experiment");
                    return $"{Kind} feature {FeatureId} at priority {EffectiveSetPriority} in the {stores}" + (extras.Count > 0 ? $" ({string.Join(", ", extras)})" : string.Empty);
                case FeatureOperationKind.Reset:
                    return ResetAllPriorities
                        ? $"Reset feature {FeatureId} at every writable priority in the {stores}"
                        : $"Reset feature {FeatureId} at priority {EffectiveSetPriority} in the {stores}";
                case FeatureOperationKind.Restore:
                    var priorities = RestorePriorities is { Count: > 0 } ? string.Join(", ", RestorePriorities) : "none";
                    return $"Restore feature {FeatureId} to its state captured at {RestoreSnapshot?.CapturedAtUtc.ToLocalTime():g} (priorities: {priorities}) in the {stores}";
                default:
                    return $"{Kind} feature {FeatureId}";
            }
        }
    }

    /// <summary>
    /// Feature-state snapshot captured before a modification. This is NOT a system backup:
    /// it only records the override entries for one feature at writable priorities.
    /// </summary>
    public sealed record FeatureSnapshot(
        uint FeatureId,
        DateTimeOffset CapturedAtUtc,
        int OsBuild,
        bool RuntimeCaptured,
        IReadOnlyList<FeatureConfiguration> RuntimeEntries,
        bool BootCaptured,
        IReadOnlyList<FeatureConfiguration> BootEntries,
        string? CaptureError = null)
    {
        /// <summary>True when every store in <paramref name="stores"/> was captured reliably.</summary>
        public bool IsCompleteFor(FeatureStores stores) =>
            (!stores.Includes(FeatureStore.Runtime) || RuntimeCaptured) &&
            (!stores.Includes(FeatureStore.Boot) || BootCaptured);

        public IReadOnlyList<FeatureConfiguration> EntriesFor(FeatureStore store) =>
            store == FeatureStore.Runtime ? RuntimeEntries : BootEntries;
    }

    /// <summary>Validated, previewable form of a request.</summary>
    public sealed record FeatureOperationPlan(
        FeatureOperationRequest Request,
        IReadOnlyList<string> ValidationErrors,
        IReadOnlyList<string> Warnings,
        OperationRisk Risk,
        string Summary)
    {
        public bool IsValid => ValidationErrors.Count == 0;
    }

    public enum FeatureOperationStatus
    {
        /// <summary>Native call returned success and a re-query confirmed the new state.</summary>
        Succeeded,
        /// <summary>Native call returned success but the new state could not be re-queried.</summary>
        SucceededUnverified,
        /// <summary>There was nothing to change (for example, resetting a feature without overrides).</summary>
        NoChange,
        /// <summary>The request was rejected before anything was written (validation, unsupported platform).</summary>
        Rejected,
        /// <summary>The native call failed, or the re-query showed the change did not take effect.</summary>
        Failed,
    }

    /// <summary>Structured outcome of an operation. Always inspect <see cref="Status"/>, never assume success.</summary>
    public sealed record FeatureOperationResult(
        FeatureOperationRequest Request,
        FeatureOperationStatus Status,
        string Message)
    {
        public DateTimeOffset StartedAtUtc { get; init; }
        public DateTimeOffset CompletedAtUtc { get; init; }
        public int OsBuild { get; init; }
        public int? NtStatusCode { get; init; }
        public FeatureStore? FailedStore { get; init; }
        public FeatureSnapshot? Before { get; init; }
        public FeatureQueryResult? After { get; init; }
        public IReadOnlyList<RTL_FEATURE_CONFIGURATION_PRIORITY> PrioritiesTouched { get; init; } = new RTL_FEATURE_CONFIGURATION_PRIORITY[0];
        public IReadOnlyList<string> Warnings { get; init; } = new string[0];
        public bool RebootRequired { get; init; }

        public bool IsSuccess => Status is FeatureOperationStatus.Succeeded or FeatureOperationStatus.SucceededUnverified or FeatureOperationStatus.NoChange;
        public bool ChangedState => Status is FeatureOperationStatus.Succeeded or FeatureOperationStatus.SucceededUnverified;
    }

    /// <summary>Current state of one feature.</summary>
    public sealed record FeatureQueryResult(uint FeatureId, bool Success, string? Error)
    {
        /// <summary>The effective (winning) runtime configuration, or null when the feature has no configuration.</summary>
        public FeatureConfiguration? Effective { get; init; }
        public IReadOnlyList<FeatureConfiguration> RuntimeEntries { get; init; } = new FeatureConfiguration[0];
        public bool BootEntriesAvailable { get; init; }
        public IReadOnlyList<FeatureConfiguration> BootEntries { get; init; } = new FeatureConfiguration[0];
        public DateTimeOffset QueriedAtUtc { get; init; }

        public bool HasAnyConfiguration => Effective != null || RuntimeEntries.Count > 0 || BootEntries.Count > 0;

        public string StateSummary =>
            !Success ? "Unknown (query failed)" :
            Effective == null ? "Default (no configuration)" :
            $"{Effective.EnabledState} (priority {Effective.Priority})";
    }

    /// <summary>System-wide feature-management status for the dashboard.</summary>
    public sealed record FeatureSystemStatus(
        bool PlatformSupported,
        int OsBuild,
        ulong? ChangeStamp,
        BSD_FEATURE_CONFIGURATION_STATE? BootStatus,
        string? BootStatusError,
        int? RuntimeConfigurationCount,
        int? RuntimeUserOverrideCount);

    internal static class Lists
    {
        public static IReadOnlyList<T> Empty<T>() => new T[0];
        public static IReadOnlyList<T> ToReadOnly<T>(this IEnumerable<T> items) => items.ToArray();
    }
}
