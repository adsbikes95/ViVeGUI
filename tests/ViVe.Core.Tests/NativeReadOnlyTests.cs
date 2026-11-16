// SPDX-License-Identifier: GPL-3.0-or-later
using ViVe.Core;
using Xunit;

namespace ViVe.Core.Tests;

/// <summary>Runs only on Windows (skipped elsewhere).</summary>
public sealed class WindowsOnlyFactAttribute : FactAttribute
{
    public WindowsOnlyFactAttribute()
    {
        if (!OperatingSystem.IsWindows())
            Skip = "Requires Windows (ntdll feature configuration APIs).";
        else if (Environment.OSVersion.Version.Build < NativeFeatureConfigurationApi.MinimumSupportedBuild)
            Skip = $"Requires Windows build {NativeFeatureConfigurationApi.MinimumSupportedBuild} or newer.";
    }
}

/// <summary>
/// Read-only checks against the real Windows feature store (used on the Windows CI runner).
/// These tests never write: they only query, so they are safe to run anywhere.
/// </summary>
public class NativeReadOnlyTests
{
    private readonly NativeFeatureConfigurationApi _api = new();

    [WindowsOnlyFact]
    public void PlatformIsSupported() => Assert.True(_api.IsPlatformSupported);

    [WindowsOnlyFact]
    public void RuntimeStoreCanBeQueried()
    {
        var all = _api.QueryAll(FeatureStore.Runtime);
        Assert.NotNull(all);
        Assert.All(all!, c => Assert.NotEqual(0u, c.FeatureId));
    }

    [WindowsOnlyFact]
    public void ChangeStampCanBeQueried() => _api.QueryChangeStamp();

    [WindowsOnlyFact]
    public void BootOverridesCanBeRead()
    {
        var result = _api.ReadBootOverrides(1);
        Assert.True(result.Success, result.Error);
    }

    [WindowsOnlyFact]
    public async Task EngineQueryAndSnapshotWork()
    {
        var engine = new FeatureEngine(_api);
        var all = _api.QueryAll(FeatureStore.Runtime)!;
        var id = all.Count > 0 ? all[0].FeatureId : 1u;
        var query = await engine.QueryAsync(id);
        Assert.True(query.Success, query.Error);
        var snapshot = await engine.CaptureSnapshotAsync(id);
        Assert.True(snapshot.RuntimeCaptured);
        Assert.True(snapshot.BootCaptured, snapshot.CaptureError);
    }

    [WindowsOnlyFact]
    public void NtStatusDescriptionsAreTranslated()
    {
        var text = NtStatus.Describe(NtStatus.AccessDenied);
        Assert.False(string.IsNullOrWhiteSpace(text));
        Assert.DoesNotContain("0x", text);
    }
}
