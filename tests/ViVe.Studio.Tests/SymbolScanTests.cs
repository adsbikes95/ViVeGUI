// SPDX-License-Identifier: GPL-3.0-or-later
using System.Buffers.Binary;
using System.Text;
using Albacore.ViVe.NativeEnums;
using ViVe.Core;
using ViVe.FeatureCatalog;
using ViVe.FeatureCatalog.Symbols;
using ViVe.Studio.Services;
using ViVe.Studio.ViewModels;
using Xunit;

namespace ViVe.Studio.Tests;

/// <summary>Builds tiny synthetic PE images and PDBs, so scanning is tested without Microsoft binaries.</summary>
internal static class SymbolFixtures
{
    public static readonly Guid PdbGuid = new("0b5d8a35-1c2e-4d6f-9a10-0123456789ab");
    public const uint PdbAge = 3;

    /// <summary>A PE32+ image with one data section holding feature descriptors (ID at offset 8) and a CodeView record.</summary>
    public static byte[] BuildPe((uint Id, int Slot)[] descriptors, Guid? guid = null, ushort machine = PeImage.MachineAmd64, bool featureStaging = true)
    {
        var b = new byte[0x400];
        b[0] = (byte)'M'; b[1] = (byte)'Z';
        BinaryPrimitives.WriteInt32LittleEndian(b.AsSpan(0x3C), 0x80);
        BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(0x80), 0x00004550);
        BinaryPrimitives.WriteUInt16LittleEndian(b.AsSpan(0x84), machine);
        BinaryPrimitives.WriteUInt16LittleEndian(b.AsSpan(0x86), 1);
        BinaryPrimitives.WriteUInt16LittleEndian(b.AsSpan(0x94), 0xF0);
        const int opt = 0x98;
        BinaryPrimitives.WriteUInt16LittleEndian(b.AsSpan(opt), 0x20B);
        BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(opt + 108), 16);
        BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(opt + 112 + 6 * 8), 0x1100); // debug directory RVA
        BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(opt + 112 + 6 * 8 + 4), 28);
        const int sec = opt + 0xF0;
        Encoding.ASCII.GetBytes(".data").CopyTo(b, sec);
        BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(sec + 8), 0x200);
        BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(sec + 12), 0x1000);
        BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(sec + 16), 0x200);
        BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(sec + 20), 0x200);
        foreach (var (id, slot) in descriptors)
            BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(0x200 + slot * 32 + FeatureSymbolScanner.DescriptorIdOffset), id);
        // Debug directory entry at RVA 0x1100 (file 0x300) pointing to an RSDS record at file 0x320.
        BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(0x300 + 12), 2);
        BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(0x300 + 16), 24 + 9);
        BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(0x300 + 24), 0x320);
        BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(0x320), 0x53445352);
        (guid ?? PdbGuid).TryWriteBytes(b.AsSpan(0x324));
        BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(0x334), PdbAge);
        Encoding.ASCII.GetBytes("test.pdb\0").CopyTo(b, 0x338);
        if (featureStaging) Encoding.ASCII.GetBytes("api-ms-win-core-featurestaging-l1-1-0.dll").CopyTo(b, 0x380);
        return b;
    }

    /// <summary>An MSF 7.00 PDB whose public symbols are <paramref name="publics"/> (name, section, offset).</summary>
    public static byte[] BuildPdb((string Name, ushort Section, uint Offset)[] publics, Guid? guid = null)
    {
        const int bs = 512;
        var info = new byte[28];
        BinaryPrimitives.WriteUInt32LittleEndian(info, 20000404);
        BinaryPrimitives.WriteUInt32LittleEndian(info.AsSpan(8), PdbAge);
        (guid ?? PdbGuid).TryWriteBytes(info.AsSpan(12));
        var dbi = new byte[64];
        BinaryPrimitives.WriteUInt16LittleEndian(dbi.AsSpan(20), 4); // symbol record stream index
        var sym = new MemoryStream();
        foreach (var (name, section, offset) in publics)
        {
            var nameBytes = Encoding.UTF8.GetBytes(name + "\0");
            var body = 2 + 10 + nameBytes.Length;
            var pad = (4 - (body + 2) % 4) % 4;
            var rec = new byte[2 + body + pad];
            BinaryPrimitives.WriteUInt16LittleEndian(rec, (ushort)(body + pad));
            BinaryPrimitives.WriteUInt16LittleEndian(rec.AsSpan(2), 0x110E);
            BinaryPrimitives.WriteUInt32LittleEndian(rec.AsSpan(8), offset);
            BinaryPrimitives.WriteUInt16LittleEndian(rec.AsSpan(12), section);
            nameBytes.CopyTo(rec, 14);
            sym.Write(rec);
        }
        var streams = new[] { Array.Empty<byte>(), info, Array.Empty<byte>(), dbi, sym.ToArray() };

        var next = 5u; // 0 super, 1-2 FPM, 3 directory map, 4 directory
        var dir = new MemoryStream();
        var w = new BinaryWriter(dir);
        w.Write((uint)streams.Length);
        foreach (var s in streams) w.Write((uint)s.Length);
        var placements = new List<(uint Block, byte[] Data, int Offset)>();
        foreach (var s in streams)
            for (var off = 0; off < s.Length; off += bs)
            {
                w.Write(next);
                placements.Add((next++, s, off));
            }
        var dirBytes = dir.ToArray();
        if (dirBytes.Length > bs) throw new InvalidOperationException("fixture directory too big");
        var file = new byte[next * bs];
        "Microsoft C/C++ MSF 7.00\r\n"u8.CopyTo(file);
        file[26] = 0x1A; file[27] = (byte)'D'; file[28] = (byte)'S';
        BinaryPrimitives.WriteUInt32LittleEndian(file.AsSpan(32), bs);
        BinaryPrimitives.WriteUInt32LittleEndian(file.AsSpan(36), 1);
        BinaryPrimitives.WriteUInt32LittleEndian(file.AsSpan(40), next);
        BinaryPrimitives.WriteUInt32LittleEndian(file.AsSpan(44), (uint)dirBytes.Length);
        BinaryPrimitives.WriteUInt32LittleEndian(file.AsSpan(52), 3);
        BinaryPrimitives.WriteUInt32LittleEndian(file.AsSpan(3 * bs), 4);
        dirBytes.CopyTo(file, 4 * bs);
        foreach (var (block, data, offset) in placements)
            data.AsSpan(offset, Math.Min(bs, data.Length - offset)).CopyTo(file.AsSpan((int)block * bs));
        return file;
    }

    public static readonly (string, ushort, uint)[] Publics =
    {
        ("Feature_AlphaFeature__descriptor", 1, 0),
        ("Feature_12345__descriptor", 1, 32),           // unnamed feature: skipped
        ("Feature_BetaFeature__descriptor", 1, 64),
        ("?GetCurrentFeatureEnabledState@...", 1, 96),  // not a descriptor
    };

    public static readonly (uint, int)[] Descriptors = { (1001, 0), (12345, 1), (1002, 2) };
}

public class SymbolScanTests
{
    [Fact]
    public void PeImage_ReadsCodeViewAndFeatureStagingMarker()
    {
        var pe = SymbolFixtures.BuildPe(SymbolFixtures.Descriptors);
        var image = PeImage.TryParse(pe)!;
        Assert.True(image.Is64BitWindows);
        var id = image.GetPdbIdentity()!;
        Assert.Equal("test.pdb", id.PdbFileName);
        Assert.Equal(SymbolFixtures.PdbGuid.ToString("N").ToUpperInvariant() + "3", id.SymbolServerKey);
        Assert.True(PeImage.ReferencesFeatureStaging(pe));
        Assert.False(PeImage.ReferencesFeatureStaging(SymbolFixtures.BuildPe(SymbolFixtures.Descriptors, featureStaging: false)));
        Assert.Equal("https://msdl.microsoft.com/download/symbols/test.pdb/" + id.SymbolServerKey + "/test.pdb", SymbolScanService.PdbUri(id).AbsoluteUri);
    }

    [Theory]
    [InlineData(new byte[0])]
    [InlineData(new byte[] { (byte)'M', (byte)'Z', 0, 0 })]
    public void PeImage_RejectsGarbage(byte[] data) => Assert.Null(PeImage.TryParse(data));

    [Fact]
    public void PeImage_RejectsTruncatedHeaders()
    {
        var pe = SymbolFixtures.BuildPe(SymbolFixtures.Descriptors);
        Assert.Null(PeImage.TryParse(pe.AsSpan(0, 0x100).ToArray()));
    }

    [Fact]
    public void Scanner_ReadsNamedDescriptorsAndSkipsTheRest()
    {
        var image = PeImage.TryParse(SymbolFixtures.BuildPe(SymbolFixtures.Descriptors))!;
        var pdb = PdbReader.Open(new MemoryStream(SymbolFixtures.BuildPdb(SymbolFixtures.Publics)));
        Assert.Equal(SymbolFixtures.PdbGuid, pdb.ReadIdentity().Guid);
        var result = FeatureSymbolScanner.Scan(image, pdb);
        Assert.Equal(new[] { ("AlphaFeature", 1001u), ("BetaFeature", 1002u) }, result.Features.Select(f => (f.Name, f.Id)).OrderBy(f => f.Id));
        Assert.Equal(1, result.UnnamedCount);
    }

    [Fact]
    public void Scanner_Ignores32BitImages()
    {
        var image = PeImage.TryParse(SymbolFixtures.BuildPe(SymbolFixtures.Descriptors, machine: 0x14C))!;
        var pdb = PdbReader.Open(new MemoryStream(SymbolFixtures.BuildPdb(SymbolFixtures.Publics)));
        Assert.Empty(FeatureSymbolScanner.Scan(image, pdb).Features);
    }

    [Fact]
    public void Scanner_DescriptorOutsideTheImageIsReportedNotGuessed()
    {
        var image = PeImage.TryParse(SymbolFixtures.BuildPe(SymbolFixtures.Descriptors))!;
        var pdb = PdbReader.Open(new MemoryStream(SymbolFixtures.BuildPdb(new[] { ("Feature_Far__descriptor", (ushort)1, 0x5000u), ("Feature_NoSection__descriptor", (ushort)9, 0u) })));
        var result = FeatureSymbolScanner.Scan(image, pdb);
        Assert.Empty(result.Features);
        Assert.Equal(2, result.Warnings.Count);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(40)]
    [InlineData(52)]
    public void PdbReader_RejectsCorruptFiles(int corruptAt)
    {
        var pdb = SymbolFixtures.BuildPdb(SymbolFixtures.Publics);
        pdb[corruptAt] ^= 0xFF;
        if (corruptAt == 40) BinaryPrimitives.WriteUInt32LittleEndian(pdb.AsSpan(40), 1); // too few blocks
        Assert.Throws<InvalidDataException>(() => PdbReader.Open(new MemoryStream(pdb)).ReadPublicSymbols().ToList());
    }

    private static (StudioHarness H, SymbolScanService Service, FakeDownloader Downloader, string Root) ScanFixture(Guid? servedGuid = null)
    {
        var h = new StudioHarness();
        var root = Path.Combine(h.Temp.Path, "windows");
        Directory.CreateDirectory(Path.Combine(root, "System32"));
        File.WriteAllBytes(Path.Combine(root, "System32", "shellish.dll"), SymbolFixtures.BuildPe(SymbolFixtures.Descriptors));
        File.WriteAllBytes(Path.Combine(root, "System32", "plain.dll"), SymbolFixtures.BuildPe(SymbolFixtures.Descriptors, featureStaging: false));
        File.WriteAllText(Path.Combine(root, "System32", "notes.txt"), "featurestaging");
        var downloader = new FakeDownloader();
        var id = PeImage.TryParse(SymbolFixtures.BuildPe(SymbolFixtures.Descriptors))!.GetPdbIdentity()!;
        downloader.Files[SymbolScanService.PdbUri(id).AbsoluteUri] = SymbolFixtures.BuildPdb(SymbolFixtures.Publics, servedGuid);
        return (h, new SymbolScanService(h.Paths, downloader), downloader, root);
    }

    [Fact]
    public async Task ScanService_ScansCachesAndBuildsCatalog()
    {
        var (h, service, downloader, root) = ScanFixture();
        using var _ = h;
        var roots = new[] { new ScanRoot(Path.Combine(root, "System32"), true) };

        var first = await service.ScanAsync("29708.1003", 29708, roots);
        Assert.True(first.Success, first.Message);
        Assert.Equal((1, 1, 2), (first.BinariesScanned, first.BinariesWithFeatures, first.FeatureCount));
        Assert.Single(downloader.Requests); // only the binary that references feature staging
        Assert.Equal("msdl.microsoft.com", downloader.Requests[0].Host);

        var catalog = FeatureCatalog.FeatureCatalog.LoadFromDirectory(Path.Combine(AppContext.BaseDirectory, "data"), null, new[] { service.CatalogPath });
        var alpha = catalog.TryGet(1001)!;
        Assert.Equal(("AlphaFeature", VerificationStatus.Verified), (alpha.Name, alpha.VerificationStatus));
        Assert.True(alpha.IsCompatibleWith(29708));
        Assert.False(alpha.IsCompatibleWith(29709));
        Assert.Contains("shellish.dll", alpha.Builds[0].Note);
        Assert.Contains("mach2", alpha.Source);
        Assert.Null(alpha.Description);

        // Same binaries on a new build: nothing is downloaded again, and the catalog lists both builds.
        var second = await service.ScanAsync("29710.1000", 29710, roots);
        Assert.Single(downloader.Requests);
        Assert.Empty(second.NewNamesSincePreviousScan);
        catalog = FeatureCatalog.FeatureCatalog.LoadFromDirectory(Path.Combine(AppContext.BaseDirectory, "data"), null, new[] { service.CatalogPath });
        Assert.True(catalog.TryGet(1001)!.IsCompatibleWith(29708));
        Assert.True(catalog.TryGet(1001)!.IsCompatibleWith(29710));
        Assert.Equal(2, service.LoadScans().Count);

        Assert.True(service.RemoveScans());
        Assert.Empty(service.LoadScans());
        Assert.False(File.Exists(service.CatalogPath));
    }

    [Fact]
    public async Task ScanService_RejectsPdbThatDoesNotMatchTheBinary()
    {
        var (h, service, _, root) = ScanFixture(servedGuid: Guid.NewGuid());
        using var _h = h;
        var outcome = await service.ScanAsync("29708.1003", 29708, new[] { new ScanRoot(root, true) });
        Assert.True(outcome.Success);
        Assert.Equal(0, outcome.FeatureCount);
        Assert.Contains(outcome.Problems, p => p.Contains("does not match"));
    }

    [Fact]
    public async Task ScanService_MissingSymbolsAreCachedAsMissing()
    {
        var (h, service, downloader, root) = ScanFixture();
        using var _h = h;
        downloader.Files.Clear();
        var roots = new[] { new ScanRoot(root, true) };
        var outcome = await service.ScanAsync("29708.1003", 29708, roots);
        Assert.True(outcome.Success);
        Assert.Equal(0, outcome.FeatureCount);
        await service.ScanAsync("29708.1003", 29708, roots);
        Assert.Single(downloader.Requests); // the 404 was remembered
    }

    [Fact]
    public async Task ScanService_CancellationKeepsNothingHalfWritten()
    {
        var (h, service, _, root) = ScanFixture();
        using var _h = h;
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        var outcome = await service.ScanAsync("29708.1003", 29708, new[] { new ScanRoot(root, true) }, cancellationToken: cts.Token);
        Assert.True(outcome.Cancelled);
        Assert.False(File.Exists(service.CatalogPath));
    }

    [Fact]
    public async Task Snapshots_FlagIdsNewAfterAnUpdate()
    {
        using var h = new StudioHarness();
        var clock = new DateTimeOffset(2026, 11, 1, 0, 0, 0, TimeSpan.Zero);
        var snapshots = new BuildSnapshotService(h.Paths, () => clock = clock.AddHours(1));
        var first = await snapshots.RecordAsync("29700.1000", 29700, new uint[] { 1, 2, 3 });
        Assert.Null(snapshots.CompareWithPreviousBuild(first));
        // A later launch on the same build keeps the first snapshot (taken right after the update).
        Assert.Equal(first.ConfiguredIds, (await snapshots.RecordAsync("29700.1000", 29700, new uint[] { 1, 2, 3, 99 })).ConfiguredIds);

        var second = await snapshots.RecordAsync("29708.1003", 29708, new uint[] { 2, 3, 4, 5 });
        var diff = snapshots.CompareWithPreviousBuild(second)!;
        Assert.Equal("29700.1000", diff.Previous.BuildText);
        Assert.Equal(new uint[] { 4, 5 }, diff.Added);
        Assert.Equal(new uint[] { 1 }, diff.Removed);
    }

    [Fact]
    public async Task Dashboard_ShowsIdsNewSinceThePreviousBuild()
    {
        using var h = new StudioHarness();
        var snapshots = new BuildSnapshotService(h.Paths);
        await snapshots.RecordAsync("26000.1", 26000, new uint[] { 7 });
        await Task.Delay(20);
        h.Api.Seed(FeatureStore.Runtime, new FeatureConfiguration(7, RTL_FEATURE_CONFIGURATION_PRIORITY.Service, RTL_FEATURE_ENABLED_STATE.Enabled));
        h.Api.Seed(FeatureStore.Runtime, new FeatureConfiguration(12345678, RTL_FEATURE_CONFIGURATION_PRIORITY.Service, RTL_FEATURE_ENABLED_STATE.Enabled));
        var vm = new DashboardViewModel(h.Engine, h.History, new FakeSystemInfo(), h.Elevation, h.Settings, h.Changes, snapshots, h.Catalog);
        await vm.RefreshAsync();
        Assert.Contains("Since build 26000.1", vm.BuildChangesText);
        Assert.Equal(new[] { "12345678  ·  TestOnlyFeature" }, vm.NewSinceUpdate);
    }

    /// <summary>Scans real Windows binaries and their PDBs when SYMBOL_SAMPLES points at a folder of *.dll/*.exe + *.pdb pairs.</summary>
    [Fact]
    public void RealSymbols_Sample()
    {
        var dir = Environment.GetEnvironmentVariable("SYMBOL_SAMPLES");
        if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir)) return;
        foreach (var bin in Directory.EnumerateFiles(dir).Where(f => f.EndsWith(".dll", StringComparison.OrdinalIgnoreCase) || f.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)))
        {
            var image = PeImage.TryParse(File.ReadAllBytes(bin))!;
            var id = image.GetPdbIdentity()!;
            using var stream = File.OpenRead(Path.Combine(dir, id.PdbFileName));
            var pdb = PdbReader.Open(stream);
            Assert.Equal(id.Guid, pdb.ReadIdentity().Guid);
            var result = FeatureSymbolScanner.Scan(image, pdb);
            Console.WriteLine($"{Path.GetFileName(bin)}: {result.Features.Count} named, {result.UnnamedCount} unnamed, warnings {result.Warnings.Count}; e.g. " +
                              string.Join(", ", result.Features.Take(3).Select(f => $"{f.Name}={f.Id}")));
            Assert.NotEmpty(result.Features);
        }
    }
}
