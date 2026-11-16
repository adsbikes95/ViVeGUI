// SPDX-License-Identifier: GPL-3.0-or-later
namespace ViVe.FeatureCatalog;

/// <summary>Read-only lookup over feature metadata.</summary>
public interface IFeatureCatalog
{
    int Count { get; }
    IReadOnlyList<string> Categories { get; }
    IReadOnlyList<string> LoadMessages { get; }

    /// <summary>Returns metadata for an ID, or null when the ID is unknown (unknown IDs are normal, not errors).</summary>
    FeatureCatalogEntry? TryGet(uint featureId);

    /// <summary>Finds an entry by exact (case-insensitive) name.</summary>
    FeatureCatalogEntry? FindByName(string name);

    /// <summary>Searches by numeric ID (exact or prefix) and by name substring.</summary>
    IReadOnlyList<FeatureCatalogEntry> Search(string query, int maxResults = 200, string? category = null);
}

/// <summary>
/// In-memory catalog that merges sources in order of trust: entries from curated JSON catalogs
/// replace name-only entries from the upstream dictionary, but keep the dictionary name if they have none.
/// </summary>
public sealed class FeatureCatalog : IFeatureCatalog
{
    private readonly Dictionary<uint, FeatureCatalogEntry> _byId = new();
    private readonly Dictionary<string, FeatureCatalogEntry> _byName = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<FeatureCatalogEntry> _ordered;
    private readonly List<string> _messages = new();

    public FeatureCatalog(IEnumerable<CatalogLoadResult> sourcesInIncreasingTrust)
    {
        foreach (var source in sourcesInIncreasingTrust)
        {
            _messages.AddRange(source.Errors);
            _messages.AddRange(source.Warnings);
            foreach (var entry in source.Entries)
            {
                var merged = _byId.TryGetValue(entry.Id, out var existing) && entry.Name == null
                    ? entry with { Name = existing.Name }
                    : entry;
                _byId[entry.Id] = merged;
            }
        }
        foreach (var entry in _byId.Values)
            if (entry.Name != null)
                _byName.TryAdd(entry.Name, entry);
        _ordered = _byId.Values.OrderBy(e => e.Name ?? "￿", StringComparer.OrdinalIgnoreCase).ThenBy(e => e.Id).ToList();
        Categories = _byId.Values.Select(e => e.Category).Where(c => !string.IsNullOrWhiteSpace(c)).Select(c => c!)
            .Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(c => c, StringComparer.OrdinalIgnoreCase).ToList();
    }

    public static FeatureCatalog Empty { get; } = new(Array.Empty<CatalogLoadResult>());

    /// <summary>Loads the bundled name dictionary and curated catalog from a directory (missing files are reported, not fatal).</summary>
    public static FeatureCatalog LoadFromDirectory(string directory, string? userCatalogPath = null)
    {
        var sources = new List<CatalogLoadResult>
        {
            FeatureCatalogLoader.LoadLegacyDictionaryFile(Path.Combine(directory, "FeatureDictionary.pfs")),
            FeatureCatalogLoader.LoadJsonFile(Path.Combine(directory, "feature-catalog.json")),
        };
        if (!string.IsNullOrWhiteSpace(userCatalogPath))
            sources.Add(FeatureCatalogLoader.LoadJsonFile(userCatalogPath));
        return new FeatureCatalog(sources);
    }

    public int Count => _byId.Count;
    public IReadOnlyList<string> Categories { get; }
    public IReadOnlyList<string> LoadMessages => _messages;

    public FeatureCatalogEntry? TryGet(uint featureId) => _byId.GetValueOrDefault(featureId);

    public FeatureCatalogEntry? FindByName(string name) =>
        string.IsNullOrWhiteSpace(name) ? null : _byName.GetValueOrDefault(name.Trim());

    public IReadOnlyList<FeatureCatalogEntry> Search(string query, int maxResults = 200, string? category = null)
    {
        query = (query ?? string.Empty).Trim();
        IEnumerable<FeatureCatalogEntry> source = _ordered;
        if (!string.IsNullOrEmpty(category))
            source = source.Where(e => string.Equals(e.Category, category, StringComparison.OrdinalIgnoreCase));
        if (query.Length == 0)
            return source.Take(maxResults).ToList();

        var results = new List<FeatureCatalogEntry>();
        if (uint.TryParse(query, out var exactId) && _byId.TryGetValue(exactId, out var exact) &&
            (category == null || string.Equals(exact.Category, category, StringComparison.OrdinalIgnoreCase)))
            results.Add(exact);
        var isNumeric = query.All(char.IsAsciiDigit);
        foreach (var entry in source)
        {
            if (results.Count >= maxResults) break;
            if (results.Count > 0 && ReferenceEquals(results[0], entry)) continue;
            var match = isNumeric
                ? entry.Id.ToString(System.Globalization.CultureInfo.InvariantCulture).StartsWith(query, StringComparison.Ordinal)
                : (entry.Name?.Contains(query, StringComparison.OrdinalIgnoreCase) ?? false) ||
                  (entry.Description?.Contains(query, StringComparison.OrdinalIgnoreCase) ?? false);
            if (match) results.Add(entry);
        }
        return results;
    }
}
