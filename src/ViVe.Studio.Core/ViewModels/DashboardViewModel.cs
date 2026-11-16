// SPDX-License-Identifier: GPL-3.0-or-later
using System.Collections.ObjectModel;
using Albacore.ViVe.NativeEnums;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ViVe.Core;
using ViVe.Studio.Services;

namespace ViVe.Studio.ViewModels;

public sealed partial class DashboardViewModel : ObservableObject
{
    private readonly IFeatureManager _manager;
    private readonly IChangeHistoryStore _history;
    private readonly ISystemInfoProvider _systemInfo;
    private readonly IElevationService _elevation;
    private readonly ISettingsService _settings;
    private readonly FeatureChangeService _changes;

    public DashboardViewModel(IFeatureManager manager, IChangeHistoryStore history, ISystemInfoProvider systemInfo,
        IElevationService elevation, ISettingsService settings, FeatureChangeService changes)
    {
        _manager = manager;
        _history = history;
        _systemInfo = systemInfo;
        _elevation = elevation;
        _settings = settings;
        _changes = changes;
    }

    public ObservableCollection<HistoryItemViewModel> RecentChanges { get; } = new();
    public ObservableCollection<string> Warnings { get; } = new();

    [ObservableProperty] private string _productName = "Windows";
    [ObservableProperty] private string _versionText = "-";
    [ObservableProperty] private string _buildText = "-";
    [ObservableProperty] private string _architectureText = "-";
    [ObservableProperty] private string _managementStatus = "Loading...";
    [ObservableProperty] private StatusSeverity _managementSeverity = StatusSeverity.Info;
    [ObservableProperty] private string _privilegeText = "-";
    [ObservableProperty] private string _changeStampText = "-";
    [ObservableProperty] private string _bootStatusText = "-";
    [ObservableProperty] private string _overridesText = "-";
    [ObservableProperty] private int _changesMade;
    [ObservableProperty] private bool _isLoading;

    [RelayCommand]
    public async Task RefreshAsync()
    {
        IsLoading = true;
        try
        {
            var info = _systemInfo.GetSystemInfo();
            ProductName = info.ProductName;
            VersionText = info.DisplayVersion ?? "-";
            BuildText = info.BuildDisplay;
            ArchitectureText = info.OsArchitecture == info.ProcessArchitecture
                ? info.OsArchitecture
                : $"{info.OsArchitecture} (app running as {info.ProcessArchitecture})";
            PrivilegeText = _elevation.IsElevated ? "Administrator (changes allowed)" : "Standard user (read-only; restart as administrator to make changes)";

            var status = await _manager.GetSystemStatusAsync();
            if (!status.PlatformSupported)
            {
                ManagementStatus = $"Not supported: Windows build {NativeFeatureConfigurationApi.MinimumSupportedBuild} or newer is required.";
                ManagementSeverity = StatusSeverity.Error;
            }
            else if (status.RuntimeConfigurationCount == null)
            {
                ManagementStatus = "Available, but the runtime feature store could not be queried.";
                ManagementSeverity = StatusSeverity.Warning;
            }
            else
            {
                ManagementStatus = "Available";
                ManagementSeverity = StatusSeverity.Success;
            }
            ChangeStampText = status.ChangeStamp?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "unavailable";
            BootStatusText = status.BootStatus?.ToString() ?? ("unavailable" + (status.BootStatusError is null ? string.Empty : $" ({status.BootStatusError})"));
            OverridesText = status.RuntimeConfigurationCount is int total
                ? $"{total:N0} runtime configurations, {status.RuntimeUserOverrideCount:N0} at User priority"
                : "unavailable";

            var entries = await _history.GetEntriesAsync();
            ChangesMade = entries.Count(e => e.ChangedState);
            RecentChanges.Clear();
            foreach (var entry in entries.Where(e => e.ChangedState).OrderByDescending(e => e.TimestampUtc).Take(5))
                RecentChanges.Add(new HistoryItemViewModel(entry, _changes.EvaluateRollback(entry, entries)));

            Warnings.Clear();
            if (_settings.Current.ShowExperimentalWarnings)
            {
                Warnings.Add("Feature IDs control hidden, experimental Windows functionality. Changing them is unsupported by Microsoft and can break features or make Windows unstable.");
                Warnings.Add("Enabling a feature does not guarantee any visible change. Many features also depend on other features, server-side flags or specific builds.");
                Warnings.Add("ViVeTool Studio records feature state before each change for rollback. That is not a system backup: keep a restore point or backup before experimenting.");
            }
            if (status.BootStatus == BSD_FEATURE_CONFIGURATION_STATE.BootPending)
                Warnings.Add("Boot store changes are pending: restart Windows for them to take full effect.");
            if (!_elevation.IsElevated)
                Warnings.Add("Running without administrator rights: you can query features, but changes require restarting as administrator.");
            if (_history.LoadWarning != null)
                Warnings.Add(_history.LoadWarning);
            if (_settings.LoadWarning != null)
                Warnings.Add(_settings.LoadWarning);
        }
        finally { IsLoading = false; }
    }
}
