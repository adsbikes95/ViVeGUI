// SPDX-License-Identifier: GPL-3.0-or-later
using System.Text;
using System.Text.Json;
using ViVe.FeatureCatalog;
using ViVe.Studio.Services;
using ViVe.Studio.ViewModels;
using Xunit;
using Xunit.Abstractions;

namespace ViVe.Studio.Tests;

public class FeatureCollectionRulesTests
{
    private static FeatureCatalogEntry Named(string name, params string[] modules) =>
        new() { Id = 1, Name = name, Modules = modules, VerificationStatus = VerificationStatus.NameOnly };

    [Theory]
    [InlineData("TaskbarAutoHide", "Taskbar")]
    [InlineData("StartMenuRecommendations", "Start menu")]
    [InlineData("FileExplorerTabs", "File Explorer")]
    [InlineData("Servicing_TaskbarCrash", "Fixes and servicing")] // earlier rules win
    [InlineData("ModernContextMenu", "Context menus")]
    [InlineData("SnapLayoutsV2", "Window management")]
    [InlineData("LockScreenWidgets", "Lock screen and sign-in")]
    [InlineData("SomethingUnrelated", "Other")]
    public void Name_Rules(string name, string expected)
    {
        var a = FeatureCollectionRules.Default.Assign(Named(name));
        Assert.Equal(expected, a.Collection);
        Assert.True(a.Inferred);
    }

    [Theory]
    [InlineData("RestartManager")] // "start" inside "Restart" is not the word "start"
    [InlineData("Repentant")]      // "pen" inside a word does not count
    [InlineData("Settled")]        // not "settings"
    public void Words_MatchWholeTokensOnly(string name) =>
        Assert.Equal("Other", FeatureCollectionRules.Default.Assign(Named(name)).Collection);

    [Fact]
    public void Tokenize_SplitsAtCapitalsDigitsAndUnderscores() =>
        Assert.Equal(new[] { "taskbar", "auto", "hide", "v", "2", "xaml", "ui" }, FeatureCollectionRules.Tokenize("TaskbarAutoHide_V2XAMLUi"));

    [Fact]
    public void Binary_IsUsedWhenNameSaysNothing()
    {
        var a = FeatureCollectionRules.Default.Assign(Named("Feature_48433719", "Taskbar.View.dll"));
        Assert.Equal("Taskbar", a.Collection);
        Assert.Contains("Taskbar.View.dll", a.Reason);
        Assert.True(a.Inferred);
        Assert.Equal("Settings", FeatureCollectionRules.Default.Assign(Named("Velocity", "SettingsHandlers_Display.dll")).Collection);
    }

    [Fact]
    public void Name_BeatsBinary() =>
        Assert.Equal("Start menu", FeatureCollectionRules.Default.Assign(Named("StartMenuPins", "Taskbar.dll")).Collection);

    [Fact]
    public void CitedCategory_WinsAndIsNotInferred()
    {
        var entry = new FeatureCatalogEntry { Id = 5, Name = "TaskbarThing", Category = "Taskbar end task", Source = "https://example.org/post", VerificationStatus = VerificationStatus.CommunityReported };
        var a = FeatureCollectionRules.Default.Assign(entry);
        Assert.Equal("Taskbar end task", a.Collection);
        Assert.False(a.Inferred);
        Assert.Contains("example.org", a.Reason);
    }

    [Fact]
    public void Loader_DropsCategoryWithoutCitation()
    {
        var doc = new FeatureCatalogDocument
        {
            Features = new[]
            {
                new FeatureCatalogEntry { Id = 5, Name = "A", Category = "Taskbar", VerificationStatus = VerificationStatus.NameOnly },
                new FeatureCatalogEntry { Id = 6, Name = "B", Category = "Taskbar", Source = "https://example.org", VerificationStatus = VerificationStatus.CommunityReported },
            },
        };
        var result = FeatureCatalogLoader.Validate(doc);
        Assert.Null(result.Entries.Single(e => e.Id == 5).Category);
        Assert.Equal("Taskbar", result.Entries.Single(e => e.Id == 6).Category);
        Assert.Contains(result.Warnings, w => w.Contains("5") && w.Contains("category"));
        // The uncited category is then inferred from the name instead.
        Assert.True(FeatureCollectionRules.Default.Assign(result.Entries.Single(e => e.Id == 5)).Inferred);
    }

    [Fact]
    public void Loader_DropsInvalidModules()
    {
        var doc = new FeatureCatalogDocument { Features = new[] { new FeatureCatalogEntry { Id = 5, Name = "A", Modules = new[] { "Taskbar.dll", "..\\evil.dll", "notabinary" } } } };
        Assert.Equal(new[] { "Taskbar.dll" }, FeatureCatalogLoader.Validate(doc).Entries[0].Modules);
    }

    [Fact]
    public void Merge_KeepsModulesFromLessTrustedSource()
    {
        var scan = new FeatureCatalogEntry { Id = 9, Name = "X", Modules = new[] { "shell32.dll" } };
        var curated = new FeatureCatalogEntry { Id = 9, Name = "X", Source = "s", VerificationStatus = VerificationStatus.CommunityReported };
        Assert.Equal(new[] { "shell32.dll" }, FeatureCatalog.FeatureCatalog.Merge(scan, curated).Modules);
    }

    [Fact]
    public void EmbeddedRules_HaveCollectionsAndFallback()
    {
        var names = FeatureCollectionRules.Default.Collections.Select(c => c.Name).ToList();
        foreach (var expected in new[] { "File Explorer", "Start menu", "Taskbar", "Context menus", "Settings", "Window management", "Other" })
            Assert.Contains(expected, names);
        Assert.Equal("Other", names[^1]);
    }

    [Fact]
    public void ScanCatalog_RecordsBinaryNames()
    {
        var scan = new BuildScanResult
        {
            BuildText = "26100.1", Build = 26100,
            Binaries = new[] { new ScannedBinary { File = @"C:\Windows\System32\Taskbar.dll", Features = new[] { new ViVe.FeatureCatalog.Symbols.ScannedFeature("Feature_1", 77) } } },
        };
        var entry = SymbolScanService.BuildCatalog(new[] { scan }).Features.Single();
        Assert.Equal(new[] { "Taskbar.dll" }, entry.Modules);
        Assert.Equal("Taskbar", FeatureCollectionRules.Default.Assign(entry).Collection);
    }
}

public class CollectionsViewModelTests
{
    private static IFeatureCatalog Catalog() => new FeatureCatalog.FeatureCatalog(new[]
    {
        new CatalogLoadResult(new[]
        {
            Verified(101, "TaskbarAutoHide", 26100),
            Verified(102, "TaskbarEndTask", 26100),
            Verified(103, "StartMenuPins", 22000),
            new FeatureCatalogEntry { Id = 104, Name = "TaskbarOldName", VerificationStatus = VerificationStatus.NameOnly },
            new FeatureCatalogEntry { Id = 105, VerificationStatus = VerificationStatus.Unknown },
        }, Array.Empty<string>(), Array.Empty<string>()),
    });

    private static FeatureCatalogEntry Verified(uint id, string name, int build) => new()
    {
        Id = id, Name = name, Source = "test", VerificationStatus = VerificationStatus.Verified,
        Builds = new[] { new BuildCompatibility { MinBuild = build, MaxBuild = build } },
    };

    private static (CollectionsViewModel Vm, FeaturesViewModel Features, StudioHarness H) Create()
    {
        var h = new StudioHarness(Catalog());
        var features = new FeaturesViewModel(h.Engine, h.Changes, h.Catalog, h.Dialogs, h.Elevation, h.Settings);
        return (new CollectionsViewModel(h.Catalog, features, h.Engine), features, h);
    }

    [Fact]
    public void Counts_AndOnlyThisBuild()
    {
        var (vm, _, h) = Create();
        using var _h = h;
        Assert.True(vm.OnlyThisBuild);
        Assert.Equal(2, vm.Collections.Single(c => c.Name == "Taskbar").Count);
        Assert.Equal(0, vm.Collections.Single(c => c.Name == "Start menu").Count);
        Assert.Equal("Taskbar", vm.SelectedCollection!.Name);
        Assert.Equal(new uint[] { 101, 102 }, vm.Features.Select(f => f.Id).OrderBy(i => i));
        Assert.All(vm.Features, f => Assert.StartsWith("Inferred", f.ReasonText));

        vm.OnlyThisBuild = false;
        Assert.Equal(3, vm.Collections.Single(c => c.Name == "Taskbar").Count);
        Assert.Equal(1, vm.Collections.Single(c => c.Name == "Start menu").Count);
        // Entries without a name are not listed anywhere.
        Assert.Equal(4, vm.Collections.Sum(c => c.Count));
    }

    [Fact]
    public void Filter_NarrowsByNameOrId()
    {
        var (vm, _, h) = Create();
        using var _h = h;
        vm.FilterText = "endtask";
        Assert.Equal(102u, Assert.Single(vm.Features).Id);
        vm.FilterText = "101";
        Assert.Equal(101u, Assert.Single(vm.Features).Id);
    }

    [Fact]
    public async Task SendToExplorer_FillsIdsWithoutChangingAnything()
    {
        var (vm, features, h) = Create();
        using var _h = h;
        var sent = 0;
        vm.SentToExplorer += (_, _) => sent++;
        Assert.False(vm.SendToExplorerCommand.CanExecute(null));

        foreach (var f in vm.Features) f.IsSelected = true;
        vm.OnlyThisBuild = false;
        vm.SelectedCollection = vm.Collections.Single(c => c.Name == "Start menu");
        vm.Features.Single().IsSelected = true;
        Assert.Equal(3, vm.SelectedCount);
        Assert.True(vm.SendToExplorerCommand.CanExecute(null));

        vm.SendToExplorerCommand.Execute(null);

        Assert.Equal("101, 102, 103", features.FeatureInput);
        Assert.Equal(1, sent);
        Assert.Contains("preview", features.StatusMessage);
        Assert.Empty(h.Api.Runtime); // nothing was written to the feature store
        Assert.Empty(await h.History.GetEntriesAsync());
    }

    [Fact]
    public void Ticks_SurviveSwitchingCollections_AndClear()
    {
        var (vm, _, h) = Create();
        using var _h = h;
        vm.Features.First().IsSelected = true;
        vm.SelectedCollection = vm.Collections.Single(c => c.Name == "Other");
        vm.SelectedCollection = vm.Collections.Single(c => c.Name == "Taskbar");
        Assert.True(vm.Features.First().IsSelected);
        vm.ClearSelectionCommand.Execute(null);
        Assert.Equal(0, vm.SelectedCount);
        Assert.All(vm.Features, f => Assert.False(f.IsSelected));
    }

    [Fact]
    public void MainViewModel_SwitchesToExplorerAfterSend()
    {
        var (vm, features, h) = Create();
        using var _h = h;
        var settings = new SettingsViewModel(h.Settings, h.Theme, h.Dialogs, h.Catalog, h.Paths);
        var dashboard = new DashboardViewModel(h.Engine, h.History, new FakeSystemInfo(), h.Elevation, h.Settings, h.Changes);
        var history = new HistoryViewModel(h.History, h.Changes, h.Dialogs, h.Elevation);
        var main = new MainViewModel(dashboard, features, history, settings, h.Elevation, h.History, vm);
        Assert.Contains(main.Items, i => i.Title == "Collections");
        vm.Features.First().IsSelected = true;
        vm.SendToExplorerCommand.Execute(null);
        Assert.Same(features, main.SelectedItem.ViewModel);
    }
}

/// <summary>Shows how a real scan's names spread over the collections when SCAN_JSON points at a scan-&lt;build&gt;.json file.</summary>
public class RealScanCollectionTests(ITestOutputHelper output)
{
    [Fact]
    public void Distribution()
    {
        var path = Environment.GetEnvironmentVariable("SCAN_JSON");
        if (string.IsNullOrEmpty(path) || !File.Exists(path)) return;
        BuildScanResult scan;
        using (var s = File.OpenRead(path)) scan = JsonSerializer.Deserialize<BuildScanResult>(s)!;
        var catalog = SymbolScanService.BuildCatalog(new[] { scan });
        var groups = catalog.Features.Select(e => (e, a: FeatureCollectionRules.Default.Assign(e))).GroupBy(x => x.a.Collection).OrderByDescending(g => g.Count());
        var sb = new StringBuilder();
        foreach (var g in groups)
        {
            sb.AppendLine($"{g.Key}: {g.Count()} ({g.Count(x => x.a.Reason.Contains("binary it is in"))} by binary)");
            foreach (var x in g.Take(6)) sb.AppendLine($"    {x.e.Name}  [{x.a.Reason}]");
        }
        output.WriteLine(sb.ToString());
        File.WriteAllText(Path.Combine(Path.GetTempPath(), "collections-distribution.txt"), sb.ToString());
    }
}
