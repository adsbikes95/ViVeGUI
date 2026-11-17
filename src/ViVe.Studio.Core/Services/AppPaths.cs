// SPDX-License-Identifier: GPL-3.0-or-later
namespace ViVe.Studio.Services;

/// <summary>Default locations: %LOCALAPPDATA%\ViVeToolStudio for data, the app directory for bundled catalogs.</summary>
public sealed class AppPaths : IAppPaths
{
    public AppPaths(string? dataDirectory = null, string? catalogDirectory = null)
    {
        DataDirectory = dataDirectory ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ViVeToolStudio");
        CatalogDirectory = catalogDirectory ?? AppContext.BaseDirectory;
    }

    public string DataDirectory { get; }
    public string HistoryFile => Path.Combine(DataDirectory, "history.json");
    public string SettingsFile => Path.Combine(DataDirectory, "settings.json");
    public string LogDirectory => Path.Combine(DataDirectory, "logs");
    public string CatalogDirectory { get; }
    public string ImportedCatalogDirectory => Path.Combine(DataDirectory, "catalog");
}
