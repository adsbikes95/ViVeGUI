// SPDX-License-Identifier: GPL-3.0-or-later
using System.Buffers.Binary;
using System.Text;

namespace ViVe.FeatureCatalog.Symbols;

/// <summary>A public symbol (S_PUB32) from a PDB.</summary>
public readonly record struct PdbPublicSymbol(string Name, ushort Section, uint Offset);

/// <summary>
/// Minimal, bounds-checked reader for MSF 7.00 program databases: the PDB info stream (GUID/age) and the
/// public symbol records. It reads data only; it never loads DIA or any other code from a PDB.
/// </summary>
public sealed class PdbReader
{
    private static readonly byte[] Magic = [.. "Microsoft C/C++ MSF 7.00\r\n"u8, 0x1A, (byte)'D', (byte)'S', 0, 0, 0];
    private const int MaxStreams = 100_000;
    private const uint SPub32 = 0x110E;

    private readonly Stream _file;
    private readonly int _blockSize;
    private readonly uint[] _streamSizes;
    private readonly uint[][] _streamBlocks;

    private PdbReader(Stream file, int blockSize, uint[] sizes, uint[][] blocks)
    {
        _file = file;
        _blockSize = blockSize;
        _streamSizes = sizes;
        _streamBlocks = blocks;
    }

    public static PdbReader Open(Stream file)
    {
        if (!file.CanSeek) throw new InvalidDataException("PDB stream must be seekable.");
        var super = new byte[56];
        ReadAt(file, 0, super);
        if (!super.AsSpan(0, 32).SequenceEqual(Magic)) throw new InvalidDataException("Not an MSF 7.00 PDB.");
        var blockSize = BinaryPrimitives.ReadInt32LittleEndian(super.AsSpan(32));
        var blockCount = BinaryPrimitives.ReadUInt32LittleEndian(super.AsSpan(40));
        var dirSize = BinaryPrimitives.ReadUInt32LittleEndian(super.AsSpan(44));
        var dirMapBlock = BinaryPrimitives.ReadUInt32LittleEndian(super.AsSpan(52));
        if (blockSize is not (512 or 1024 or 2048 or 4096 or 8192 or 16384 or 32768) || (long)blockCount * blockSize > file.Length + blockSize)
            throw new InvalidDataException("Invalid PDB block layout.");
        if (dirSize == 0 || dirSize > 256 * 1024 * 1024) throw new InvalidDataException("Invalid PDB directory size.");

        var dirBlockCount = (int)((dirSize + blockSize - 1) / blockSize);
        var dirMap = new byte[dirBlockCount * 4];
        if (dirMap.Length > blockSize) throw new InvalidDataException("PDB directory too large.");
        ReadAt(file, (long)dirMapBlock * blockSize, dirMap);
        var directory = new byte[dirSize];
        for (var i = 0; i < dirBlockCount; i++)
        {
            var block = BinaryPrimitives.ReadUInt32LittleEndian(dirMap.AsSpan(i * 4));
            CheckBlock(block, blockCount);
            var len = (int)Math.Min(blockSize, dirSize - (long)i * blockSize);
            ReadAt(file, (long)block * blockSize, directory.AsSpan(i * blockSize, len));
        }

        var count = BinaryPrimitives.ReadUInt32LittleEndian(directory);
        if (count > MaxStreams || 4 + (long)count * 4 > dirSize) throw new InvalidDataException("Invalid PDB stream count.");
        var sizes = new uint[count];
        var blocks = new uint[count][];
        var pos = 4 + (int)count * 4;
        for (var i = 0; i < count; i++)
        {
            var size = BinaryPrimitives.ReadUInt32LittleEndian(directory.AsSpan(4 + i * 4));
            if (size == uint.MaxValue) size = 0;
            sizes[i] = size;
            var n = (int)((size + blockSize - 1) / blockSize);
            if (pos + (long)n * 4 > dirSize) throw new InvalidDataException("Truncated PDB directory.");
            var list = new uint[n];
            for (var j = 0; j < n; j++)
            {
                list[j] = BinaryPrimitives.ReadUInt32LittleEndian(directory.AsSpan(pos + j * 4));
                CheckBlock(list[j], blockCount);
            }
            blocks[i] = list;
            pos += n * 4;
        }
        return new PdbReader(file, blockSize, sizes, blocks);
    }

    private static void CheckBlock(uint block, uint blockCount)
    {
        if (block == 0 || block >= blockCount) throw new InvalidDataException("PDB block index out of range.");
    }

    private static void ReadAt(Stream file, long offset, Span<byte> buffer)
    {
        if (offset < 0 || offset + buffer.Length > file.Length) throw new InvalidDataException("PDB read past end of file.");
        file.Seek(offset, SeekOrigin.Begin);
        file.ReadExactly(buffer);
    }

    public int StreamCount => _streamSizes.Length;

    public byte[] ReadStream(int index, long maxBytes = 512L * 1024 * 1024)
    {
        if (index < 0 || index >= _streamSizes.Length) throw new InvalidDataException("PDB stream index out of range.");
        var size = _streamSizes[index];
        if (size > maxBytes) throw new InvalidDataException("PDB stream too large.");
        var data = new byte[size];
        var blocks = _streamBlocks[index];
        for (var i = 0; i < blocks.Length; i++)
        {
            var len = (int)Math.Min(_blockSize, size - (long)i * _blockSize);
            ReadAt(_file, (long)blocks[i] * _blockSize, data.AsSpan(i * _blockSize, len));
        }
        return data;
    }

    /// <summary>GUID and age from the PDB info stream, to confirm the PDB belongs to the binary.</summary>
    public (Guid Guid, uint Age) ReadIdentity()
    {
        var info = ReadStream(1, 1024 * 1024);
        if (info.Length < 28) throw new InvalidDataException("PDB info stream is truncated.");
        return (new Guid(info.AsSpan(12, 16)), BinaryPrimitives.ReadUInt32LittleEndian(info.AsSpan(8)));
    }

    /// <summary>All S_PUB32 records from the symbol record stream named by the DBI header.</summary>
    public IEnumerable<PdbPublicSymbol> ReadPublicSymbols()
    {
        var dbi = ReadStream(3, 512L * 1024 * 1024);
        if (dbi.Length < 64) throw new InvalidDataException("PDB DBI stream is truncated.");
        var symRecordStream = BinaryPrimitives.ReadUInt16LittleEndian(dbi.AsSpan(20));
        var records = ReadStream(symRecordStream);
        var pos = 0;
        while (pos + 4 <= records.Length)
        {
            var length = BinaryPrimitives.ReadUInt16LittleEndian(records.AsSpan(pos));
            var kind = BinaryPrimitives.ReadUInt16LittleEndian(records.AsSpan(pos + 2));
            if (length < 2 || pos + 2 + length > records.Length) yield break;
            if (kind == SPub32 && length >= 2 + 10 + 1)
            {
                var body = records.AsSpan(pos + 4, length - 2);
                var offset = BinaryPrimitives.ReadUInt32LittleEndian(body[4..]);
                var section = BinaryPrimitives.ReadUInt16LittleEndian(body[8..]);
                var nameSpan = body[10..];
                var nul = nameSpan.IndexOf((byte)0);
                var name = Encoding.UTF8.GetString(nul < 0 ? nameSpan : nameSpan[..nul]);
                yield return new PdbPublicSymbol(name, section, offset);
            }
            pos += 2 + length;
        }
    }
}
