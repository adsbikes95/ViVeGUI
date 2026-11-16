// SPDX-License-Identifier: GPL-3.0-or-later
using Albacore.ViVe.NativeEnums;
using ViVe.Core;

namespace ViVe.Studio.Models;

/// <summary>One auditable record of a requested feature operation and its result.</summary>
public sealed record ChangeHistoryEntry
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public DateTimeOffset TimestampUtc { get; init; }
    public int OsBuild { get; init; }
    public string? OsDisplayVersion { get; init; }

    public uint FeatureId { get; init; }
    public string? FeatureName { get; init; }

    public FeatureOperationKind Operation { get; init; }
    public FeatureStores Stores { get; init; }
    public RTL_FEATURE_CONFIGURATION_PRIORITY? Priority { get; init; }
    public bool ResetAllPriorities { get; init; }
    public uint Variant { get; init; }
    public RTL_FEATURE_VARIANT_PAYLOAD_KIND VariantPayloadKind { get; init; }
    public uint VariantPayload { get; init; }
    public bool Experiment { get; init; }

    public FeatureOperationStatus Status { get; init; }
    public string Message { get; init; } = string.Empty;
    public string? NtStatus { get; init; }
    public IReadOnlyList<string> Warnings { get; init; } = Array.Empty<string>();
    public bool RebootRequired { get; init; }

    /// <summary>Feature state captured before the change (not a system backup).</summary>
    public FeatureSnapshot? Before { get; init; }
    public IReadOnlyList<FeatureConfiguration>? AfterRuntimeEntries { get; init; }
    public IReadOnlyList<RTL_FEATURE_CONFIGURATION_PRIORITY> PrioritiesTouched { get; init; } = Array.Empty<RTL_FEATURE_CONFIGURATION_PRIORITY>();

    /// <summary>Set on a Restore entry: the entry it rolled back.</summary>
    public Guid? RollbackOf { get; init; }
    /// <summary>Set on an original entry once it has been rolled back successfully.</summary>
    public Guid? RolledBackBy { get; init; }

    public bool ChangedState => Status is FeatureOperationStatus.Succeeded or FeatureOperationStatus.SucceededUnverified;

    public static ChangeHistoryEntry FromResult(FeatureOperationResult result, string? featureName, string? osDisplayVersion, Guid? rollbackOf = null)
    {
        var request = result.Request;
        return new ChangeHistoryEntry
        {
            TimestampUtc = result.CompletedAtUtc == default ? DateTimeOffset.UtcNow : result.CompletedAtUtc,
            OsBuild = result.OsBuild,
            OsDisplayVersion = osDisplayVersion,
            FeatureId = request.FeatureId,
            FeatureName = featureName,
            Operation = request.Kind,
            Stores = request.Stores,
            Priority = request.Kind is FeatureOperationKind.Enable or FeatureOperationKind.Disable or FeatureOperationKind.Reset && !request.ResetAllPriorities
                ? request.EffectiveSetPriority : null,
            ResetAllPriorities = request.ResetAllPriorities,
            Variant = request.Variant,
            VariantPayloadKind = request.VariantPayloadKind,
            VariantPayload = request.VariantPayload,
            Experiment = request.Experiment,
            Status = result.Status,
            Message = result.Message,
            NtStatus = result.NtStatusCode is int code ? global::ViVe.Core.NtStatus.ToHex(code) : null,
            Warnings = result.Warnings,
            RebootRequired = result.RebootRequired,
            Before = result.Before,
            AfterRuntimeEntries = result.After?.Success == true ? result.After.RuntimeEntries : null,
            PrioritiesTouched = result.PrioritiesTouched,
            RollbackOf = rollbackOf,
        };
    }
}

/// <summary>On-disk/exported history document.</summary>
public sealed record ChangeHistoryDocument
{
    public const int CurrentSchemaVersion = 1;
    public int SchemaVersion { get; init; } = CurrentSchemaVersion;
    public string Application { get; init; } = "ViVeTool Studio";
    public DateTimeOffset? ExportedAtUtc { get; init; }
    public IReadOnlyList<ChangeHistoryEntry> Entries { get; init; } = Array.Empty<ChangeHistoryEntry>();
}
