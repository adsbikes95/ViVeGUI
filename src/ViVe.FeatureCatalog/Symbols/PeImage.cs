// SPDX-License-Identifier: GPL-3.0-or-later
using System.Buffers.Binary;
using System.Text;

namespace ViVe.FeatureCatalog.Symbols;

/// <summary>The CodeView (RSDS) record that names a binary's matching PDB on a symbol server.</summary>
public sealed record PdbIdentity(string PdbFileName, Guid Guid, uint Age)
{
    /// <summary>Symbol-server key: GUID as 32 upper-case hex digits followed by the age in hex.</summary>
    public string SymbolServerKey => Guid.ToString("N").ToUpperInvariant() + Age.ToString("X", System.Globalization.CultureInfo.InvariantCulture);
}

/// <summary>
/// Minimal, bounds-checked reader for 64-bit PE images (AMD64 / ARM64): section mapping, the CodeView
/// record and imported DLL names. It reads bytes only; nothing in the image is loaded or executed.
/// </summary>
public sealed class PeImage
{
    public const ushort MachineAmd64 = 0x8664;
    public const ushort MachineArm64 = 0xAA64;

    private readonly byte[] _data;
    private readonly (uint VirtualAddress, uint VirtualSize, uint RawSize, uint RawPointer)[] _sections;
    private readonly int _dataDirectoryOffset;
    private readonly uint _dataDirectoryCount;

    private PeImage(byte[] data, ushort machine, (uint, uint, uint, uint)[] sections, int dataDirectoryOffset, uint dataDirectoryCount)
    {
        _data = data;
        Machine = machine;
        _sections = sections;
        _dataDirectoryOffset = dataDirectoryOffset;
        _dataDirectoryCount = dataDirectoryCount;
    }

    public ushort Machine { get; }
    public bool Is64BitWindows => Machine is MachineAmd64 or MachineArm64;

    /// <summary>Parses a PE image, or returns null if the bytes are not a well-formed PE32+ image.</summary>
    public static PeImage? TryParse(byte[] data)
    {
        try
        {
            if (data.Length < 0x40 || data[0] != 'M' || data[1] != 'Z') return null;
            var pe = BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(0x3C));
            if (pe < 0 || pe > data.Length - 24 || BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(pe)) != 0x00004550) return null;
            var machine = BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(pe + 4));
            var sectionCount = BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(pe + 6));
            var optionalSize = BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(pe + 20));
            var optional = pe + 24;
            if (optional + optionalSize > data.Length || optionalSize < 112) return null;
            if (BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(optional)) != 0x20B) return null; // PE32+ only
            var dirCount = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(optional + 108));
            var dirOffset = optional + 112;
            if (dirCount > 16 || dirOffset + dirCount * 8 > optional + optionalSize) return null;
            var sectionTable = optional + optionalSize;
            if (sectionCount > 96 || sectionTable + sectionCount * 40 > data.Length) return null;
            var sections = new (uint, uint, uint, uint)[sectionCount];
            for (var i = 0; i < sectionCount; i++)
            {
                var s = data.AsSpan(sectionTable + i * 40);
                sections[i] = (BinaryPrimitives.ReadUInt32LittleEndian(s[12..]), BinaryPrimitives.ReadUInt32LittleEndian(s[8..]),
                    BinaryPrimitives.ReadUInt32LittleEndian(s[16..]), BinaryPrimitives.ReadUInt32LittleEndian(s[20..]));
            }
            return new PeImage(data, machine, sections, dirOffset, dirCount);
        }
        catch (ArgumentOutOfRangeException) { return null; }
    }

    /// <summary>File offset of an RVA, or -1 when it is not backed by file data.</summary>
    public long RvaToOffset(uint rva, int length = 1)
    {
        foreach (var (va, vsize, rawSize, raw) in _sections)
        {
            if (rva < va) continue;
            var delta = (ulong)rva - va;
            if (delta + (ulong)length > rawSize || delta >= Math.Max(vsize, rawSize)) continue;
            var offset = (long)raw + (long)delta;
            return offset + length <= _data.Length ? offset : -1;
        }
        return -1;
    }

    /// <summary>RVA of (section index from 1, offset), as PDB public symbols address data.</summary>
    public uint? SectionOffsetToRva(ushort section, uint offset) =>
        section >= 1 && section <= _sections.Length ? _sections[section - 1].VirtualAddress + offset : null;

    public bool TryReadUInt32(uint rva, out uint value)
    {
        value = 0;
        var off = RvaToOffset(rva, 4);
        if (off < 0) return false;
        value = BinaryPrimitives.ReadUInt32LittleEndian(_data.AsSpan((int)off));
        return true;
    }

    private (uint Rva, uint Size) Directory(int index)
    {
        if (index >= _dataDirectoryCount) return (0, 0);
        var s = _data.AsSpan(_dataDirectoryOffset + index * 8);
        return (BinaryPrimitives.ReadUInt32LittleEndian(s), BinaryPrimitives.ReadUInt32LittleEndian(s[4..]));
    }

    /// <summary>The PDB this image was built with (from its debug directory), or null.</summary>
    public PdbIdentity? GetPdbIdentity()
    {
        var (rva, size) = Directory(6);
        if (rva == 0 || size < 28) return null;
        for (uint i = 0; i < Math.Min(size / 28, 32); i++)
        {
            var entry = RvaToOffset(rva + i * 28, 28);
            if (entry < 0) return null;
            var e = _data.AsSpan((int)entry, 28);
            if (BinaryPrimitives.ReadUInt32LittleEndian(e[12..]) != 2) continue; // IMAGE_DEBUG_TYPE_CODEVIEW
            var dataSize = BinaryPrimitives.ReadUInt32LittleEndian(e[16..]);
            var pointer = BinaryPrimitives.ReadUInt32LittleEndian(e[24..]);
            if (dataSize < 25 || dataSize > 4096 || pointer + (long)dataSize > _data.Length) return null;
            var cv = _data.AsSpan((int)pointer, (int)dataSize);
            if (BinaryPrimitives.ReadUInt32LittleEndian(cv) != 0x53445352) return null; // "RSDS"
            var guid = new Guid(cv.Slice(4, 16));
            var age = BinaryPrimitives.ReadUInt32LittleEndian(cv[20..]);
            var nameBytes = cv[24..];
            var nul = nameBytes.IndexOf((byte)0);
            var path = Encoding.UTF8.GetString(nul < 0 ? nameBytes : nameBytes[..nul]);
            var fileName = path.Replace('/', '\\').Split('\\')[^1];
            if (fileName.Length == 0 || fileName.Length > 200 || !fileName.EndsWith(".pdb", StringComparison.OrdinalIgnoreCase) ||
                fileName.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || fileName.Contains("..", StringComparison.Ordinal))
                return null;
            return new PdbIdentity(fileName, guid, age);
        }
        return null;
    }

    private static readonly byte[][] FeatureStagingMarkers =
    {
        "featurestaging"u8.ToArray(),          // api-ms-win-core-featurestaging-l1-1-x.dll imports
        "RtlQueryFeatureConfiguration"u8.ToArray(), // direct ntdll imports
    };

    /// <summary>
    /// True if the image references the Windows feature-staging API (where WIL feature checks land), so it is
    /// worth fetching symbols for. A cheap byte search: it may over-include, never under-include for WIL features.
    /// </summary>
    public static bool ReferencesFeatureStaging(ReadOnlySpan<byte> data)
    {
        foreach (var marker in FeatureStagingMarkers)
        {
            if (data.IndexOf(marker) >= 0) return true;
            // Import names are ASCII, but check a case-folded variant of the DLL name too.
            if (marker.Length == 14 && data.IndexOf("FeatureStaging"u8) >= 0) return true;
        }
        return false;
    }
}
