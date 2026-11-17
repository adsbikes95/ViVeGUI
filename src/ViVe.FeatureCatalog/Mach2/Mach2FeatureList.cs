// SPDX-License-Identifier: GPL-3.0-or-later
using System.Globalization;
using System.Text.RegularExpressions;

namespace ViVe.FeatureCatalog.Mach2;

/// <summary>
/// Where one mach2 feature dump came from, taken from its path in the mach2 repository:
/// <c>features/&lt;branch&gt;/[&lt;arch&gt;/]&lt;build&gt;[.&lt;revision&gt;][_&lt;label&gt;].txt</c>.
/// </summary>
public sealed partial record Mach2ListInfo(string RelativePath, string Branch, string Architecture, int Build, int Revision, string? Label)
{
    [GeneratedRegex(@"^features/(?<branch>[A-Za-z0-9_\-]{1,64})/(?:(?<arch>[A-Za-z0-9_]{1,16})/)?(?<build>\d{4,6})(?:\.(?<rev>\d{1,6}))?(?:_(?<label>[A-Za-z0-9]{1,32}))?\.txt$")]
    private static partial Regex PathPattern();

    /// <summary>"26100.2314" style build string.</summary>
    public string BuildText => $"{Build}.{Revision}";

    public static Mach2ListInfo? TryParse(string relativePath)
    {
        var path = (relativePath ?? string.Empty).Replace('\\', '/');
        var m = PathPattern().Match(path);
        if (!m.Success) return null;
        var build = int.Parse(m.Groups["build"].Value, CultureInfo.InvariantCulture);
        var rev = m.Groups["rev"].Success ? int.Parse(m.Groups["rev"].Value, CultureInfo.InvariantCulture) : 0;
        // A few dumps sit directly under the branch folder; their architecture is not stated, so none is assumed.
        var arch = m.Groups["arch"].Success ? m.Groups["arch"].Value : "unspecified";
        return new Mach2ListInfo(path, m.Groups["branch"].Value, arch, build, rev,
            m.Groups["label"].Success ? m.Groups["label"].Value : null);
    }
}

/// <summary>One named feature in a mach2 dump. <see cref="Staging"/> is the section mach2 filed it under (null in older dumps).</summary>
public sealed record Mach2FeatureRecord(uint Id, string Name, string? Staging);

public sealed record Mach2ParseResult(Mach2ListInfo Info, IReadOnlyList<Mach2FeatureRecord> Features, int SkippedLines, IReadOnlyList<string> Warnings);

/// <summary>
/// Parses mach2 feature dumps. These are plain text, "Name: Id" per line, optionally grouped under
/// "## Always Enabled:" style staging headers. The parser only reads text: nothing in a dump is executed.
/// Entries without a usable name (numeric placeholders, ID 0) are skipped rather than guessed.
/// </summary>
public static partial class Mach2FeatureListParser
{
    /// <summary>Upper bound for one dump (the largest published one is under 1 MB).</summary>
    public const long MaxListBytes = 8L * 1024 * 1024;
    private const int MaxFeaturesPerList = 200_000;

    [GeneratedRegex(@"^\s*(?<name>[A-Za-z0-9_.\-]{1,256})(?:\|mach2\.warning\.[a-z]{1,32}\d{0,4})?:\s*(?<id>\d{1,10})\s*$")]
    private static partial Regex EntryPattern();

    [GeneratedRegex(@"^##\s*(?<section>[A-Za-z ]{1,40}):\s*$")]
    private static partial Regex SectionPattern();

    public static Mach2ParseResult Parse(TextReader reader, Mach2ListInfo info)
    {
        var features = new Dictionary<uint, Mach2FeatureRecord>();
        var warnings = new List<string>();
        var skipped = 0;
        string? staging = null;
        string? line;
        var lineNumber = 0;
        while ((line = reader.ReadLine()) != null)
        {
            lineNumber++;
            if (line.Length == 0 || line.Trim().Length == 0) continue;
            var section = SectionPattern().Match(line);
            if (section.Success) { staging = section.Groups["section"].Value.Trim(); continue; }
            if (line.TrimStart().StartsWith('#')) continue;

            var m = EntryPattern().Match(line);
            if (!m.Success)
            {
                skipped++;
                if (warnings.Count < 10) warnings.Add($"{info.RelativePath} line {lineNumber}: not a 'Name: Id' entry; skipped.");
                continue;
            }
            var name = m.Groups["name"].Value;
            if (!uint.TryParse(m.Groups["id"].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var id) || id == 0)
            {
                skipped++; // mach2 lists symbols it could not resolve to an ID as "Name: 0".
                continue;
            }
            if (name.All(char.IsAsciiDigit))
            {
                skipped++; // ID-only placeholder ("12345: 12345"): no name to import.
                continue;
            }
            if (!FeatureCatalogLoader.IsValidName(name)) { skipped++; continue; }
            // First occurrence in a dump wins; duplicates are flagged by mach2 itself.
            features.TryAdd(id, new Mach2FeatureRecord(id, name, staging));
            if (features.Count >= MaxFeaturesPerList)
            {
                warnings.Add($"{info.RelativePath}: more than {MaxFeaturesPerList} entries; remainder ignored.");
                break;
            }
        }
        return new Mach2ParseResult(info, features.Values.ToList(), skipped, warnings);
    }
}
