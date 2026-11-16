// SPDX-License-Identifier: GPL-3.0-or-later
using System.Globalization;
using Albacore.ViVe.NativeEnums;
using ViVe.Core;

namespace ViVe.Studio.ViewModels;

public enum StatusSeverity { None, Info, Success, Warning, Error }

public sealed record PriorityOption(RTL_FEATURE_CONFIGURATION_PRIORITY Priority, string Display)
{
    public static IReadOnlyList<PriorityOption> Writable { get; } = FeaturePriorities.Mutable
        .Select(p => new PriorityOption(p, p == FeaturePriorities.Default
            ? $"{p} ({(int)p}) - default"
            : $"{p} ({(int)p}) - higher risk"))
        .ToArray();

    public override string ToString() => Display;
}

public sealed record StoreOption(FeatureStores Stores, string Display)
{
    public static IReadOnlyList<StoreOption> All { get; } = new[]
    {
        new StoreOption(FeatureStores.Both, "Runtime and boot (ViVeTool default)"),
        new StoreOption(FeatureStores.Runtime, "Runtime only (lost at restart)"),
        new StoreOption(FeatureStores.Boot, "Boot only (applies after restart)"),
    };

    public static StoreOption For(FeatureStores stores) => All.FirstOrDefault(o => o.Stores == stores) ?? All[0];

    public override string ToString() => Display;
}

internal static class Format
{
    public static bool TryParseNumber(string? text, out uint value)
    {
        value = 0;
        text = text?.Trim();
        if (string.IsNullOrEmpty(text)) return true; // empty = 0
        return text.StartsWith("0x", StringComparison.OrdinalIgnoreCase)
            ? uint.TryParse(text.AsSpan(2), NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out value)
            : uint.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out value);
    }

    public static string Stores(FeatureStores stores) => stores switch
    {
        FeatureStores.Both => "Runtime + Boot",
        FeatureStores.Runtime => "Runtime",
        FeatureStores.Boot => "Boot",
        _ => "None",
    };

    public static string Entries(IReadOnlyList<FeatureConfiguration> entries) =>
        entries.Count == 0 ? "none" : string.Join("; ", entries.Select(e => e.DescribeState()));

    public static StatusSeverity Severity(FeatureOperationStatus status) => status switch
    {
        FeatureOperationStatus.Succeeded => StatusSeverity.Success,
        FeatureOperationStatus.NoChange => StatusSeverity.Info,
        FeatureOperationStatus.SucceededUnverified => StatusSeverity.Warning,
        _ => StatusSeverity.Error,
    };

    public static string Status(FeatureOperationStatus status) => status switch
    {
        FeatureOperationStatus.Succeeded => "Succeeded (verified)",
        FeatureOperationStatus.SucceededUnverified => "Reported success (not verified)",
        FeatureOperationStatus.NoChange => "No change needed",
        FeatureOperationStatus.Rejected => "Rejected (nothing changed)",
        _ => "Failed",
    };
}
