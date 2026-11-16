// SPDX-License-Identifier: GPL-3.0-or-later
using ViVe.Core;
using ViVe.FeatureCatalog;

namespace ViVe.Studio.ViewModels;

/// <summary>One row in the Feature Explorer results list.</summary>
public sealed record FeatureResultItem(
    uint FeatureId,
    string? Name,
    string Headline,
    string StateText,
    string Details,
    StatusSeverity Severity,
    DateTimeOffset Timestamp)
{
    public string Title => Name is null ? FeatureId.ToString(System.Globalization.CultureInfo.InvariantCulture) : $"{FeatureId}  ·  {Name}";
    public string TimeText => Timestamp.ToLocalTime().ToString("T", System.Globalization.CultureInfo.CurrentCulture);

    public static FeatureResultItem FromQuery(FeatureQueryResult query, FeatureCatalogEntry? meta, int build)
    {
        var details = new List<string>();
        if (query.Success)
        {
            details.Add("Runtime store: " + Format.Entries(query.RuntimeEntries));
            details.Add(query.BootEntriesAvailable ? "Boot store (persisted): " + Format.Entries(query.BootEntries) : "Boot store: could not be read");
        }
        else details.Add(query.Error ?? "Query failed.");
        details.Add(CatalogLine(meta, build));
        return new FeatureResultItem(query.FeatureId, meta?.Name, "Query", query.StateSummary, string.Join(Environment.NewLine, details),
            query.Success ? StatusSeverity.Info : StatusSeverity.Error, query.QueriedAtUtc == default ? DateTimeOffset.UtcNow : query.QueriedAtUtc);
    }

    public static FeatureResultItem FromOperation(FeatureOperationResult result, FeatureCatalogEntry? meta, int build)
    {
        var details = new List<string> { result.Message };
        if (result.Before != null)
            details.Add("Before (runtime): " + (result.Before.RuntimeCaptured ? Format.Entries(result.Before.RuntimeEntries) : "not captured"));
        if (result.After != null && result.After.Success)
            details.Add("After (runtime): " + Format.Entries(result.After.RuntimeEntries));
        if (result.NtStatusCode is int code)
            details.Add("Status code: " + NtStatus.ToHex(code));
        details.AddRange(result.Warnings.Select(w => "Warning: " + w));
        if (result.RebootRequired)
            details.Add("Restart Windows for the boot store change to take full effect.");
        details.Add(CatalogLine(meta, build));
        return new FeatureResultItem(result.Request.FeatureId, meta?.Name, result.Request.Kind.ToString(), Format.Status(result.Status),
            string.Join(Environment.NewLine, details), Format.Severity(result.Status), result.CompletedAtUtc == default ? DateTimeOffset.UtcNow : result.CompletedAtUtc);
    }

    internal static string CatalogLine(FeatureCatalogEntry? meta, int build)
    {
        if (meta == null) return "Catalog: unknown feature ID (no metadata). Compatibility unknown.";
        var compat = meta.IsCompatibleWith(build) switch
        {
            true => $"listed as present on build {build} by its source",
            false => $"not listed for build {build} by its source",
            null => "compatibility unknown",
        };
        var source = meta.Source is null ? string.Empty : $" Source: {meta.Source}.";
        return $"Catalog: {meta.VerificationStatus}; {compat}.{source}";
    }
}
