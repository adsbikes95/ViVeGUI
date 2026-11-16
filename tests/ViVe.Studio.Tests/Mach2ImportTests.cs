// SPDX-License-Identifier: GPL-3.0-or-later
using System.Text;
using Albacore.ViVe.NativeEnums;
using ViVe.Core;
using ViVe.FeatureCatalog;
using ViVe.FeatureCatalog.Mach2;
using ViVe.Studio.Services;
using ViVe.Studio.ViewModels;
using Xunit;

namespace ViVe.Studio.Tests;

public sealed class FakeDownloader : IFileDownloader
{
    public Dictionary<string, byte[]> Files { get; } = new();
    public List<Uri> Requests { get; } = new();

    public Task<byte[]> DownloadAsync(Uri uri, long maxBytes, CancellationToken cancellationToken)
    {
        lock (Requests) Requests.Add(uri);
        if (!Files.TryGetValue(uri.AbsoluteUri, out var data)) throw new HttpRequestException("404 (test)");
        if (data.Length > maxBytes) throw new InvalidDataException("too large (test)");
        return Task.FromResult(data);
    }
}

public class Mach2ImportTests
{
    private static readonly string DataDir = Path.Combine(AppContext.BaseDirectory, "data");

    private const string NewFormat = """
        ## Unknown:

          1000244541: 0
          1100577080: 50477134
          Ghost: 0

        ## Always Enabled:

          37023586: 37023586
          AlphaFeature: 1001

        ## Disabled By Default:

          BetaFeature|mach2.warning.duplicate0: 1002
          BetaFeatureDup: 1002

        ## Always Disabled:

          MeetNow: 28170999
          this is not an entry
        """;

    private const string OldFormat = """
        # .\symbols\Example.pdb
        AlphaFeature: 1001
        AOAC: 0
        """;

    private static Mach2ParseResult Parse(string text, string path) =>
        Mach2FeatureListParser.Parse(new StringReader(text), Mach2ListInfo.TryParse(path)!);

    [Theory]
    [InlineData("features/ge_release/amd64/26100.2314_WNC.txt", "ge_release", "amd64", 26100, 2314, "WNC")]
    [InlineData("features/ni_release/amd64/22557.txt", "ni_release", "amd64", 22557, 0, null)]
    [InlineData(@"features\rs_prerelease\arm64\27764.1000.txt", "rs_prerelease", "arm64", 27764, 1000, null)]
    [InlineData("features/ge_release_svc_betaflt_im/26100.2415.txt", "ge_release_svc_betaflt_im", "unspecified", 26100, 2415, null)]
    public void ListInfo_ParsesPaths(string path, string branch, string arch, int build, int rev, string? label)
    {
        var info = Mach2ListInfo.TryParse(path);
        Assert.NotNull(info);
        Assert.Equal((branch, arch, build, rev, label), (info!.Branch, info.Architecture, info.Build, info.Revision, info.Label));
    }

    [Theory]
    [InlineData("features/ni_release/amd64/22557_22563_diff.patch")]
    [InlineData("features/../secrets/26100.1.txt")]
    [InlineData("features/ge_release/amd64/x/26100.1.txt")]
    [InlineData("README.md")]
    public void ListInfo_RejectsOtherPaths(string path) => Assert.Null(Mach2ListInfo.TryParse(path));

    [Fact]
    public void Parser_ReadsSectionsAndSkipsWhatItCannotName()
    {
        var r = Parse(NewFormat, "features/ge_release/amd64/26100.1.txt");
        Assert.Equal(new uint[] { 1001, 1002, 28170999 }, r.Features.Select(f => f.Id).OrderBy(i => i));
        Assert.Equal("Always Enabled", r.Features.Single(f => f.Id == 1001).Staging);
        Assert.Equal("Always Disabled", r.Features.Single(f => f.Id == 28170999).Staging);
        // mach2's duplicate marker is stripped and the first name for an ID wins.
        Assert.Equal("BetaFeature", r.Features.Single(f => f.Id == 1002).Name);
        Assert.Single(r.Warnings); // "this is not an entry"
        Assert.True(r.SkippedLines >= 5); // numeric placeholders, ID 0 and the junk line
    }

    [Fact]
    public void Parser_ReadsOldUnsectionedFormat()
    {
        var r = Parse(OldFormat, "features/rs_prerelease/amd64/17643.1000.txt");
        var f = Assert.Single(r.Features);
        Assert.Equal(("AlphaFeature", 1001u), (f.Name, f.Id));
        Assert.Null(f.Staging);
    }

    [Fact]
    public void EmbeddedManifest_CoversThePinnedCommit()
    {
        var m = Mach2Manifest.Embedded;
        Assert.Equal(295, m.Entries.Count);
        Assert.All(m.Entries, e => Assert.Equal(64, e.Sha256.Length));
        Assert.Equal(27764, m.Entries.Max(e => e.Info.Build));
        Assert.StartsWith("https://raw.githubusercontent.com/riverar/mach2/d6bcdce5ef7c09d3cb24549500e5d63b87a5cc83/features/",
            Mach2Source.RawFileUri(m.Entries[0].Info.RelativePath).AbsoluteUri);
    }

    [Fact]
    public void Builder_RecordsOnlyWhatTheDumpsShow()
    {
        var b = new Mach2CatalogBuilder();
        b.Add(Parse("AlphaFeature: 1001\nGamma: 3", "features/rs_prerelease/amd64/20000.1.txt"));
        b.Add(Parse("AlphaFeature: 1001", "features/rs_prerelease/amd64/20100.1.txt"));
        b.Add(Parse("Gamma: 3", "features/rs_prerelease/amd64/20200.1.txt"));
        b.Add(Parse("## Always Disabled:\n  AlphaRenamed: 1001", "features/rs_prerelease/amd64/20300.1.txt"));
        var (doc, report) = b.Build(new DateTimeOffset(2026, 11, 16, 0, 0, 0, TimeSpan.Zero));

        Assert.Equal((4, 2, 20000, 20300), (report.ListCount, report.FeatureCount, report.OldestBuild!.Value, report.NewestBuild!.Value));
        var alpha = doc.Features.Single(f => f.Id == 1001);
        Assert.Equal("AlphaRenamed", alpha.Name);              // latest dump's name
        Assert.Equal(new[] { "AlphaFeature" }, alpha.Aliases);  // older name kept as an alias
        Assert.Equal(VerificationStatus.Verified, alpha.VerificationStatus);
        Assert.Contains("riverar/mach2", alpha.Source);
        Assert.Null(alpha.Description);
        Assert.Null(alpha.Category);
        // Missing from the 20200 dump, so two runs rather than one range claiming 20000-20300.
        Assert.Equal(new (int?, int?)[] { (20000, 20100), (20300, 20300) }, alpha.Builds.Select(x => (x.MinBuild, x.MaxBuild)));
        Assert.Contains("\"Always Disabled\"", alpha.Builds[1].Note);
        Assert.True(alpha.IsCompatibleWith(20050));
        Assert.False(alpha.IsCompatibleWith(20200));
        Assert.False(alpha.IsCompatibleWith(29708));
        Assert.False(FeatureCatalogLoader.Validate(doc).HasErrors);
    }

    [Fact]
    public void Catalog_MergeKeepsEarlierNameAsSearchableAlias()
    {
        var dictionary = new CatalogLoadResult(new[] { new FeatureCatalogEntry { Id = 1001, Name = "OldName", VerificationStatus = VerificationStatus.NameOnly, Source = "pfs" } },
            Array.Empty<string>(), Array.Empty<string>());
        var imported = new CatalogLoadResult(new[] { new FeatureCatalogEntry { Id = 1001, Name = "NewName", VerificationStatus = VerificationStatus.Verified, Source = "mach2" } },
            Array.Empty<string>(), Array.Empty<string>());
        var catalog = new FeatureCatalog.FeatureCatalog(new[] { dictionary, imported });
        var entry = catalog.TryGet(1001)!;
        Assert.Equal("NewName", entry.Name);
        Assert.Equal(new[] { "OldName" }, entry.Aliases);
        Assert.Same(entry, catalog.FindByName("oldname"));
        Assert.Single(catalog.Search("OldNa"));
    }

    [Fact]
    public void Loader_RejectsInvalidAliases()
    {
        var r = FeatureCatalogLoader.LoadJson(new MemoryStream(Encoding.UTF8.GetBytes("""{ "features": [ { "id": 1, "name": "A", "aliases": ["ok", "not ok;"] } ] }""")));
        Assert.Empty(r.Entries);
        Assert.True(r.HasErrors);
    }

    private static (Mach2Manifest Manifest, FakeDownloader Downloader) TwoListFixture(bool corruptSecond = false)
    {
        var files = new[]
        {
            ("features/ge_release/amd64/26100.1.txt", "## Enabled By Default:\n  AlphaFeature: 1001\n  BetaFeature: 1002\n"),
            ("features/rs_prerelease/amd64/27764.1000.txt", "## Always Enabled:\n  AlphaFeature: 1001\n"),
        };
        var downloader = new FakeDownloader();
        var entries = new List<Mach2ManifestEntry>();
        foreach (var (path, text) in files)
        {
            var bytes = Encoding.UTF8.GetBytes(text);
            entries.Add(new Mach2ManifestEntry(Mach2Manifest.Sha256Hex(bytes), bytes.Length, Mach2ListInfo.TryParse(path)!));
            downloader.Files[Mach2Source.RawFileUri(path).AbsoluteUri] = bytes;
        }
        if (corruptSecond)
            downloader.Files[Mach2Source.RawFileUri(files[1].Item1).AbsoluteUri] = Encoding.UTF8.GetBytes("## Always Enabled:\n  Evil: 1001\n");
        return (new Mach2Manifest(entries), downloader);
    }

    [Fact]
    public async Task GitHubImport_WritesCatalogThatLoadsBetweenDictionaryAndCurated()
    {
        using var temp = new TempDirectory();
        var paths = new AppPaths(temp.Path, AppContext.BaseDirectory);
        var (manifest, downloader) = TwoListFixture();
        var service = new Mach2ImportService(paths, downloader, manifest);

        var outcome = await service.ImportFromGitHubAsync();
        Assert.True(outcome.Success, outcome.Message);
        Assert.Empty(outcome.Problems);
        Assert.Equal(2, downloader.Requests.Count);
        Assert.All(downloader.Requests, u => Assert.Equal("raw.githubusercontent.com", u.Host));
        Assert.True(service.GetStatus().IsImported);

        var catalog = FeatureCatalog.FeatureCatalog.LoadFromDirectory(DataDir, null, new[] { service.ImportFilePath });
        var alpha = catalog.TryGet(1001)!;
        Assert.Equal(VerificationStatus.Verified, alpha.VerificationStatus);
        Assert.True(alpha.IsCompatibleWith(27764));
        Assert.Contains(alpha.Builds, b => b.Note!.Contains("\"Always Enabled\""));
        Assert.True(catalog.Count > 2); // the ViVe dictionary is still loaded underneath

        Assert.True(service.RemoveImport());
        Assert.False(service.GetStatus().IsImported);
    }

    [Fact]
    public async Task GitHubImport_RejectsContentThatDoesNotMatchThePinnedHash()
    {
        using var temp = new TempDirectory();
        var (manifest, downloader) = TwoListFixture(corruptSecond: true);
        var service = new Mach2ImportService(new AppPaths(temp.Path, AppContext.BaseDirectory), downloader, manifest);
        var outcome = await service.ImportFromGitHubAsync();
        Assert.True(outcome.Success);
        Assert.Contains(outcome.Problems, p => p.Contains("SHA-256"));
        var loaded = FeatureCatalogLoader.LoadJsonFile(service.ImportFilePath);
        Assert.DoesNotContain(loaded.Entries, e => e.Name == "Evil");
        Assert.Equal("AlphaFeature", loaded.Entries.Single(e => e.Id == 1001).Name);
    }

    [Fact]
    public async Task GitHubImport_WhenNothingDownloads_KeepsExistingImport()
    {
        using var temp = new TempDirectory();
        var (manifest, _) = TwoListFixture();
        var service = new Mach2ImportService(new AppPaths(temp.Path, AppContext.BaseDirectory), new FakeDownloader(), manifest);
        var outcome = await service.ImportFromGitHubAsync();
        Assert.False(outcome.Success);
        Assert.Equal(2, outcome.Problems.Count);
        Assert.False(File.Exists(service.ImportFilePath));
    }

    [Fact]
    public async Task FolderImport_VerifiesPinnedFilesAndFlagsUnpinnedOnes()
    {
        using var temp = new TempDirectory();
        var root = Path.Combine(temp.Path, "mach2");
        Directory.CreateDirectory(Path.Combine(root, "features", "ge_release", "amd64"));
        File.WriteAllText(Path.Combine(root, "features", "ge_release", "amd64", "26100.1.txt"), "## Enabled By Default:\n  AlphaFeature: 1001\n  BetaFeature: 1002\n");
        File.WriteAllText(Path.Combine(root, "features", "ge_release", "amd64", "26200.1.txt"), "AlphaFeature: 1001\n");
        File.WriteAllText(Path.Combine(root, "features", "ge_release", "amd64", "26100.1_26200.1_diff.patch"), "+ Evil: 1\n");
        var (manifest, _) = TwoListFixture();
        var service = new Mach2ImportService(new AppPaths(Path.Combine(temp.Path, "data"), AppContext.BaseDirectory), new FakeDownloader(), manifest);

        var outcome = await service.ImportFromFolderAsync(root);
        Assert.True(outcome.Success, outcome.Message);
        Assert.Equal(2, outcome.Report!.ListCount);
        Assert.Contains(outcome.Problems, p => p.Contains("not in the pinned manifest"));

        var missing = await service.ImportFromFolderAsync(temp.Path);
        Assert.False(missing.Success);
    }

    [Fact]
    public async Task Engine_ListsDistinctConfiguredIds()
    {
        using var h = new StudioHarness();
        h.Api.Seed(FeatureStore.Runtime, new FeatureConfiguration(7, RTL_FEATURE_CONFIGURATION_PRIORITY.User, RTL_FEATURE_ENABLED_STATE.Enabled));
        h.Api.Seed(FeatureStore.Runtime, new FeatureConfiguration(7, RTL_FEATURE_CONFIGURATION_PRIORITY.Service, RTL_FEATURE_ENABLED_STATE.Disabled));
        h.Api.Seed(FeatureStore.Runtime, new FeatureConfiguration(3, RTL_FEATURE_CONFIGURATION_PRIORITY.Service, RTL_FEATURE_ENABLED_STATE.Enabled));
        Assert.Equal(new uint[] { 3, 7 }, await h.Engine.GetConfiguredFeatureIdsAsync());
        h.Api.FailRuntimeQuery = true;
        Assert.Null(await h.Engine.GetConfiguredFeatureIdsAsync());
    }

    [Fact]
    public void CrossCheck_CountsNamedUnnamedAndBuildCoverage()
    {
        var entries = new[]
        {
            new FeatureCatalogEntry { Id = 1, Name = "OnThisBuild", VerificationStatus = VerificationStatus.Verified, Source = "s", Builds = new[] { new BuildCompatibility { MinBuild = 100, MaxBuild = 200 } } },
            new FeatureCatalogEntry { Id = 2, Name = "OlderOnly", VerificationStatus = VerificationStatus.Verified, Source = "s", Builds = new[] { new BuildCompatibility { MinBuild = 50, MaxBuild = 60 } } },
            new FeatureCatalogEntry { Id = 3, Name = "NameOnly", VerificationStatus = VerificationStatus.NameOnly },
        };
        var catalog = new FeatureCatalog.FeatureCatalog(new[] { new CatalogLoadResult(entries, Array.Empty<string>(), Array.Empty<string>()) });
        var r = CatalogCrossCheckResult.Run(new uint[] { 1, 2, 3, 9, 8, 9 }, catalog, 150);
        Assert.Equal((5, 3, 1, 1, 2), (r.ConfiguredCount, r.NamedCount, r.ListedForThisBuildCount, r.ListedOnlyForOtherBuildsCount, r.UnnamedCount));
        Assert.Equal(new uint[] { 8, 9 }, r.UnnamedSample);
        Assert.Contains("build 150", r.Summary);
    }

    [Fact]
    public async Task SettingsViewModel_ImportReloadsCatalogAndCrossChecks()
    {
        using var h = new StudioHarness();
        var (manifest, downloader) = TwoListFixture();
        var service = new Mach2ImportService(h.Paths, downloader, manifest);
        var catalog = new ReloadableFeatureCatalog(() => FeatureCatalog.FeatureCatalog.LoadFromDirectory(
            DataDir, null, new[] { service.ImportFilePath }));
        var vm = new SettingsViewModel(h.Settings, h.Theme, h.Dialogs, catalog, h.Paths, h.Engine, service);
        Assert.Equal("Not imported yet.", vm.Mach2StatusText);

        // Declining the download prompt changes nothing and downloads nothing.
        h.Dialogs.ConfirmAnswers.Enqueue(false);
        await vm.ImportMach2Command.ExecuteAsync(null);
        Assert.Empty(downloader.Requests);

        h.Dialogs.ConfirmAnswers.Enqueue(true);
        await vm.ImportMach2Command.ExecuteAsync(null);
        Assert.Equal(StatusSeverity.Success, vm.StatusSeverity);
        Assert.Equal(VerificationStatus.Verified, catalog.TryGet(1001)!.VerificationStatus);
        Assert.StartsWith("Imported:", vm.Mach2StatusText);

        h.Api.Seed(FeatureStore.Runtime, new FeatureConfiguration(1001, RTL_FEATURE_CONFIGURATION_PRIORITY.Service, RTL_FEATURE_ENABLED_STATE.Enabled));
        h.Api.Seed(FeatureStore.Runtime, new FeatureConfiguration(4000000001, RTL_FEATURE_CONFIGURATION_PRIORITY.Service, RTL_FEATURE_ENABLED_STATE.Enabled));
        await vm.CrossCheckCommand.ExecuteAsync(null);
        Assert.Contains("2 feature IDs are configured", vm.CrossCheckText);
        Assert.Contains("4000000001", vm.CrossCheckUnnamedText);

        vm.RemoveMach2Command.Execute(null);
        Assert.Equal(VerificationStatus.NameOnly, catalog.TryGet(1001)?.VerificationStatus ?? VerificationStatus.NameOnly);
        Assert.Equal("Not imported yet.", vm.Mach2StatusText);
    }

    /// <summary>
    /// Imports a real mach2 clone when MACH2_ROOT points at one (skipped otherwise, so CI stays offline).
    /// </summary>
    [Fact]
    public async Task RealMach2Clone_ImportsAndMatchesManifest()
    {
        var root = Environment.GetEnvironmentVariable("MACH2_ROOT");
        if (string.IsNullOrEmpty(root) || !Directory.Exists(root)) return;
        using var temp = new TempDirectory();
        var service = new Mach2ImportService(new AppPaths(temp.Path, AppContext.BaseDirectory), new FakeDownloader());
        var outcome = await service.ImportFromFolderAsync(root);
        Assert.True(outcome.Success, outcome.Message);
        Assert.DoesNotContain(outcome.Problems, p => p.Contains("SHA-256"));
        Assert.Equal(295, outcome.Report!.ListCount);
        Assert.True(outcome.Report.FeatureCount > 10_000);
        Console.WriteLine(outcome.Message + " Size: " + new FileInfo(service.ImportFilePath).Length);
    }
}
