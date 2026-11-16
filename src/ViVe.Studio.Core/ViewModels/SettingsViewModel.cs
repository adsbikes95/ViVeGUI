// SPDX-License-Identifier: GPL-3.0-or-later
using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ViVe.FeatureCatalog;
using ViVe.Studio.Services;

namespace ViVe.Studio.ViewModels;

public sealed partial class SettingsViewModel : ObservableObject
{
    private readonly ISettingsService _settings;
    private readonly IThemeService _theme;
    private readonly IDialogService _dialogs;
    private readonly IAppPaths _paths;
    private bool _loading;

    public SettingsViewModel(ISettingsService settings, IThemeService theme, IDialogService dialogs, IFeatureCatalog catalog, IAppPaths paths)
    {
        _settings = settings;
        _theme = theme;
        _dialogs = dialogs;
        _paths = paths;
        CatalogSummary = $"{catalog.Count:N0} feature IDs known. Names come from the upstream ViVe dictionary (name only: no descriptions, categories or compatibility data). Curated, source-attributed entries: {catalog.Search(string.Empty, int.MaxValue).Count(e => e.VerificationStatus >= VerificationStatus.CommunityReported):N0}.";
        foreach (var message in catalog.LoadMessages.Take(20)) CatalogMessages.Add(message);
        LoadFrom(settings.Current);
        settings.Changed += (_, _) => LoadFrom(settings.Current);
    }

    public IReadOnlyList<ThemePreference> Themes { get; } = Enum.GetValues<ThemePreference>();
    public IReadOnlyList<StoreOption> StoreOptions => StoreOption.All;
    public ObservableCollection<string> CatalogMessages { get; } = new();
    public string CatalogSummary { get; }
    public string CatalogUpdateText => "Remote catalog updates are not available in this version. The local catalog ships with the application; you can add a local JSON catalog file below.";
    public string DataDirectory => _paths.DataDirectory;
    public string AboutText =>
        "ViVeTool Studio is a fork of ViVe / ViVeTool by @thebookisclosed and contributors (https://github.com/thebookisclosed/ViVe), " +
        "licensed under the GNU General Public License v3.0. Source code: https://github.com/adsbikes95/ViVeGUI. " +
        "This program comes with ABSOLUTELY NO WARRANTY.";

    [ObservableProperty] private ThemePreference _selectedTheme;
    [ObservableProperty] private bool _showExperimentalWarnings;
    [ObservableProperty] private bool _enableFileLogging;
    [ObservableProperty] private StoreOption _defaultStores = StoreOption.All[0];
    [ObservableProperty] private string? _userCatalogPath;
    [ObservableProperty] private string? _statusMessage;
    [ObservableProperty] private StatusSeverity _statusSeverity;

    partial void OnSelectedThemeChanged(ThemePreference value)
    {
        if (_loading) return;
        _theme.Apply(value);
        _ = SaveAsync();
    }

    partial void OnShowExperimentalWarningsChanged(bool value) { if (!_loading) _ = SaveAsync(); }
    partial void OnEnableFileLoggingChanged(bool value) { if (!_loading) _ = SaveAsync(); }
    partial void OnDefaultStoresChanged(StoreOption value) { if (!_loading) _ = SaveAsync(); }

    [RelayCommand]
    private async Task BrowseCatalogAsync()
    {
        var path = _dialogs.PickOpenFile("Select a local feature catalog", "Feature catalog (*.json)|*.json");
        if (path == null) return;
        var check = FeatureCatalogLoader.LoadJsonFile(path);
        if (check.HasErrors && check.Entries.Count == 0)
        {
            SetStatus("That file is not a valid feature catalog: " + string.Join(" ", check.Errors.Take(3)), StatusSeverity.Error);
            return;
        }
        UserCatalogPath = path;
        await SaveAsync();
        SetStatus($"Catalog file set ({check.Entries.Count} entries, {check.Errors.Count} problems). Restart ViVeTool Studio to load it.", check.HasErrors ? StatusSeverity.Warning : StatusSeverity.Success);
    }

    [RelayCommand]
    private async Task ClearCatalogAsync()
    {
        UserCatalogPath = null;
        await SaveAsync();
        SetStatus("Local catalog file removed. Restart ViVeTool Studio to apply.", StatusSeverity.Info);
    }

    [RelayCommand]
    private async Task ExportSettingsAsync()
    {
        var path = _dialogs.PickSaveFile("Export settings", "JSON files (*.json)|*.json", "vivetool-studio-settings.json");
        if (path == null) return;
        try
        {
            await _settings.ExportAsync(path);
            SetStatus("Settings exported to " + path, StatusSeverity.Success);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            SetStatus("Export failed: " + ex.Message, StatusSeverity.Error);
        }
    }

    [RelayCommand]
    private async Task ImportSettingsAsync()
    {
        var path = _dialogs.PickOpenFile("Import settings", "JSON files (*.json)|*.json");
        if (path == null) return;
        var error = await _settings.ImportAsync(path);
        if (error != null)
        {
            SetStatus(error, StatusSeverity.Error);
            return;
        }
        _theme.Apply(_settings.Current.Theme);
        SetStatus("Settings imported.", StatusSeverity.Success);
    }

    private async Task SaveAsync()
    {
        try
        {
            await _settings.SaveAsync(_settings.Current with
            {
                Theme = SelectedTheme,
                ShowExperimentalWarnings = ShowExperimentalWarnings,
                EnableFileLogging = EnableFileLogging,
                DefaultStores = DefaultStores.Stores,
                UserCatalogPath = UserCatalogPath,
            });
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            SetStatus("Settings could not be saved: " + ex.Message, StatusSeverity.Error);
        }
    }

    private void LoadFrom(AppSettings s)
    {
        _loading = true;
        try
        {
            SelectedTheme = s.Theme;
            ShowExperimentalWarnings = s.ShowExperimentalWarnings;
            EnableFileLogging = s.EnableFileLogging;
            DefaultStores = StoreOption.For(s.DefaultStores);
            UserCatalogPath = s.UserCatalogPath;
        }
        finally { _loading = false; }
    }

    private void SetStatus(string message, StatusSeverity severity)
    {
        StatusMessage = message;
        StatusSeverity = severity;
    }
}
