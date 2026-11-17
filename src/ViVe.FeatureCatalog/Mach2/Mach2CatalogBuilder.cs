// SPDX-License-Identifier: GPL-3.0-or-later
using System.Globalization;

namespace ViVe.FeatureCatalog.Mach2;

/// <summary>What an import produced, for display and logging.</summary>
public sealed record Mach2ImportReport(
    int ListCount,
    int FeatureCount,
    int SkippedLines,
    int? OldestBuild,
    int? NewestBuild,
    IReadOnlyList<string> Warnings);

/// <summary>
/// Turns parsed mach2 dumps into a catalog document. What it records is only what the dumps show:
/// the internal name of each ID, the dumped builds it appeared in, and mach2's staging section.
/// Descriptions and categories stay empty, because mach2 does not provide them.
/// </summary>
public sealed class Mach2CatalogBuilder
{
    private const int MaxAliases = 8;
    private readonly List<Mach2ParseResult> _lists = new();

    public void Add(Mach2ParseResult list) => _lists.Add(list);

    public int ListCount => _lists.Count;

    public (FeatureCatalogDocument Document, Mach2ImportReport Report) Build(DateTimeOffset importedAtUtc)
    {
        var ordered = _lists.OrderBy(l => l.Info.Build).ThenBy(l => l.Info.Revision).ThenBy(l => l.Info.RelativePath, StringComparer.Ordinal).ToList();
        var dumpedBuilds = ordered.Select(l => l.Info.Build).Distinct().OrderBy(b => b).ToList();

        // Per ID: every sighting, oldest dump first.
        var sightings = new Dictionary<uint, List<(Mach2ListInfo Info, Mach2FeatureRecord Record)>>();
        foreach (var list in ordered)
            foreach (var f in list.Features)
            {
                if (!sightings.TryGetValue(f.Id, out var s)) sightings[f.Id] = s = new();
                s.Add((list.Info, f));
            }

        var entries = new List<FeatureCatalogEntry>(sightings.Count);
        foreach (var (id, seen) in sightings.OrderBy(kv => kv.Key))
        {
            var latest = seen[^1];
            var aliases = seen.Select(s => s.Record.Name)
                .Where(n => !string.Equals(n, latest.Record.Name, StringComparison.OrdinalIgnoreCase))
                .Distinct(StringComparer.OrdinalIgnoreCase).Reverse().Take(MaxAliases).ToArray();
            entries.Add(new FeatureCatalogEntry
            {
                Id = id,
                Name = latest.Record.Name,
                Aliases = aliases,
                Source = Mach2Source.Citation,
                VerificationStatus = VerificationStatus.Verified,
                Builds = BuildRuns(seen, dumpedBuilds),
            });
        }

        var warnings = ordered.SelectMany(l => l.Warnings).Take(50).ToList();
        var document = new FeatureCatalogDocument
        {
            SchemaVersion = FeatureCatalogDocument.CurrentSchemaVersion,
            CatalogVersion = $"mach2@{Mach2Source.ShortCommit}",
            Description =
                $"Imported {importedAtUtc.UtcDateTime.ToString("u", CultureInfo.InvariantCulture)} from {ordered.Count} feature dumps by {Mach2Source.Citation}. " +
                "Names and IDs come from Windows debug symbols as dumped by mach2; build ranges list only the dumped builds an ID appeared in. " +
                $"mach2 stopped publishing dumps on {Mach2Source.PinnedCommitDate}; builds newer than {(dumpedBuilds.Count > 0 ? dumpedBuilds[^1] : 0)} are not covered. " +
                "Descriptions and categories are not provided by mach2 and are left empty.",
            Features = entries,
        };
        var report = new Mach2ImportReport(ordered.Count, entries.Count, ordered.Sum(l => l.SkippedLines),
            dumpedBuilds.Count > 0 ? dumpedBuilds[0] : null, dumpedBuilds.Count > 0 ? dumpedBuilds[^1] : null, warnings);
        return (document, report);
    }

    /// <summary>
    /// Groups sightings into runs of consecutive dumped builds. A run never spans a dumped build the ID was
    /// missing from, so a range only claims what the dumps show (builds between two dumps were not checked).
    /// </summary>
    private static IReadOnlyList<BuildCompatibility> BuildRuns(List<(Mach2ListInfo Info, Mach2FeatureRecord Record)> seen, List<int> dumpedBuilds)
    {
        var byBuild = seen.GroupBy(s => s.Info.Build).ToDictionary(g => g.Key, g => g.ToList());
        var runs = new List<BuildCompatibility>();
        int? start = null;
        var dumps = 0;
        Mach2ListInfo? lastInfo = null;
        Mach2FeatureRecord? lastRecord = null;
        for (var i = 0; i <= dumpedBuilds.Count; i++)
        {
            var present = i < dumpedBuilds.Count && byBuild.ContainsKey(dumpedBuilds[i]);
            if (present)
            {
                var group = byBuild[dumpedBuilds[i]];
                start ??= dumpedBuilds[i];
                dumps += group.Count;
                (lastInfo, lastRecord) = group[^1];
                continue;
            }
            if (start is int min && lastInfo != null && lastRecord != null)
            {
                var max = lastInfo.Build;
                var range = min == max ? $"build {min}" : $"builds {min} to {max}";
                var staging = lastRecord.Staging is null ? "not recorded in this dump" : $"\"{lastRecord.Staging}\"";
                runs.Add(new BuildCompatibility
                {
                    MinBuild = min,
                    MaxBuild = max,
                    Note = $"Listed in every mach2 dump for {range} ({dumps} dump(s); builds between dumps were not checked). " +
                           $"mach2 staging in the latest of these ({lastInfo.BuildText}, {lastInfo.Branch}/{lastInfo.Architecture}): {staging}.",
                    Source = lastInfo.RelativePath,
                });
                start = null;
                dumps = 0;
            }
        }
        return runs;
    }
}
