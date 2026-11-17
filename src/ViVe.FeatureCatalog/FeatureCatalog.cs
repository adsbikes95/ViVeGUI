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

    /// <summary>Every entry, ordered by name then ID.</summary>
    IReadOnlyList<FeatureCatalogEntry> Entries { get; }
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

    public IReadOnlyList<FeatureCatalogEntry> Entries => _ordered;
    private readonly List<string> _messages = new();

    public FeatureCatalog(IEnumerable<CatalogLoadResult> sourcesInIncreasingTrust)
    {
        foreach (var source in sourcesInIncreasingTrust)
        {
            _messages.AddRange(source.Errors);
            _messages.AddRange(source.Warnings);
            foreach (var entry in source.Entries)
                _byId[entry.Id] = _byId.TryGetValue(entry.Id, out var existing) ? Merge(existing, entry) : entry;
        }
        foreach (var entry in _byId.Values)
        {
            if (entry.Name != null)
                _byName.TryAdd(entry.Name, entry);
        }
        foreach (var entry in _byId.Values)
            foreach (var alias in entry.Aliases)
                _byName.TryAdd(alias, entry);
        _ordered = _byId.Values.OrderBy(e => e.Name ?? "\uffff", StringComparer.OrdinalIgnoreCase).ThenBy(e => e.Id).ToList();
        Categories = _byId.Values.Select(e => e.Category).Where(c => !string.IsNullOrWhiteSpace(c)).Select(c => c!)
            .Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(c => c, StringComparer.OrdinalIgnoreCase).ToList();
    }

    /// <summary>
    /// The more trusted entry wins. It keeps the earlier name when it has none, and any name the earlier
    /// source used that differs is kept as a searchable alias (so a rename never hides a known name).
    /// </summary>
    internal static FeatureCatalogEntry Merge(FeatureCatalogEntry existing, FeatureCatalogEntry incoming)
    {
        var name = incoming.Name ?? existing.Name;
        var aliases = incoming.Aliases.Concat(existing.Aliases)
            .Append(existing.Name).Append(incoming.Name)
            .Where(a => a != null && !string.Equals(a, name, StringComparison.OrdinalIgnoreCase))
            .Select(a => a!)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var modules = incoming.Modules.Count > 0 ? incoming.Modules : existing.Modules;
        return incoming with { Name = name, Aliases = aliases, Modules = modules };
    }

    public static FeatureCatalog Empty { get; } = new(Array.Empty<CatalogLoadResult>());

    /// <summary>
    /// Loads catalogs from a directory, in increasing trust: the bundled name dictionary, then imported
    /// catalogs (for example the mach2 import), then the curated catalog, then the user's own catalog.
    /// Missing files are reported, not fatal; missing imported catalogs are silently skipped.
    /// </summary>
    public static FeatureCatalog LoadFromDirectory(string directory, string? userCatalogPath = null, IEnumerable<string>? importedCatalogPaths = null)
    {
        var sources = new List<CatalogLoadResult>
        {
            FeatureCatalogLoader.LoadLegacyDictionaryFile(Path.Combine(directory, "FeatureDictionary.pfs")),
        };
        foreach (var imported in importedCatalogPaths ?? Array.Empty<string>())
            if (File.Exists(imported))
                sources.Add(FeatureCatalogLoader.LoadJsonFile(imported));
        sources.Add(FeatureCatalogLoader.LoadJsonFile(Path.Combine(directory, "feature-catalog.json")));
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
                  entry.Aliases.Any(a => a.Contains(query, StringComparison.OrdinalIgnoreCase)) ||
                  (entry.Description?.Contains(query, StringComparison.OrdinalIgnoreCase) ?? false);
            if (match) results.Add(entry);
        }
        return results;
    }
}
