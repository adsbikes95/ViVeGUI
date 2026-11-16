// SPDX-License-Identifier: GPL-3.0-or-later
using System.Text.Json;
using System.Text.RegularExpressions;

namespace ViVe.FeatureCatalog;

/// <summary>Loads and validates catalog data from JSON documents and the upstream ViVe name dictionary.</summary>
public static partial class FeatureCatalogLoader
{
    public const string LegacyDictionarySource = "ViVe FeatureDictionary.pfs (github.com/thebookisclosed/ViVe, Extra/FeatureDictionary.pfs)";

    /// <summary>Upper bound on catalog size to avoid unbounded memory use from a malformed file.</summary>
    public const int MaxEntries = 500_000;
    private const long MaxFileBytes = 64L * 1024 * 1024;

    [GeneratedRegex("^[A-Za-z0-9_.\\-]{1,256}$")]
    private static partial Regex NamePattern();

    /// <summary>True when <paramref name="name"/> is an acceptable internal feature name (letters, digits, '_', '.', '-').</summary>
    public static bool IsValidName(string? name) => name != null && NamePattern().IsMatch(name);

    [GeneratedRegex(@"^[A-Za-z0-9_.\-]{1,128}\.(dll|exe|sys|cpl|mui|ocx|winmd)$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex ModulePattern();

    /// <summary>A plain binary file name such as "Taskbar.dll" (no path).</summary>
    public static bool IsValidModuleName(string? name) => name != null && ModulePattern().IsMatch(name);

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        MaxDepth = 16,
    };

    public static CatalogLoadResult LoadJson(Stream stream)
    {
        FeatureCatalogDocument? document;
        try
        {
            document = JsonSerializer.Deserialize<FeatureCatalogDocument>(stream, JsonOptions);
        }
        catch (JsonException ex)
        {
            return new CatalogLoadResult(Array.Empty<FeatureCatalogEntry>(), new[] { $"Catalog is not valid JSON: {ex.Message}" }, Array.Empty<string>());
        }
        if (document == null)
            return new CatalogLoadResult(Array.Empty<FeatureCatalogEntry>(), new[] { "Catalog is empty." }, Array.Empty<string>());
        return Validate(document);
    }

    public static CatalogLoadResult LoadJsonFile(string path)
    {
        if (!File.Exists(path))
            return new CatalogLoadResult(Array.Empty<FeatureCatalogEntry>(), new[] { $"Catalog file not found: {path}" }, Array.Empty<string>());
        if (new FileInfo(path).Length > MaxFileBytes)
            return new CatalogLoadResult(Array.Empty<FeatureCatalogEntry>(), new[] { "Catalog file is too large." }, Array.Empty<string>());
        using var stream = File.OpenRead(path);
        return LoadJson(stream);
    }

    public static CatalogLoadResult Validate(FeatureCatalogDocument document)
    {
        var errors = new List<string>();
        var warnings = new List<string>();
        var entries = new List<FeatureCatalogEntry>();
        if (document.SchemaVersion > FeatureCatalogDocument.CurrentSchemaVersion)
        {
            errors.Add($"Catalog schema version {document.SchemaVersion} is newer than supported ({FeatureCatalogDocument.CurrentSchemaVersion}).");
            return new CatalogLoadResult(entries, errors, warnings);
        }

        var seen = new HashSet<uint>();
        foreach (var entry in document.Features ?? Array.Empty<FeatureCatalogEntry>())
        {
            if (entry == null) { errors.Add("Catalog contains a null entry."); continue; }
            if (entry.Id == 0) { errors.Add("Entry with feature ID 0 skipped."); continue; }
            if (!seen.Add(entry.Id)) { errors.Add($"Duplicate entry for feature {entry.Id} skipped."); continue; }
            if (entry.Name != null && !NamePattern().IsMatch(entry.Name)) { errors.Add($"Feature {entry.Id} has an invalid name; entry skipped."); continue; }
            if ((entry.Aliases ?? Array.Empty<string>()).Any(a => a == null || !NamePattern().IsMatch(a))) { errors.Add($"Feature {entry.Id} has an invalid alias; entry skipped."); continue; }
            if (entry.VerificationStatus is VerificationStatus.CommunityReported or VerificationStatus.Verified && string.IsNullOrWhiteSpace(entry.Source))
            {
                errors.Add($"Feature {entry.Id} is marked {entry.VerificationStatus} but has no source; entry skipped.");
                continue;
            }
            if (entry.VerificationStatus == VerificationStatus.Verified && entry.Builds.Count == 0)
                warnings.Add($"Feature {entry.Id} is marked Verified but lists no builds; compatibility will be shown as unknown.");
            foreach (var b in entry.Builds)
                if (b.MinBuild is not null && b.MaxBuild is not null && b.MinBuild > b.MaxBuild)
                    warnings.Add($"Feature {entry.Id} has a build range with minBuild > maxBuild.");
            if (entries.Count >= MaxEntries) { errors.Add($"Catalog exceeds {MaxEntries} entries; remaining entries ignored."); break; }
            var clean = entry with { Aliases = entry.Aliases ?? Array.Empty<string>(), Modules = (entry.Modules ?? Array.Empty<string>()).Where(m => m != null && ModulePattern().IsMatch(m)).Take(32).ToArray() };
            if (!string.IsNullOrWhiteSpace(clean.Category) && (string.IsNullOrWhiteSpace(clean.Source) || clean.VerificationStatus < VerificationStatus.CommunityReported))
            {
                warnings.Add($"Feature {entry.Id} has a category without a cited source; the category was ignored.");
                clean = clean with { Category = null };
            }
            entries.Add(clean);
        }
        return new CatalogLoadResult(entries, errors, warnings);
    }

    /// <summary>
    /// Parses the upstream "Name,Id" dictionary. Entries only carry a name (VerificationStatus.NameOnly):
    /// knowing an ID's internal name says nothing about what it does or whether it is safe.
    /// </summary>
    public static CatalogLoadResult LoadLegacyDictionary(TextReader reader)
    {
        var entries = new Dictionary<uint, FeatureCatalogEntry>();
        var errors = new List<string>();
        string? line;
        var lineNumber = 0;
        while ((line = reader.ReadLine()) != null)
        {
            lineNumber++;
            if (line.Length == 0) continue;
            var comma = line.LastIndexOf(',');
            if (comma <= 0 || !uint.TryParse(line.AsSpan(comma + 1), System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var id) || id == 0)
            {
                if (errors.Count < 20) errors.Add($"Line {lineNumber}: not a 'Name,Id' pair.");
                continue;
            }
            var name = line.Substring(0, comma).Trim();
            if (!NamePattern().IsMatch(name))
            {
                if (errors.Count < 20) errors.Add($"Line {lineNumber}: invalid feature name.");
                continue;
            }
            // First occurrence wins, matching ViVeTool's FeatureNaming lookup order.
            entries.TryAdd(id, new FeatureCatalogEntry { Id = id, Name = name, Source = LegacyDictionarySource, VerificationStatus = VerificationStatus.NameOnly });
            if (entries.Count >= MaxEntries) break;
        }
        return new CatalogLoadResult(entries.Values.ToList(), errors, Array.Empty<string>());
    }

    public static CatalogLoadResult LoadLegacyDictionaryFile(string path)
    {
        if (!File.Exists(path))
            return new CatalogLoadResult(Array.Empty<FeatureCatalogEntry>(), new[] { $"Name dictionary not found: {path}" }, Array.Empty<string>());
        using var reader = new StreamReader(path);
        return LoadLegacyDictionary(reader);
    }
}
