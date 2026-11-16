// SPDX-License-Identifier: GPL-3.0-or-later
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ViVe.Studio.Services;

namespace ViVe.Studio.ViewModels;

public sealed record NavigationItem(string Title, string Glyph, object ViewModel, string AutomationName);

public sealed partial class MainViewModel : ObservableObject
{
    private readonly IElevationService _elevation;
    private readonly DashboardViewModel _dashboard;
    private readonly HistoryViewModel _history;

    public MainViewModel(DashboardViewModel dashboard, FeaturesViewModel features, HistoryViewModel history, SettingsViewModel settings,
        IElevationService elevation, IChangeHistoryStore historyStore)
    {
        _elevation = elevation;
        _dashboard = dashboard;
        _history = history;
        // Glyphs are Segoe Fluent Icons code points.
        Items = new[]
        {
            new NavigationItem("Dashboard", "", dashboard, "Dashboard"),
            new NavigationItem("Feature Explorer", "", features, "Feature Explorer"),
            new NavigationItem("Change History", "", history, "Change History"),
            new NavigationItem("Settings", "", settings, "Settings"),
        };
        _selectedItem = Items[0];
        historyStore.Changed += (_, _) => _historyDirty = true;
    }

    private bool _historyDirty = true;

    public IReadOnlyList<NavigationItem> Items { get; }

    public bool IsElevated => _elevation.IsElevated;
    public string Title => IsElevated ? "ViVeTool Studio (Administrator)" : "ViVeTool Studio";

    [ObservableProperty] private NavigationItem _selectedItem;

    partial void OnSelectedItemChanged(NavigationItem value)
    {
        // Refresh pages that show history when they are opened.
        if (value.ViewModel == _dashboard) _ = _dashboard.RefreshAsync();
        else if (value.ViewModel == _history && _historyDirty)
        {
            _historyDirty = false;
            _ = _history.RefreshAsync();
        }
    }

    public async Task InitializeAsync()
    {
        await _dashboard.RefreshAsync();
    }

    [RelayCommand]
    private void RestartElevated() => _elevation.TryRestartElevated();
}
