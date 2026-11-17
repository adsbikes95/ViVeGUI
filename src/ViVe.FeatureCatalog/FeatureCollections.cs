// SPDX-License-Identifier: GPL-3.0-or-later
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace ViVe.FeatureCatalog;

/// <summary>Which collection a feature is shown in, and why. <see cref="Inferred"/> is false only for cited, curated categories.</summary>
public sealed record CollectionAssignment(string Collection, bool Inferred, string Reason);

public sealed record FeatureCollectionInfo(string Name, string Description);

/// <summary>
/// Groups features into browsing collections (File Explorer, Start menu, Taskbar, ...). A curated catalog entry with a
/// cited category wins; otherwise the collection is inferred from whole words of the feature's internal name and,
/// failing that, from the binary the feature was found in (see catalog/collection-rules.json). Inferred collections
/// are a browsing aid, never a claim about what a feature does or whether it is safe.
/// </summary>
public sealed partial class FeatureCollectionRules
{
    private readonly List<(FeatureCollectionInfo Info, string[][] Phrases, Regex[] Modules)> _rules = new();

    public FeatureCollectionRules(RulesDocument document)
    {
        foreach (var c in document.Collections ?? Array.Empty<RuleCollection>())
        {
            if (string.IsNullOrWhiteSpace(c.Name)) continue;
            var phrases = (c.Words ?? Array.Empty<string>())
                .Select(w => w.ToLowerInvariant().Split(new[] { ' ', '-' }, StringSplitOptions.RemoveEmptyEntries))
                .Where(p => p.Length > 0).ToArray();
            var modules = (c.Modules ?? Array.Empty<string>())
                .Select(m => new Regex("^" + Regex.Escape(m).Replace("\\*", ".*") + "$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))
                .ToArray();
            _rules.Add((new FeatureCollectionInfo(c.Name, c.Description ?? string.Empty), phrases, modules));
        }
        Fallback = new FeatureCollectionInfo(document.Fallback?.Name ?? "Other", document.Fallback?.Description ?? "No rule matched.");
    }

    public FeatureCollectionInfo Fallback { get; }

    /// <summary>All collections in display order, ending with the fallback.</summary>
    public IReadOnlyList<FeatureCollectionInfo> Collections => _rules.Select(r => r.Info).Append(Fallback).ToList();

    public static FeatureCollectionRules Default { get; } = LoadEmbedded();

    public CollectionAssignment Assign(FeatureCatalogEntry entry)
    {
        if (!string.IsNullOrWhiteSpace(entry.Category) && !string.IsNullOrWhiteSpace(entry.Source) &&
            entry.VerificationStatus >= VerificationStatus.CommunityReported)
            return new CollectionAssignment(entry.Category!, false, $"Category from {entry.Source}");

        var names = entry.Name == null ? Array.Empty<string>() : new[] { entry.Name }.Concat(entry.Aliases).ToArray();
        foreach (var (info, phrases, _) in _rules)
            foreach (var name in names)
            {
                var tokens = Tokenize(name);
                foreach (var phrase in phrases)
                    if (ContainsPhrase(tokens, phrase))
                        return new CollectionAssignment(info.Name, true, $"Inferred from the name (word \"{string.Join(' ', phrase)}\")");
            }
        foreach (var (info, _, modules) in _rules)
            foreach (var module in entry.Modules)
                if (modules.Any(m => m.IsMatch(module)))
                    return new CollectionAssignment(info.Name, true, $"Inferred from the binary it is in ({module})");
        return new CollectionAssignment(Fallback.Name, true, "No rule matched the name or binary");
    }

    [GeneratedRegex("[A-Z]+(?![a-z])|[A-Z]?[a-z]+|[0-9]+")]
    private static partial Regex TokenPattern();

    /// <summary>Splits "TaskbarAutoHide_V2" into ["taskbar", "auto", "hide", "v", "2"].</summary>
    public static string[] Tokenize(string name) =>
        TokenPattern().Matches(name).Select(m => m.Value.ToLowerInvariant()).ToArray();

    private static bool ContainsPhrase(string[] tokens, string[] phrase)
    {
        for (var i = 0; i + phrase.Length <= tokens.Length; i++)
        {
            var match = true;
            for (var j = 0; j < phrase.Length && match; j++) match = tokens[i + j] == phrase[j];
            if (match) return true;
        }
        return false;
    }

    public static FeatureCollectionRules Load(Stream stream) =>
        new(JsonSerializer.Deserialize<RulesDocument>(stream, new JsonSerializerOptions { PropertyNameCaseInsensitive = true, MaxDepth = 8 })
            ?? throw new InvalidDataException("Collection rules are empty."));

    private static FeatureCollectionRules LoadEmbedded()
    {
        using var s = typeof(FeatureCollectionRules).Assembly.GetManifestResourceStream("ViVe.FeatureCatalog.collection-rules.json")
            ?? throw new InvalidOperationException("Embedded collection rules are missing.");
        return Load(s);
    }

    public sealed record RulesDocument
    {
        [JsonPropertyName("collections")] public RuleCollection[]? Collections { get; init; }
        [JsonPropertyName("fallback")] public RuleCollection? Fallback { get; init; }
    }

    public sealed record RuleCollection
    {
        [JsonPropertyName("name")] public string? Name { get; init; }
        [JsonPropertyName("description")] public string? Description { get; init; }
        [JsonPropertyName("words")] public string[]? Words { get; init; }
        [JsonPropertyName("modules")] public string[]? Modules { get; init; }
    }
}
