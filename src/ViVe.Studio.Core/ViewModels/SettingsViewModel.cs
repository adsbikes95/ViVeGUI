// SPDX-License-Identifier: GPL-3.0-or-later
using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ViVe.Core;
using ViVe.FeatureCatalog;
using ViVe.FeatureCatalog.Mach2;
using ViVe.Studio.Services;

namespace ViVe.Studio.ViewModels;

public sealed partial class SettingsViewModel : ObservableObject
{
    private readonly ISettingsService _settings;
    private readonly IThemeService _theme;
    private readonly IDialogService _dialogs;
    private readonly IAppPaths _paths;
    private readonly IFeatureCatalog _catalog;
    private readonly IFeatureManager? _manager;
    private readonly Mach2ImportService? _mach2;
    private bool _loading;

    public SettingsViewModel(ISettingsService settings, IThemeService theme, IDialogService dialogs, IFeatureCatalog catalog, IAppPaths paths,
        IFeatureManager? manager = null, Mach2ImportService? mach2 = null)
    {
        _settings = settings;
        _theme = theme;
        _dialogs = dialogs;
        _paths = paths;
        _catalog = catalog;
        _manager = manager;
        _mach2 = mach2;
        RefreshCatalogInfo();
        LoadFrom(settings.Current);
        settings.Changed += (_, _) => LoadFrom(settings.Current);
    }

    public IReadOnlyList<ThemePreference> Themes { get; } = Enum.GetValues<ThemePreference>();
    public IReadOnlyList<StoreOption> StoreOptions => StoreOption.All;
    public ObservableCollection<string> CatalogMessages { get; } = new();
    [ObservableProperty] private string _catalogSummary = string.Empty;
    [ObservableProperty] private string _mach2StatusText = string.Empty;
    [ObservableProperty] private string? _mach2ProgressText;
    [ObservableProperty] private string? _crossCheckText;
    [ObservableProperty] private string? _crossCheckUnnamedText;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ImportMach2Command), nameof(ImportMach2FolderCommand), nameof(RemoveMach2Command), nameof(CrossCheckCommand))]
    private bool _isImporting;

    public bool IsMach2Available => _mach2 != null;
    public string Mach2AboutText =>
        $"mach2 by {Mach2Source.Author} ({Mach2Source.RepositoryUrl}, {Mach2Source.License}) published feature names and IDs read from Windows debug symbols, " +
        $"for {Mach2Manifest.Embedded.Entries.Count} builds. It stopped publishing on {Mach2Source.PinnedCommitDate}; the newest listed build is " +
        $"{Mach2Manifest.Embedded.Entries.Max(e => e.Info.Build)}. Imported entries record the builds each ID was listed for and mach2's staging section. " +
        "They carry no descriptions or categories, and being listed does not mean a feature is safe to change.";
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

    private bool CanImport() => !IsImporting && _mach2 != null;

    [RelayCommand(CanExecute = nameof(CanImport))]
    private async Task ImportMach2Async()
    {
        var manifest = _mach2!.Manifest;
        var mb = manifest.TotalBytes / (1024.0 * 1024.0);
        if (!_dialogs.Confirm("Import feature names from mach2",
                $"ViVeTool Studio will download {manifest.Entries.Count} feature lists ({mb:N0} MB) from raw.githubusercontent.com " +
                $"(riverar/mach2 at commit {Mach2Source.ShortCommit}). Each file is checked against a pinned SHA-256 hash and read as text only.\n\n" +
                "No feature settings are changed. Continue?", highRisk: false))
        {
            SetStatus("Import cancelled.", StatusSeverity.Info);
            return;
        }
        await RunImportAsync(p => _mach2.ImportFromGitHubAsync(p));
    }

    [RelayCommand(CanExecute = nameof(CanImport))]
    private async Task ImportMach2FolderAsync()
    {
        var folder = _dialogs.PickFolder("Select the root folder of a mach2 clone (it contains a \"features\" folder)");
        if (folder == null) return;
        await RunImportAsync(p => _mach2!.ImportFromFolderAsync(folder, p));
    }

    [RelayCommand(CanExecute = nameof(CanImport))]
    private void RemoveMach2()
    {
        if (!_mach2!.RemoveImport())
        {
            SetStatus("There is no mach2 import to remove.", StatusSeverity.Info);
            return;
        }
        ReloadCatalog();
        SetStatus("mach2 import removed.", StatusSeverity.Info);
    }

    private bool CanCrossCheck() => !IsImporting && _manager != null;

    [RelayCommand(CanExecute = nameof(CanCrossCheck))]
    private async Task CrossCheckAsync()
    {
        var ids = await _manager!.GetConfiguredFeatureIdsAsync();
        if (ids == null)
        {
            CrossCheckText = _manager.IsPlatformSupported ? "The runtime feature store could not be read." : "Feature management is not supported on this system.";
            CrossCheckUnnamedText = null;
            return;
        }
        var result = CatalogCrossCheckResult.Run(ids, _catalog, _manager.OsBuild);
        CrossCheckText = result.Summary;
        CrossCheckUnnamedText = result.UnnamedCount == 0 ? null :
            $"IDs without a name (first {result.UnnamedSample.Count}): " + string.Join(", ", result.UnnamedSample);
    }

    private async Task RunImportAsync(Func<IProgress<Mach2ImportProgress>, Task<Mach2ImportOutcome>> import)
    {
        IsImporting = true;
        try
        {
            var progress = new Progress<Mach2ImportProgress>(p => Mach2ProgressText = $"Reading {p.Completed} of {p.Total}: {p.CurrentPath}");
            Mach2ImportOutcome outcome;
            try { outcome = await import(progress); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or HttpRequestException or InvalidDataException)
            {
                SetStatus("Import failed: " + ex.Message, StatusSeverity.Error);
                return;
            }
            Mach2ProgressText = outcome.Problems.Count == 0 ? null : string.Join(Environment.NewLine, outcome.Problems.Take(10));
            if (outcome.Success) ReloadCatalog();
            SetStatus(outcome.Message, !outcome.Success ? StatusSeverity.Error : outcome.Problems.Count > 0 ? StatusSeverity.Warning : StatusSeverity.Success);
        }
        finally { IsImporting = false; }
    }

    private void ReloadCatalog()
    {
        if (_catalog is ReloadableFeatureCatalog reloadable) reloadable.Reload();
        RefreshCatalogInfo();
    }

    private void RefreshCatalogInfo()
    {
        var all = _catalog.Search(string.Empty, int.MaxValue);
        var verified = all.Count(e => e.VerificationStatus == VerificationStatus.Verified);
        var reported = all.Count(e => e.VerificationStatus == VerificationStatus.CommunityReported);
        CatalogSummary = $"{_catalog.Count:N0} feature IDs known, {all.Count(e => e.Name != null):N0} with a name. " +
                         $"Names come from the upstream ViVe dictionary (name only) and, if imported, from mach2. " +
                         $"Entries with verified build data: {verified:N0}. Community-reported entries: {reported:N0}. " +
                         "No entry has a description or category unless a cited source provides one.";
        CatalogMessages.Clear();
        foreach (var message in _catalog.LoadMessages.Take(20)) CatalogMessages.Add(message);
        if (_mach2 == null) { Mach2StatusText = string.Empty; return; }
        var status = _mach2.GetStatus();
        Mach2StatusText = status.IsImported
            ? $"Imported: {status.FeatureCount:N0} feature IDs (file updated {status.FileTimeUtc?.ToLocalTime():g}). {status.Description}"
            : "Not imported yet.";
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
