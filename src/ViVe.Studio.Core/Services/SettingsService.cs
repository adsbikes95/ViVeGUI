// SPDX-License-Identifier: GPL-3.0-or-later
using System.Text.Json;
using ViVe.Core;

namespace ViVe.Studio.Services;

public sealed record AppSettings
{
    public const int CurrentSchemaVersion = 1;
    public int SchemaVersion { get; init; } = CurrentSchemaVersion;
    public ThemePreference Theme { get; init; } = ThemePreference.System;
    /// <summary>Show the dashboard banner about experimental functionality.</summary>
    public bool ShowExperimentalWarnings { get; init; } = true;
    public bool EnableFileLogging { get; init; }
    /// <summary>Stores used by Enable/Disable/Reset (ViVeTool's default is both).</summary>
    public FeatureStores DefaultStores { get; init; } = FeatureStores.Both;
    /// <summary>Optional additional local catalog JSON file.</summary>
    public string? UserCatalogPath { get; init; }

    public AppSettings Sanitize() => this with
    {
        SchemaVersion = CurrentSchemaVersion,
        Theme = Enum.IsDefined(Theme) ? Theme : ThemePreference.System,
        DefaultStores = DefaultStores is FeatureStores.Runtime or FeatureStores.Boot or FeatureStores.Both ? DefaultStores : FeatureStores.Both,
        UserCatalogPath = string.IsNullOrWhiteSpace(UserCatalogPath) ? null : UserCatalogPath.Trim(),
    };
}

public interface ISettingsService
{
    AppSettings Current { get; }
    event EventHandler? Changed;
    string? LoadWarning { get; }
    Task SaveAsync(AppSettings settings, CancellationToken cancellationToken = default);
    Task ExportAsync(string path, CancellationToken cancellationToken = default);
    /// <summary>Imports and saves settings from a file. Returns an error message, or null on success.</summary>
    Task<string?> ImportAsync(string path, CancellationToken cancellationToken = default);
}

public sealed class JsonSettingsService : ISettingsService
{
    private readonly string _path;

    public JsonSettingsService(string path)
    {
        _path = path;
        Current = Load(path, out var warning);
        LoadWarning = warning;
    }

    public AppSettings Current { get; private set; }
    public string? LoadWarning { get; }
    public event EventHandler? Changed;

    public async Task SaveAsync(AppSettings settings, CancellationToken cancellationToken = default)
    {
        var sanitized = settings.Sanitize();
        await JsonFile.WriteAtomicAsync(_path, sanitized, cancellationToken).ConfigureAwait(false);
        Current = sanitized;
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public Task ExportAsync(string path, CancellationToken cancellationToken = default) =>
        JsonFile.WriteAtomicAsync(path, Current, cancellationToken);

    public async Task<string?> ImportAsync(string path, CancellationToken cancellationToken = default)
    {
        try
        {
            if (new FileInfo(path).Length > 1024 * 1024) return "The file is too large to be a settings file.";
            await using var stream = File.OpenRead(path);
            var imported = await JsonSerializer.DeserializeAsync<AppSettings>(stream, JsonFile.Options, cancellationToken).ConfigureAwait(false);
            if (imported == null) return "The file does not contain settings.";
            if (imported.SchemaVersion > AppSettings.CurrentSchemaVersion) return $"Settings schema version {imported.SchemaVersion} is not supported.";
            await SaveAsync(imported, cancellationToken).ConfigureAwait(false);
            return null;
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            return "The settings file could not be imported: " + ex.Message;
        }
    }

    private static AppSettings Load(string path, out string? warning)
    {
        warning = null;
        if (!File.Exists(path)) return new AppSettings();
        try
        {
            var settings = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(path), JsonFile.Options);
            return (settings ?? new AppSettings()).Sanitize();
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            warning = "Settings could not be read and defaults are being used: " + ex.Message;
            return new AppSettings();
        }
    }
}
