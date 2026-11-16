// SPDX-License-Identifier: GPL-3.0-or-later
namespace ViVe.Studio.Services;

public enum ThemePreference { System, Light, Dark }

/// <summary>Information about the running Windows installation, for display only.</summary>
public sealed record SystemInfo(
    string ProductName,
    string? DisplayVersion,
    int Build,
    int? Revision,
    string OsArchitecture,
    string ProcessArchitecture,
    bool IsWindows)
{
    public string BuildDisplay => Revision is int ubr ? $"{Build}.{ubr}" : Build.ToString(System.Globalization.CultureInfo.InvariantCulture);
}

public interface ISystemInfoProvider
{
    SystemInfo GetSystemInfo();
}

/// <summary>Administrator elevation. Studio runs unelevated (read-only) and restarts elevated on request.</summary>
public interface IElevationService
{
    bool IsElevated { get; }

    /// <summary>Starts an elevated instance (UAC prompt). Returns false if the user declined or it failed.</summary>
    bool TryRestartElevated();
}

public interface IDialogService
{
    /// <summary>Asks the user to confirm. <paramref name="highRisk"/> uses a warning presentation.</summary>
    bool Confirm(string title, string message, bool highRisk);

    void ShowMessage(string title, string message, bool isError);

    string? PickSaveFile(string title, string filter, string defaultFileName);

    string? PickOpenFile(string title, string filter);
}

public interface IThemeService
{
    void Apply(ThemePreference preference);
}

public interface IAppPaths
{
    string DataDirectory { get; }
    string HistoryFile { get; }
    string SettingsFile { get; }
    string LogDirectory { get; }
    /// <summary>Directory holding the bundled catalog files (next to the executable).</summary>
    string CatalogDirectory { get; }
}

public enum LogLevel { Debug, Information, Warning, Error }

public interface IAppLogger
{
    void Log(LogLevel level, string message);
}

/// <summary>Logger that discards everything.</summary>
public sealed class NullLogger : IAppLogger
{
    public static NullLogger Instance { get; } = new();
    public void Log(LogLevel level, string message) { }
}
