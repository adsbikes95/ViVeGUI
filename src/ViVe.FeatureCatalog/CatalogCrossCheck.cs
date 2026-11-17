// SPDX-License-Identifier: GPL-3.0-or-later
namespace ViVe.FeatureCatalog;

/// <summary>
/// How the feature IDs configured on this PC compare with the catalog. Only the runtime store's
/// configured IDs can be listed (the "ViVeTool /query" view), so this is a cross-check, not a full inventory.
/// </summary>
public sealed record CatalogCrossCheckResult(
    int Build,
    int ConfiguredCount,
    int NamedCount,
    int ListedForThisBuildCount,
    int ListedOnlyForOtherBuildsCount,
    int UnnamedCount,
    IReadOnlyList<uint> UnnamedSample)
{
    public string Summary
    {
        get
        {
            if (ConfiguredCount == 0) return "No configured features were found in this PC's runtime store.";
            var text = $"{ConfiguredCount:N0} feature IDs are configured on this PC (build {Build}). " +
                       $"{NamedCount:N0} have a known name and {UnnamedCount:N0} have none. ";
            if (ListedForThisBuildCount + ListedOnlyForOtherBuildsCount > 0)
                text += $"Of the named ones, {ListedForThisBuildCount:N0} fall within a build range a verified source lists for build {Build}, " +
                        $"and {ListedOnlyForOtherBuildsCount:N0} are listed by a verified source only for other builds.";
            else
                text += "No verified build data covers these IDs.";
            return text.TrimEnd();
        }
    }

    public static CatalogCrossCheckResult Run(IEnumerable<uint> configuredIds, IFeatureCatalog catalog, int build, int sampleSize = 50)
    {
        int configured = 0, named = 0, thisBuild = 0, otherBuilds = 0;
        var unnamed = new List<uint>();
        foreach (var id in configuredIds.Distinct())
        {
            configured++;
            var entry = catalog.TryGet(id);
            if (entry?.Name == null) { unnamed.Add(id); continue; }
            named++;
            switch (entry.IsCompatibleWith(build))
            {
                case true: thisBuild++; break;
                case false: otherBuilds++; break;
            }
        }
        unnamed.Sort();
        return new CatalogCrossCheckResult(build, configured, named, thisBuild, otherBuilds, unnamed.Count, unnamed.Take(sampleSize).ToList());
    }
}
