// SPDX-License-Identifier: GPL-3.0-or-later
using System.Text.Json;
using Albacore.ViVe.NativeEnums;
using ViVe.Core;
using ViVe.Studio.Models;
using ViVe.Studio.Services;
using Xunit;

namespace ViVe.Studio.Tests;

public class ChangeHistoryStoreTests
{
    [Fact]
    public async Task Entries_PersistAcrossInstances_IncludingSnapshot()
    {
        using var harness = new StudioHarness();
        harness.Api.Seed(FeatureStore.Runtime, new FeatureConfiguration(12345678, RTL_FEATURE_CONFIGURATION_PRIORITY.User, RTL_FEATURE_ENABLED_STATE.Disabled, Variant: 3));
        await harness.Changes.ExecuteAsync(new FeatureOperationRequest(FeatureOperationKind.Enable, 12345678) { Stores = FeatureStores.Runtime });

        var reloaded = await new JsonChangeHistoryStore(harness.Paths.HistoryFile).GetEntriesAsync();
        var entry = Assert.Single(reloaded);
        Assert.Equal(12345678u, entry.FeatureId);
        Assert.Equal("TestOnlyFeature", entry.FeatureName);
        Assert.Equal(FeatureOperationKind.Enable, entry.Operation);
        Assert.Equal(FeatureOperationStatus.Succeeded, entry.Status);
        Assert.Equal(26100, entry.OsBuild);
        Assert.Equal("24H2", entry.OsDisplayVersion);
        Assert.Equal(RTL_FEATURE_CONFIGURATION_PRIORITY.User, entry.Priority);
        var previous = Assert.Single(entry.Before!.RuntimeEntries);
        Assert.Equal(RTL_FEATURE_ENABLED_STATE.Disabled, previous.EnabledState);
        Assert.Equal(3u, previous.Variant);
    }

    [Fact]
    public async Task RejectedOperations_AreAlsoRecorded()
    {
        using var harness = new StudioHarness();
        await harness.Changes.ExecuteAsync(new FeatureOperationRequest(FeatureOperationKind.Enable, 1) { Priority = RTL_FEATURE_CONFIGURATION_PRIORITY.ImageDefault });
        var entry = Assert.Single(await harness.History.GetEntriesAsync());
        Assert.Equal(FeatureOperationStatus.Rejected, entry.Status);
        Assert.False(entry.ChangedState);
    }

    [Fact]
    public async Task CorruptFile_IsMovedAsideNotOverwritten()
    {
        using var temp = new TempDirectory();
        var path = temp.File("history.json");
        await File.WriteAllTextAsync(path, "{ this is not json");
        var store = new JsonChangeHistoryStore(path);

        Assert.Empty(await store.GetEntriesAsync());
        Assert.NotNull(store.LoadWarning);
        Assert.Single(Directory.GetFiles(temp.Path, "history.json.corrupt-*"));
        await store.AppendAsync(new ChangeHistoryEntry { FeatureId = 1, Message = "x" });
        Assert.Single(await new JsonChangeHistoryStore(path).GetEntriesAsync());
    }

    [Fact]
    public async Task Export_WritesVersionedJsonDocument()
    {
        using var harness = new StudioHarness();
        await harness.Changes.ExecuteAsync(new FeatureOperationRequest(FeatureOperationKind.Disable, 5) { Stores = FeatureStores.Runtime });
        var exportPath = harness.Temp.File("export.json");
        await harness.History.ExportAsync(exportPath);

        using var doc = JsonDocument.Parse(await File.ReadAllTextAsync(exportPath));
        Assert.Equal(1, doc.RootElement.GetProperty("schemaVersion").GetInt32());
        Assert.True(doc.RootElement.TryGetProperty("exportedAtUtc", out _));
        var entry = doc.RootElement.GetProperty("entries")[0];
        Assert.Equal("Disable", entry.GetProperty("operation").GetString());
        Assert.Equal(5u, entry.GetProperty("featureId").GetUInt32());
    }

    [Fact]
    public async Task ChangedEvent_IsRaisedOnAppend()
    {
        using var temp = new TempDirectory();
        var store = new JsonChangeHistoryStore(temp.File("h.json"));
        var raised = 0;
        store.Changed += (_, _) => raised++;
        await store.AppendAsync(new ChangeHistoryEntry { FeatureId = 1 });
        Assert.Equal(1, raised);
    }
}

public class RollbackTests
{
    private const uint Id = 12345678;

    [Fact]
    public async Task Rollback_RestoresPreviousState_AndMarksEntry()
    {
        using var harness = new StudioHarness();
        var original = new FeatureConfiguration(Id, RTL_FEATURE_CONFIGURATION_PRIORITY.User, RTL_FEATURE_ENABLED_STATE.Disabled);
        harness.Api.Seed(FeatureStore.Runtime, original);
        harness.Api.Seed(FeatureStore.Boot, original);
        await harness.Changes.ExecuteAsync(new FeatureOperationRequest(FeatureOperationKind.Enable, Id));
        var change = Assert.Single(await harness.History.GetEntriesAsync());
        Assert.True(harness.Changes.EvaluateRollback(change, new[] { change }).CanRollback);

        var result = await harness.Changes.RollbackAsync(change);

        Assert.Equal(FeatureOperationStatus.Succeeded, result!.Status);
        Assert.Equal(original, harness.Api.Runtime[(Id, RTL_FEATURE_CONFIGURATION_PRIORITY.User)]);
        Assert.Equal(original, harness.Api.Boot[(Id, RTL_FEATURE_CONFIGURATION_PRIORITY.User)]);
        var history = await harness.History.GetEntriesAsync();
        Assert.Equal(2, history.Count);
        var marked = history.Single(e => e.Id == change.Id);
        var restore = history.Single(e => e.Id != change.Id);
        Assert.Equal(restore.Id, marked.RolledBackBy);
        Assert.Equal(change.Id, restore.RollbackOf);
        Assert.False(harness.Changes.EvaluateRollback(marked, history).CanRollback);
        Assert.False(harness.Changes.EvaluateRollback(restore, history).CanRollback);
    }

    [Fact]
    public async Task Rollback_NotAllowed_WhenLaterChangeExists()
    {
        using var harness = new StudioHarness();
        await harness.Changes.ExecuteAsync(new FeatureOperationRequest(FeatureOperationKind.Enable, Id));
        await harness.Changes.ExecuteAsync(new FeatureOperationRequest(FeatureOperationKind.Disable, Id));
        var history = await harness.History.GetEntriesAsync();
        var first = history.OrderBy(e => e.TimestampUtc).First();
        var latest = history.OrderBy(e => e.TimestampUtc).Last();
        Assert.False(harness.Changes.EvaluateRollback(first, history).CanRollback);
        Assert.True(harness.Changes.EvaluateRollback(latest, history).CanRollback);
        Assert.Null(await harness.Changes.RollbackAsync(first));
    }

    [Fact]
    public async Task Rollback_NotAllowed_WhenSnapshotIncomplete()
    {
        using var harness = new StudioHarness();
        harness.Api.FailBootRead = true;
        await harness.Changes.ExecuteAsync(new FeatureOperationRequest(FeatureOperationKind.Enable, Id));
        var entry = Assert.Single(await harness.History.GetEntriesAsync());
        var eligibility = harness.Changes.EvaluateRollback(entry, new[] { entry });
        Assert.False(eligibility.CanRollback);
        Assert.Contains("reliably", eligibility.Reason);
    }

    [Fact]
    public void Rollback_NotAllowed_AfterWindowsUpdate()
    {
        var entry = new ChangeHistoryEntry
        {
            FeatureId = Id, OsBuild = 22631, Status = FeatureOperationStatus.Succeeded, Stores = FeatureStores.Runtime,
            Before = new FeatureSnapshot(Id, DateTimeOffset.UtcNow, 22631, true, Array.Empty<FeatureConfiguration>(), true, Array.Empty<FeatureConfiguration>()),
            PrioritiesTouched = new[] { RTL_FEATURE_CONFIGURATION_PRIORITY.User },
        };
        Assert.False(RollbackPolicy.Evaluate(entry, new[] { entry }, 26100).CanRollback);
        Assert.True(RollbackPolicy.Evaluate(entry, new[] { entry }, 22631).CanRollback);
    }

    [Theory]
    [InlineData(FeatureOperationStatus.Failed)]
    [InlineData(FeatureOperationStatus.Rejected)]
    [InlineData(FeatureOperationStatus.NoChange)]
    public void Rollback_NotAllowed_WhenNothingChanged(FeatureOperationStatus status)
    {
        var entry = new ChangeHistoryEntry { FeatureId = Id, Status = status };
        Assert.False(RollbackPolicy.Evaluate(entry, new[] { entry }, 0).CanRollback);
    }
}

public class SettingsServiceTests
{
    [Fact]
    public async Task Settings_RoundTrip_ExportImport()
    {
        using var temp = new TempDirectory();
        var service = new JsonSettingsService(temp.File("settings.json"));
        Assert.Equal(ThemePreference.System, service.Current.Theme);
        await service.SaveAsync(service.Current with { Theme = ThemePreference.Dark, EnableFileLogging = true, DefaultStores = FeatureStores.Runtime });

        Assert.Equal(ThemePreference.Dark, new JsonSettingsService(temp.File("settings.json")).Current.Theme);

        var export = temp.File("export.json");
        await service.ExportAsync(export);
        var other = new JsonSettingsService(temp.File("other.json"));
        Assert.Null(await other.ImportAsync(export));
        Assert.Equal(ThemePreference.Dark, other.Current.Theme);
        Assert.Equal(FeatureStores.Runtime, other.Current.DefaultStores);
    }

    [Fact]
    public async Task Import_InvalidFile_ReturnsErrorAndKeepsSettings()
    {
        using var temp = new TempDirectory();
        var bad = temp.File("bad.json");
        await File.WriteAllTextAsync(bad, "[1,2,3]");
        var service = new JsonSettingsService(temp.File("settings.json"));
        Assert.NotNull(await service.ImportAsync(bad));
        Assert.Equal(ThemePreference.System, service.Current.Theme);
    }

    [Fact]
    public void CorruptSettings_FallBackToDefaultsWithWarning()
    {
        using var temp = new TempDirectory();
        File.WriteAllText(temp.File("settings.json"), "{{{");
        var service = new JsonSettingsService(temp.File("settings.json"));
        Assert.NotNull(service.LoadWarning);
        Assert.Equal(new AppSettings(), service.Current);
    }

    [Fact]
    public void Sanitize_FixesOutOfRangeValues()
    {
        var s = new AppSettings { Theme = (ThemePreference)42, DefaultStores = FeatureStores.None, UserCatalogPath = "  " }.Sanitize();
        Assert.Equal(ThemePreference.System, s.Theme);
        Assert.Equal(FeatureStores.Both, s.DefaultStores);
        Assert.Null(s.UserCatalogPath);
    }
}
