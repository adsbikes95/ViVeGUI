// SPDX-License-Identifier: GPL-3.0-or-later
using Albacore.ViVe.NativeEnums;
using ViVe.Core;
using ViVe.Studio.Services;
using ViVe.Studio.ViewModels;
using Xunit;

namespace ViVe.Studio.Tests;

public class FeaturesViewModelTests
{
    private const uint Id = 12345678;

    private static FeaturesViewModel Create(StudioHarness h) =>
        new(h.Engine, h.Changes, h.Catalog, h.Dialogs, h.Elevation, h.Settings);

    [Fact]
    public async Task Query_ShowsStateWithoutChangingAnything()
    {
        using var h = new StudioHarness();
        h.Api.Seed(FeatureStore.Runtime, new FeatureConfiguration(Id, RTL_FEATURE_CONFIGURATION_PRIORITY.User, RTL_FEATURE_ENABLED_STATE.Enabled));
        var vm = Create(h);
        vm.FeatureInput = Id.ToString();

        await vm.QueryCommand.ExecuteAsync(null);

        var item = Assert.Single(vm.Results);
        Assert.Equal("TestOnlyFeature", item.Name);
        Assert.Contains("Enabled", item.StateText);
        Assert.Empty(h.Api.SetCalls);
        Assert.Empty(await h.History.GetEntriesAsync());
        Assert.Equal(StatusSeverity.Success, vm.StatusSeverity);
    }

    [Fact]
    public async Task InvalidInput_ShowsErrorAndDoesNothing()
    {
        using var h = new StudioHarness();
        var vm = Create(h);
        vm.FeatureInput = "12; DROP TABLE";
        await vm.EnableCommand.ExecuteAsync(null);
        Assert.Equal(StatusSeverity.Error, vm.StatusSeverity);
        Assert.Empty(h.Dialogs.Confirms);
        Assert.Empty(h.Api.SetCalls);
    }

    [Fact]
    public async Task NotElevated_OffersRestart_AndDoesNotModify()
    {
        using var h = new StudioHarness();
        h.Elevation.IsElevated = false;
        h.Dialogs.ConfirmAnswers.Enqueue(true);
        var vm = Create(h);
        vm.FeatureInput = Id.ToString();

        await vm.EnableCommand.ExecuteAsync(null);

        Assert.Equal(1, h.Elevation.RestartRequests);
        Assert.Empty(h.Api.SetCalls);
    }

    [Fact]
    public async Task Declined_Confirmation_ChangesNothing()
    {
        using var h = new StudioHarness();
        h.Dialogs.ConfirmAnswers.Enqueue(false);
        var vm = Create(h);
        vm.FeatureInput = Id.ToString();

        await vm.EnableCommand.ExecuteAsync(null);

        var confirm = Assert.Single(h.Dialogs.Confirms);
        Assert.Contains("Current state: Default", confirm.Message);
        Assert.Contains("not been verified", confirm.Message);
        Assert.Contains("not a system backup", confirm.Message);
        Assert.False(confirm.HighRisk);
        Assert.Empty(h.Api.SetCalls);
        Assert.Empty(await h.History.GetEntriesAsync());
    }

    [Fact]
    public async Task Confirmed_Enable_AppliesRecordsAndReports()
    {
        using var h = new StudioHarness();
        h.Dialogs.ConfirmAnswers.Enqueue(true);
        var vm = Create(h);
        vm.FeatureInput = $"{Id}, 0x10";

        await vm.EnableCommand.ExecuteAsync(null);

        Assert.Equal(RTL_FEATURE_ENABLED_STATE.Enabled, h.Api.Runtime[(Id, RTL_FEATURE_CONFIGURATION_PRIORITY.User)].EnabledState);
        Assert.True(h.Api.Runtime.ContainsKey((16, RTL_FEATURE_CONFIGURATION_PRIORITY.User)));
        Assert.Equal(2, vm.Results.Count);
        Assert.All(vm.Results, r => Assert.Equal(StatusSeverity.Success, r.Severity));
        Assert.Equal(2, (await h.History.GetEntriesAsync()).Count);
        Assert.Equal(StatusSeverity.Success, vm.StatusSeverity);
        Assert.Contains("Restart Windows", vm.StatusMessage);
    }

    [Fact]
    public async Task NonDefaultPriority_IsConfirmedAsHighRisk()
    {
        using var h = new StudioHarness();
        h.Dialogs.ConfirmAnswers.Enqueue(true);
        var vm = Create(h);
        vm.FeatureInput = Id.ToString();
        vm.SelectedPriority = vm.Priorities.Single(p => p.Priority == RTL_FEATURE_CONFIGURATION_PRIORITY.Service);

        await vm.DisableCommand.ExecuteAsync(null);

        Assert.True(Assert.Single(h.Dialogs.Confirms).HighRisk);
        Assert.Equal(RTL_FEATURE_ENABLED_STATE.Disabled, h.Api.Runtime[(Id, RTL_FEATURE_CONFIGURATION_PRIORITY.Service)].EnabledState);
    }

    [Fact]
    public async Task EngineFailure_IsShownAsError()
    {
        using var h = new StudioHarness();
        h.Api.RuntimeSetStatus = NtStatus.AccessDenied;
        h.Dialogs.ConfirmAnswers.Enqueue(true);
        var vm = Create(h);
        vm.FeatureInput = Id.ToString();

        await vm.EnableCommand.ExecuteAsync(null);

        Assert.Equal(StatusSeverity.Error, vm.StatusSeverity);
        Assert.Equal(StatusSeverity.Error, Assert.Single(vm.Results).Severity);
        Assert.Equal(FeatureOperationStatus.Failed, Assert.Single(await h.History.GetEntriesAsync()).Status);
    }

    [Fact]
    public async Task InvalidVariant_IsRejectedBeforeConfirmation()
    {
        using var h = new StudioHarness();
        var vm = Create(h);
        vm.FeatureInput = Id.ToString();
        vm.VariantText = "64";
        await vm.EnableCommand.ExecuteAsync(null);
        Assert.Empty(h.Dialogs.Confirms);
        Assert.Equal(StatusSeverity.Error, vm.StatusSeverity);
    }

    [Fact]
    public async Task Reset_WithoutOverride_ReportsNoChange()
    {
        using var h = new StudioHarness();
        h.Dialogs.ConfirmAnswers.Enqueue(true);
        var vm = Create(h);
        vm.FeatureInput = Id.ToString();
        await vm.ResetCommand.ExecuteAsync(null);
        Assert.Contains("no change", vm.StatusMessage);
        Assert.Empty(h.Api.SetCalls);
    }

    [Fact]
    public void Search_FindsByNameAndAddsToInput()
    {
        using var h = new StudioHarness();
        var vm = Create(h);
        vm.SearchText = "testonly";
        var entry = Assert.Single(vm.SearchResults);
        vm.AddToInputCommand.Execute(entry);
        vm.AddToInputCommand.Execute(entry); // no duplicates
        Assert.Equal(Id.ToString(), vm.FeatureInput);
    }
}

public class HistoryAndDashboardViewModelTests
{
    [Fact]
    public async Task HistoryViewModel_RollbackFlow()
    {
        using var h = new StudioHarness();
        await h.Changes.ExecuteAsync(new FeatureOperationRequest(FeatureOperationKind.Enable, 7));
        var vm = new HistoryViewModel(h.History, h.Changes, h.Dialogs, h.Elevation);
        await vm.RefreshAsync();
        var item = Assert.Single(vm.Entries);
        Assert.True(item.CanRollback);

        h.Dialogs.ConfirmAnswers.Enqueue(true);
        await vm.RollbackCommand.ExecuteAsync(item);

        Assert.Empty(h.Api.Runtime);
        Assert.Equal(2, vm.Entries.Count);
        Assert.All(vm.Entries, e => Assert.False(e.CanRollback));
        Assert.Equal(StatusSeverity.Success, vm.StatusSeverity);
    }

    [Fact]
    public async Task HistoryViewModel_Export()
    {
        using var h = new StudioHarness();
        await h.Changes.ExecuteAsync(new FeatureOperationRequest(FeatureOperationKind.Enable, 7));
        h.Dialogs.SavePath = h.Temp.File("out.json");
        var vm = new HistoryViewModel(h.History, h.Changes, h.Dialogs, h.Elevation);
        await vm.RefreshAsync();
        await vm.ExportCommand.ExecuteAsync(null);
        Assert.True(File.Exists(h.Dialogs.SavePath));
        Assert.Equal(StatusSeverity.Success, vm.StatusSeverity);
    }

    [Fact]
    public async Task Dashboard_ShowsSystemAndChangeCounts()
    {
        using var h = new StudioHarness();
        await h.Changes.ExecuteAsync(new FeatureOperationRequest(FeatureOperationKind.Enable, 7));
        await h.Changes.ExecuteAsync(new FeatureOperationRequest(FeatureOperationKind.Enable, 8) { Priority = RTL_FEATURE_CONFIGURATION_PRIORITY.EKB }); // rejected
        h.Elevation.IsElevated = false;
        var vm = new DashboardViewModel(h.Engine, h.History, new FakeSystemInfo(), h.Elevation, h.Settings, h.Changes);

        await vm.RefreshAsync();

        Assert.Equal("Windows 11 Pro", vm.ProductName);
        Assert.Equal("26100.2033", vm.BuildText);
        Assert.Equal("X64", vm.ArchitectureText);
        Assert.Equal(1, vm.ChangesMade);
        Assert.Single(vm.RecentChanges);
        Assert.Equal("Available", vm.ManagementStatus);
        Assert.Contains(vm.Warnings, w => w.Contains("administrator"));
        Assert.Contains(vm.Warnings, w => w.Contains("pending"));
        Assert.Contains(vm.Warnings, w => w.Contains("experimental"));
    }

    [Fact]
    public async Task Dashboard_UnsupportedPlatform()
    {
        using var h = new StudioHarness();
        h.Api.IsPlatformSupported = false;
        var vm = new DashboardViewModel(h.Engine, h.History, new FakeSystemInfo(), h.Elevation, h.Settings, h.Changes);
        await vm.RefreshAsync();
        Assert.Equal(StatusSeverity.Error, vm.ManagementSeverity);
    }

    [Fact]
    public async Task Settings_ThemeChange_AppliesAndPersists()
    {
        using var h = new StudioHarness();
        var vm = new SettingsViewModel(h.Settings, h.Theme, h.Dialogs, h.Catalog, h.Paths);
        Assert.Empty(h.Theme.Applied); // loading does not re-apply or save
        vm.SelectedTheme = ThemePreference.Light;
        for (var i = 0; i < 100 && h.Settings.Current.Theme != ThemePreference.Light; i++)
            await Task.Delay(20);
        Assert.Equal(new[] { ThemePreference.Light }, h.Theme.Applied);
        Assert.Equal(ThemePreference.Light, new JsonSettingsService(h.Paths.SettingsFile).Current.Theme);
    }
}
