// SPDX-License-Identifier: GPL-3.0-or-later
using System.Text.Json.Serialization;

namespace ViVe.FeatureCatalog;

/// <summary>How well a catalog entry's metadata has been confirmed.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<VerificationStatus>))]
public enum VerificationStatus
{
    /// <summary>Nothing is known beyond the ID.</summary>
    Unknown,
    /// <summary>Only the internal feature name is known (for example from the ViVe name dictionary).</summary>
    NameOnly,
    /// <summary>Described by a cited community source but not independently confirmed.</summary>
    CommunityReported,
    /// <summary>Confirmed on the listed builds by the cited source.</summary>
    Verified,
}

/// <summary>A build range a source reports the feature for. Presence on a build does not imply it is safe to change.</summary>
public sealed record BuildCompatibility
{
    [JsonPropertyName("minBuild")] public int? MinBuild { get; init; }
    [JsonPropertyName("maxBuild")] public int? MaxBuild { get; init; }
    [JsonPropertyName("note")] public string? Note { get; init; }
    [JsonPropertyName("source")] public string? Source { get; init; }

    public bool Contains(int build) => (MinBuild is null || build >= MinBuild) && (MaxBuild is null || build <= MaxBuild);
}

/// <summary>Metadata about one feature ID. This is never feature state.</summary>
public sealed record FeatureCatalogEntry
{
    [JsonPropertyName("id")] public uint Id { get; init; }
    [JsonPropertyName("name")] public string? Name { get; init; }
    [JsonPropertyName("description")] public string? Description { get; init; }
    [JsonPropertyName("category")] public string? Category { get; init; }
    [JsonPropertyName("builds")] public IReadOnlyList<BuildCompatibility> Builds { get; init; } = Array.Empty<BuildCompatibility>();
    /// <summary>Where the ID/metadata came from (citation or URL). Required for anything above NameOnly.</summary>
    [JsonPropertyName("source")] public string? Source { get; init; }
    [JsonPropertyName("verificationStatus")] public VerificationStatus VerificationStatus { get; init; } = VerificationStatus.Unknown;

    /// <summary>
    /// Compatibility of this entry with a build: true/false only when verified build data exists, otherwise null (unknown).
    /// </summary>
    public bool? IsCompatibleWith(int build)
    {
        if (VerificationStatus != VerificationStatus.Verified || Builds.Count == 0 || build <= 0)
            return null;
        return Builds.Any(b => b.Contains(build));
    }
}

/// <summary>A catalog JSON document (see docs/feature-catalog.schema.json).</summary>
public sealed record FeatureCatalogDocument
{
    public const int CurrentSchemaVersion = 1;

    [JsonPropertyName("schemaVersion")] public int SchemaVersion { get; init; } = CurrentSchemaVersion;
    [JsonPropertyName("catalogVersion")] public string? CatalogVersion { get; init; }
    [JsonPropertyName("description")] public string? Description { get; init; }
    [JsonPropertyName("features")] public IReadOnlyList<FeatureCatalogEntry> Features { get; init; } = Array.Empty<FeatureCatalogEntry>();
}

/// <summary>Problems found while loading a catalog. Invalid entries are skipped, never guessed.</summary>
public sealed record CatalogLoadResult(IReadOnlyList<FeatureCatalogEntry> Entries, IReadOnlyList<string> Errors, IReadOnlyList<string> Warnings)
{
    public bool HasErrors => Errors.Count > 0;
}
