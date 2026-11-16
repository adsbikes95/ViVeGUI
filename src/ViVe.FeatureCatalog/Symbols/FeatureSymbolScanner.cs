// SPDX-License-Identifier: GPL-3.0-or-later
using System.Text.RegularExpressions;

namespace ViVe.FeatureCatalog.Symbols;

/// <summary>A feature found in one binary: its internal name and ID.</summary>
public sealed record ScannedFeature(string Name, uint Id);

public sealed record BinaryScanResult(IReadOnlyList<ScannedFeature> Features, int UnnamedCount, IReadOnlyList<string> Warnings);

/// <summary>
/// Finds WIL feature names and IDs in a binary using its public PDB. The approach follows mach2 by Rafael
/// Rivera (github.com/riverar/mach2, GPL-3.0), which located features through their symbols:
/// every WIL feature has a public data symbol <c>Feature_&lt;Name&gt;__descriptor</c>, and on 64-bit Windows
/// the descriptor stores the feature ID as a 32-bit value at offset 8. The ID is read from the binary's
/// own bytes at that address; the binary is never loaded or executed.
/// </summary>
public static partial class FeatureSymbolScanner
{
    /// <summary>Offset of the feature ID in a WIL feature descriptor on 64-bit images.</summary>
    public const int DescriptorIdOffset = 8;

    [GeneratedRegex("^Feature_(?<name>[A-Za-z0-9_]{1,200})__descriptor$")]
    private static partial Regex DescriptorPattern();

    public static BinaryScanResult Scan(PeImage image, PdbReader pdb)
    {
        var warnings = new List<string>();
        if (!image.Is64BitWindows)
            return new BinaryScanResult(Array.Empty<ScannedFeature>(), 0, new[] { "Only 64-bit (AMD64/ARM64) images are scanned." });

        var byName = new Dictionary<string, ScannedFeature>(StringComparer.Ordinal);
        var unnamed = 0;
        foreach (var symbol in pdb.ReadPublicSymbols())
        {
            var m = DescriptorPattern().Match(symbol.Name);
            if (!m.Success) continue;
            var name = m.Groups["name"].Value;
            if (image.SectionOffsetToRva(symbol.Section, symbol.Offset) is not uint rva ||
                !image.TryReadUInt32(rva + DescriptorIdOffset, out var id) || id == 0)
            {
                if (warnings.Count < 5) warnings.Add($"Could not read the ID of {name}.");
                continue;
            }
            if (name.All(char.IsAsciiDigit))
            {
                // Feature compiled without a name (the symbol carries its number instead). Nothing to add.
                unnamed++;
                continue;
            }
            if (byName.TryGetValue(name, out var existing))
            {
                if (existing.Id != id && warnings.Count < 5) warnings.Add($"{name} appears with two IDs ({existing.Id}, {id}); the first was kept.");
                continue;
            }
            byName[name] = new ScannedFeature(name, id);
        }
        return new BinaryScanResult(byName.Values.ToList(), unnamed, warnings);
    }
}
