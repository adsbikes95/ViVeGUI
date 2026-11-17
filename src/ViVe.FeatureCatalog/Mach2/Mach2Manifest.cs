// SPDX-License-Identifier: GPL-3.0-or-later
using System.Globalization;
using System.Security.Cryptography;

namespace ViVe.FeatureCatalog.Mach2;

/// <summary>Attribution and pinning for the mach2 feature dumps.</summary>
public static class Mach2Source
{
    public const string RepositoryUrl = "https://github.com/riverar/mach2";
    public const string Author = "Rafael Rivera (riverar)";
    public const string License = "GPL-3.0";

    /// <summary>
    /// Final commit with feature dumps. mach2 stopped publishing them on 2024-12-14, so the newest
    /// dump is build 27764 and nothing newer will appear upstream.
    /// </summary>
    public const string PinnedCommit = "d6bcdce5ef7c09d3cb24549500e5d63b87a5cc83";
    public const string PinnedCommitDate = "2024-12-14";

    public static string ShortCommit => PinnedCommit[..7];

    public static string Citation => $"mach2 by {Author}, {RepositoryUrl} ({License}), feature dumps at commit {ShortCommit}";

    /// <summary>Raw download URL for one file at the pinned commit (raw.githubusercontent.com serves plain text).</summary>
    public static Uri RawFileUri(string relativePath) =>
        new($"https://raw.githubusercontent.com/riverar/mach2/{PinnedCommit}/{relativePath.Replace('\\', '/')}");
}

public sealed record Mach2ManifestEntry(string Sha256, long Bytes, Mach2ListInfo Info);

/// <summary>
/// The list of mach2 dumps at <see cref="Mach2Source.PinnedCommit"/> with their SHA-256 hashes
/// (embedded from catalog/mach2-manifest.txt). Downloads are checked against it, so a changed or
/// substituted file is rejected instead of imported.
/// </summary>
public sealed class Mach2Manifest
{
    private readonly Dictionary<string, Mach2ManifestEntry> _byPath;

    public Mach2Manifest(IReadOnlyList<Mach2ManifestEntry> entries)
    {
        Entries = entries;
        _byPath = entries.ToDictionary(e => e.Info.RelativePath, StringComparer.Ordinal);
    }

    public IReadOnlyList<Mach2ManifestEntry> Entries { get; }
    public long TotalBytes => Entries.Sum(e => e.Bytes);

    public Mach2ManifestEntry? Find(string relativePath) => _byPath.GetValueOrDefault(relativePath.Replace('\\', '/'));

    public static Mach2Manifest Parse(TextReader reader)
    {
        var entries = new List<Mach2ManifestEntry>();
        string? line;
        while ((line = reader.ReadLine()) != null)
        {
            if (line.Length == 0 || line.StartsWith('#')) continue;
            var parts = line.Split(' ', 3);
            if (parts.Length != 3 || parts[0].Length != 64 || !parts[0].All(Uri.IsHexDigit) ||
                !long.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out var bytes))
                throw new FormatException("Invalid mach2 manifest line: " + line);
            var info = Mach2ListInfo.TryParse(parts[2]) ?? throw new FormatException("Unrecognized mach2 list path: " + parts[2]);
            entries.Add(new Mach2ManifestEntry(parts[0].ToLowerInvariant(), bytes, info));
        }
        return new Mach2Manifest(entries);
    }

    /// <summary>The manifest shipped with this build.</summary>
    public static Mach2Manifest Embedded { get; } = LoadEmbedded();

    private static Mach2Manifest LoadEmbedded()
    {
        using var stream = typeof(Mach2Manifest).Assembly.GetManifestResourceStream("ViVe.FeatureCatalog.mach2-manifest.txt")
            ?? throw new InvalidOperationException("Embedded mach2 manifest is missing.");
        using var reader = new StreamReader(stream);
        return Parse(reader);
    }

    public static string Sha256Hex(ReadOnlySpan<byte> data) => Convert.ToHexStringLower(SHA256.HashData(data));
}
