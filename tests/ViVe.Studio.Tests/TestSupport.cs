// SPDX-License-Identifier: GPL-3.0-or-later
using ViVe.Core;
using ViVe.Core.Tests;
using ViVe.FeatureCatalog;
using ViVe.Studio.Services;

namespace ViVe.Studio.Tests;

public sealed class TempDirectory : IDisposable
{
    public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "vivestudio-tests-" + Guid.NewGuid().ToString("N"));
    public TempDirectory() => Directory.CreateDirectory(Path);
    public string File(string name) => System.IO.Path.Combine(Path, name);
    public void Dispose() { try { Directory.Delete(Path, true); } catch (IOException) { } }
}

public sealed class FakeDialogs : IDialogService
{
    public Queue<bool> ConfirmAnswers { get; } = new();
    public List<(string Title, string Message, bool HighRisk)> Confirms { get; } = new();
    public string? SavePath { get; set; }
    public string? OpenPath { get; set; }

    public bool Confirm(string title, string message, bool highRisk)
    {
        Confirms.Add((title, message, highRisk));
        return ConfirmAnswers.Count > 0 && ConfirmAnswers.Dequeue();
    }

    public void ShowMessage(string title, string message, bool isError) { }
    public string? PickSaveFile(string title, string filter, string defaultFileName) => SavePath;
    public string? PickOpenFile(string title, string filter) => OpenPath;
    public string? FolderPath { get; set; }
    public string? PickFolder(string title) => FolderPath;
}

public sealed class FakeElevation : IElevationService
{
    public bool IsElevated { get; set; } = true;
    public int RestartRequests { get; private set; }
    public bool RestartResult { get; set; } = true;
    public bool TryRestartElevated() { RestartRequests++; return RestartResult; }
}

public sealed class FakeTheme : IThemeService
{
    public List<ThemePreference> Applied { get; } = new();
    public void Apply(ThemePreference preference) => Applied.Add(preference);
}

public sealed class FakeSystemInfo : ISystemInfoProvider
{
    public SystemInfo GetSystemInfo() => new("Windows 11 Pro", "24H2", 26100, 2033, "X64", "X64", true);
}

/// <summary>Wires the real engine to the in-memory fake store, plus temp-directory history and settings.</summary>
public sealed class StudioHarness : IDisposable
{
    public TempDirectory Temp { get; } = new();
    public FakeFeatureConfigurationApi Api { get; } = new();
    public FeatureEngine Engine { get; }
    public JsonChangeHistoryStore History { get; }
    public JsonSettingsService Settings { get; }
    public FakeDialogs Dialogs { get; } = new();
    public FakeElevation Elevation { get; } = new();
    public FakeTheme Theme { get; } = new();
    public IFeatureCatalog Catalog { get; }
    public FeatureChangeService Changes { get; }
    public AppPaths Paths { get; }
    private DateTimeOffset _now = new(2026, 11, 16, 12, 0, 0, TimeSpan.Zero);

    public StudioHarness(IFeatureCatalog? catalog = null)
    {
        Func<DateTimeOffset> clock = () => _now = _now.AddSeconds(1);
        Engine = new FeatureEngine(Api, clock);
        Paths = new AppPaths(Temp.Path, AppContext.BaseDirectory);
        History = new JsonChangeHistoryStore(Paths.HistoryFile, clock);
        Settings = new JsonSettingsService(Paths.SettingsFile);
        Catalog = catalog ?? new FeatureCatalog.FeatureCatalog(new[]
        {
            new CatalogLoadResult(new[] { new FeatureCatalogEntry { Id = 12345678, Name = "TestOnlyFeature", VerificationStatus = VerificationStatus.NameOnly, Source = "unit test" } },
                Array.Empty<string>(), Array.Empty<string>()),
        });
        Changes = new FeatureChangeService(Engine, History, Catalog, new FakeSystemInfo(), NullLogger.Instance);
    }

    public void Dispose() => Temp.Dispose();
}
