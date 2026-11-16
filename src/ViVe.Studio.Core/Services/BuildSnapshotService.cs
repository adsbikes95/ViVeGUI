// SPDX-License-Identifier: GPL-3.0-or-later
using System.Text.Json;
using System.Text.Json.Serialization;

namespace ViVe.Studio.Services;

/// <summary>The feature IDs configured in the runtime store, as first seen on one Windows build.</summary>
public sealed record FeatureStoreSnapshot
{
    [JsonPropertyName("buildText")] public string BuildText { get; init; } = string.Empty;
    [JsonPropertyName("build")] public int Build { get; init; }
    [JsonPropertyName("capturedAtUtc")] public DateTimeOffset CapturedAtUtc { get; init; }
    [JsonPropertyName("configuredIds")] public IReadOnlyList<uint> ConfiguredIds { get; init; } = Array.Empty<uint>();
}

/// <summary>What changed in the feature store between the previous build and this one.</summary>
public sealed record BuildDiff(FeatureStoreSnapshot Previous, FeatureStoreSnapshot Current, IReadOnlyList<uint> Added, IReadOnlyList<uint> Removed);

/// <summary>
/// Keeps one snapshot of the configured feature IDs per Windows build (the first time a build is seen, which is
/// right after an update), so new IDs that arrive with an update can be flagged. Read-only towards Windows.
/// </summary>
public sealed class BuildSnapshotService
{
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = false, MaxDepth = 8 };
    private readonly IAppPaths _paths;
    private readonly Func<DateTimeOffset> _clock;

    public BuildSnapshotService(IAppPaths paths, Func<DateTimeOffset>? clock = null)
    {
        _paths = paths;
        _clock = clock ?? (() => DateTimeOffset.UtcNow);
    }

    public string Directory => Path.Combine(_paths.DataDirectory, "snapshots");

    /// <summary>Saves a snapshot for this build unless one exists already; returns the stored snapshot.</summary>
    public async Task<FeatureStoreSnapshot> RecordAsync(string buildText, int build, IReadOnlyList<uint> configuredIds)
    {
        var path = PathFor(buildText);
        if (TryLoad(path) is { } existing) return existing;
        var snapshot = new FeatureStoreSnapshot
        {
            BuildText = buildText,
            Build = build,
            CapturedAtUtc = _clock(),
            ConfiguredIds = configuredIds.Distinct().OrderBy(i => i).ToArray(),
        };
        System.IO.Directory.CreateDirectory(Directory);
        var temp = path + $".{Guid.NewGuid():N}.tmp";
        try
        {
            await using (var s = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                await JsonSerializer.SerializeAsync(s, snapshot, Json).ConfigureAwait(false);
            File.Move(temp, path, overwrite: true);
        }
        finally { if (File.Exists(temp)) File.Delete(temp); }
        return snapshot;
    }

    public IReadOnlyList<FeatureStoreSnapshot> List()
    {
        if (!System.IO.Directory.Exists(Directory)) return Array.Empty<FeatureStoreSnapshot>();
        return System.IO.Directory.EnumerateFiles(Directory, "*.json").Select(TryLoad).Where(s => s != null).Select(s => s!)
            .OrderBy(s => s.CapturedAtUtc).ToList();
    }

    /// <summary>Compares a build's snapshot with the most recently captured snapshot of a different build.</summary>
    public BuildDiff? CompareWithPreviousBuild(FeatureStoreSnapshot current)
    {
        var previous = List().Where(s => s.BuildText != current.BuildText && s.CapturedAtUtc <= current.CapturedAtUtc)
            .OrderBy(s => s.CapturedAtUtc).LastOrDefault();
        if (previous == null) return null;
        var before = previous.ConfiguredIds.ToHashSet();
        var after = current.ConfiguredIds.ToHashSet();
        return new BuildDiff(previous, current,
            current.ConfiguredIds.Where(i => !before.Contains(i)).ToList(),
            previous.ConfiguredIds.Where(i => !after.Contains(i)).ToList());
    }

    private string PathFor(string buildText) =>
        Path.Combine(Directory, string.Concat(buildText.Select(c => char.IsAsciiLetterOrDigit(c) || c == '.' ? c : '_')) + ".json");

    private static FeatureStoreSnapshot? TryLoad(string path)
    {
        try
        {
            if (!File.Exists(path)) return null;
            using var s = File.OpenRead(path);
            return JsonSerializer.Deserialize<FeatureStoreSnapshot>(s, Json);
        }
        catch (Exception ex) when (ex is JsonException or IOException) { return null; }
    }
}
