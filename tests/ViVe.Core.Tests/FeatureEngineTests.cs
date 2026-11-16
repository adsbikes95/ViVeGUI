// SPDX-License-Identifier: GPL-3.0-or-later
using Albacore.ViVe.NativeEnums;
using ViVe.Core;
using Xunit;

namespace ViVe.Core.Tests;

public class FeatureEngineTests
{
    private const uint Id = 12345678; // arbitrary test value, not a real feature
    private readonly FakeFeatureConfigurationApi _api = new();
    private FeatureEngine CreateEngine() => new(_api, () => new DateTimeOffset(2026, 1, 2, 3, 4, 5, TimeSpan.Zero));

    [Fact]
    public async Task Query_ReturnsEffectiveAndPerPriorityEntries()
    {
        _api.Seed(FeatureStore.Runtime, new FeatureConfiguration(Id, RTL_FEATURE_CONFIGURATION_PRIORITY.Service, RTL_FEATURE_ENABLED_STATE.Disabled));
        _api.Seed(FeatureStore.Runtime, new FeatureConfiguration(Id, RTL_FEATURE_CONFIGURATION_PRIORITY.User, RTL_FEATURE_ENABLED_STATE.Enabled));
        _api.Seed(FeatureStore.Boot, new FeatureConfiguration(Id, RTL_FEATURE_CONFIGURATION_PRIORITY.User, RTL_FEATURE_ENABLED_STATE.Enabled));

        var result = await CreateEngine().QueryAsync(Id);

        Assert.True(result.Success);
        Assert.Equal(RTL_FEATURE_ENABLED_STATE.Enabled, result.Effective!.EnabledState);
        Assert.Equal(RTL_FEATURE_CONFIGURATION_PRIORITY.User, result.Effective.Priority);
        Assert.Equal(2, result.RuntimeEntries.Count);
        Assert.Single(result.BootEntries);
        Assert.True(result.BootEntriesAvailable);
    }

    [Fact]
    public async Task Query_UnknownFeature_ReportsDefaultWithoutCrashing()
    {
        var result = await CreateEngine().QueryAsync(999);
        Assert.True(result.Success);
        Assert.Null(result.Effective);
        Assert.False(result.HasAnyConfiguration);
        Assert.Equal("Default (no configuration)", result.StateSummary);
    }

    [Fact]
    public async Task Query_FailedStoreQuery_IsReportedAsFailure()
    {
        _api.FailRuntimeQuery = true;
        var result = await CreateEngine().QueryAsync(Id);
        Assert.False(result.Success);
        Assert.NotNull(result.Error);
        Assert.StartsWith("Unknown", result.StateSummary);
    }

    [Fact]
    public async Task Query_UnsupportedPlatform_Fails()
    {
        _api.IsPlatformSupported = false;
        var result = await CreateEngine().QueryAsync(Id);
        Assert.False(result.Success);
    }

    [Theory]
    [InlineData(FeatureOperationKind.Enable, RTL_FEATURE_ENABLED_STATE.Enabled)]
    [InlineData(FeatureOperationKind.Disable, RTL_FEATURE_ENABLED_STATE.Disabled)]
    public async Task EnableDisable_WritesBothStores_AndVerifies(FeatureOperationKind kind, RTL_FEATURE_ENABLED_STATE expected)
    {
        var result = await CreateEngine().ExecuteAsync(new FeatureOperationRequest(kind, Id));

        Assert.Equal(FeatureOperationStatus.Succeeded, result.Status);
        Assert.Equal(expected, _api.Runtime[(Id, RTL_FEATURE_CONFIGURATION_PRIORITY.User)].EnabledState);
        Assert.Equal(expected, _api.Boot[(Id, RTL_FEATURE_CONFIGURATION_PRIORITY.User)].EnabledState);
        Assert.Equal(new[] { FeatureStore.Runtime, FeatureStore.Boot }, _api.SetCalls.Select(c => c.Store));
        Assert.True(result.RebootRequired);
        Assert.Contains(BSD_FEATURE_CONFIGURATION_STATE.BootPending, _api.BootStatusWrites);
        Assert.NotNull(result.Before);
        Assert.Empty(result.Before!.RuntimeEntries);
        Assert.Equal(new[] { RTL_FEATURE_CONFIGURATION_PRIORITY.User }, result.PrioritiesTouched);
        Assert.Equal(expected, result.After!.Effective!.EnabledState);
    }

    [Fact]
    public async Task Enable_RuntimeOnly_DoesNotTouchBootStore()
    {
        var result = await CreateEngine().ExecuteAsync(new FeatureOperationRequest(FeatureOperationKind.Enable, Id) { Stores = FeatureStores.Runtime });
        Assert.Equal(FeatureOperationStatus.Succeeded, result.Status);
        Assert.Empty(_api.Boot);
        Assert.False(result.RebootRequired);
        Assert.Empty(_api.BootStatusWrites);
    }

    [Fact]
    public async Task Enable_WithVariantAndPriority_PassesPropertiesThrough()
    {
        var request = new FeatureOperationRequest(FeatureOperationKind.Enable, Id)
        {
            Priority = RTL_FEATURE_CONFIGURATION_PRIORITY.Test,
            Variant = 3,
            VariantPayloadKind = RTL_FEATURE_VARIANT_PAYLOAD_KIND.Resident,
            VariantPayload = 7,
            Experiment = true,
        };
        var plan = CreateEngine().Plan(request);
        Assert.Equal(OperationRisk.High, plan.Risk);

        var result = await CreateEngine().ExecuteAsync(request);

        Assert.Equal(FeatureOperationStatus.Succeeded, result.Status);
        var written = _api.Runtime[(Id, RTL_FEATURE_CONFIGURATION_PRIORITY.Test)];
        Assert.Equal(3u, written.Variant);
        Assert.Equal(7u, written.VariantPayload);
        Assert.True(written.IsExperiment);
    }

    [Fact]
    public async Task NativeFailure_IsReportedAsFailed_WithStatusCode()
    {
        _api.RuntimeSetStatus = NtStatus.AccessDenied;
        var result = await CreateEngine().ExecuteAsync(new FeatureOperationRequest(FeatureOperationKind.Enable, Id));

        Assert.Equal(FeatureOperationStatus.Failed, result.Status);
        Assert.Equal(NtStatus.AccessDenied, result.NtStatusCode);
        Assert.Equal(FeatureStore.Runtime, result.FailedStore);
        Assert.False(result.IsSuccess);
        Assert.Single(_api.SetCalls); // boot store was not attempted
    }

    [Fact]
    public async Task BootFailure_AfterRuntimeSuccess_WarnsAboutPartialChange()
    {
        _api.BootSetStatus = NtStatus.AccessDenied;
        var result = await CreateEngine().ExecuteAsync(new FeatureOperationRequest(FeatureOperationKind.Enable, Id));

        Assert.Equal(FeatureOperationStatus.Failed, result.Status);
        Assert.Equal(FeatureStore.Boot, result.FailedStore);
        Assert.Contains(result.Warnings, w => w.Contains("runtime store was already changed"));
    }

    [Fact]
    public async Task ZeroStatusWithoutEffect_IsNotReportedAsSuccess()
    {
        _api.IgnoreWrites = true;
        var result = await CreateEngine().ExecuteAsync(new FeatureOperationRequest(FeatureOperationKind.Enable, Id));

        Assert.Equal(FeatureOperationStatus.Failed, result.Status);
        Assert.Contains("does not match", result.Message);
    }

    [Fact]
    public async Task Success_WithoutReadback_IsUnverified()
    {
        _api.FailRuntimeQuery = true; // snapshot + verification queries fail
        var result = await CreateEngine().ExecuteAsync(new FeatureOperationRequest(FeatureOperationKind.Enable, Id) { Stores = FeatureStores.Runtime });

        Assert.Equal(FeatureOperationStatus.SucceededUnverified, result.Status);
        Assert.False(result.Before!.RuntimeCaptured);
    }

    [Fact]
    public async Task Reset_DefaultPriority_RemovesOnlyUserOverride()
    {
        _api.Seed(FeatureStore.Runtime, new FeatureConfiguration(Id, RTL_FEATURE_CONFIGURATION_PRIORITY.User, RTL_FEATURE_ENABLED_STATE.Enabled));
        _api.Seed(FeatureStore.Runtime, new FeatureConfiguration(Id, RTL_FEATURE_CONFIGURATION_PRIORITY.Service, RTL_FEATURE_ENABLED_STATE.Disabled));
        _api.Seed(FeatureStore.Boot, new FeatureConfiguration(Id, RTL_FEATURE_CONFIGURATION_PRIORITY.User, RTL_FEATURE_ENABLED_STATE.Enabled));

        var result = await CreateEngine().ExecuteAsync(new FeatureOperationRequest(FeatureOperationKind.Reset, Id));

        Assert.Equal(FeatureOperationStatus.Succeeded, result.Status);
        Assert.False(_api.Runtime.ContainsKey((Id, RTL_FEATURE_CONFIGURATION_PRIORITY.User)));
        Assert.True(_api.Runtime.ContainsKey((Id, RTL_FEATURE_CONFIGURATION_PRIORITY.Service)));
        Assert.Empty(_api.Boot);
    }

    [Fact]
    public async Task Reset_AllPriorities_MatchesViVeToolResetBehaviour()
    {
        _api.Seed(FeatureStore.Runtime, new FeatureConfiguration(Id, RTL_FEATURE_CONFIGURATION_PRIORITY.User, RTL_FEATURE_ENABLED_STATE.Enabled));
        _api.Seed(FeatureStore.Runtime, new FeatureConfiguration(Id, RTL_FEATURE_CONFIGURATION_PRIORITY.Service, RTL_FEATURE_ENABLED_STATE.Disabled));
        _api.Seed(FeatureStore.Runtime, new FeatureConfiguration(Id, RTL_FEATURE_CONFIGURATION_PRIORITY.ImageDefault, RTL_FEATURE_ENABLED_STATE.Disabled));

        var request = new FeatureOperationRequest(FeatureOperationKind.Reset, Id) { ResetAllPriorities = true };
        Assert.Equal(OperationRisk.High, CreateEngine().Plan(request).Risk);
        var result = await CreateEngine().ExecuteAsync(request);

        Assert.Equal(FeatureOperationStatus.Succeeded, result.Status);
        // Immutable priorities are never touched.
        Assert.Single(_api.Runtime);
        Assert.True(_api.Runtime.ContainsKey((Id, RTL_FEATURE_CONFIGURATION_PRIORITY.ImageDefault)));
    }

    [Fact]
    public async Task Reset_WithoutOverride_IsNoChange()
    {
        var result = await CreateEngine().ExecuteAsync(new FeatureOperationRequest(FeatureOperationKind.Reset, Id));
        Assert.Equal(FeatureOperationStatus.NoChange, result.Status);
        Assert.Empty(_api.SetCalls);
        Assert.True(result.IsSuccess);
        Assert.False(result.ChangedState);
    }

    [Fact]
    public async Task InvalidFeatureId_IsRejected()
    {
        var result = await CreateEngine().ExecuteAsync(new FeatureOperationRequest(FeatureOperationKind.Enable, 0));
        Assert.Equal(FeatureOperationStatus.Rejected, result.Status);
        Assert.Empty(_api.SetCalls);
    }

    [Theory]
    [InlineData(RTL_FEATURE_CONFIGURATION_PRIORITY.ImageDefault)]
    [InlineData(RTL_FEATURE_CONFIGURATION_PRIORITY.EKB)]
    [InlineData(RTL_FEATURE_CONFIGURATION_PRIORITY.Security)]
    [InlineData(RTL_FEATURE_CONFIGURATION_PRIORITY.ImageOverride)]
    public async Task ImmutablePriority_IsRejectedBeforeWriting(RTL_FEATURE_CONFIGURATION_PRIORITY priority)
    {
        var result = await CreateEngine().ExecuteAsync(new FeatureOperationRequest(FeatureOperationKind.Enable, Id) { Priority = priority });
        Assert.Equal(FeatureOperationStatus.Rejected, result.Status);
        Assert.Contains("immutable", result.Message);
        Assert.Empty(_api.SetCalls);
    }

    [Fact]
    public async Task UserPolicyWithVariant_IsRejected()
    {
        var result = await CreateEngine().ExecuteAsync(new FeatureOperationRequest(FeatureOperationKind.Enable, Id)
        { Priority = RTL_FEATURE_CONFIGURATION_PRIORITY.UserPolicy, Variant = 1 });
        Assert.Equal(FeatureOperationStatus.Rejected, result.Status);
    }

    [Fact]
    public async Task VariantOutOfRange_IsRejected()
    {
        var result = await CreateEngine().ExecuteAsync(new FeatureOperationRequest(FeatureOperationKind.Enable, Id) { Variant = 64 });
        Assert.Equal(FeatureOperationStatus.Rejected, result.Status);
    }

    [Fact]
    public async Task UnsupportedOperation_IsRejected()
    {
        var result = await CreateEngine().ExecuteAsync(new FeatureOperationRequest((FeatureOperationKind)99, Id));
        Assert.Equal(FeatureOperationStatus.Rejected, result.Status);
        Assert.Contains("Unsupported operation", result.Message);
    }

    [Fact]
    public async Task UnsupportedPlatform_IsRejected()
    {
        _api.IsPlatformSupported = false;
        var result = await CreateEngine().ExecuteAsync(new FeatureOperationRequest(FeatureOperationKind.Enable, Id));
        Assert.Equal(FeatureOperationStatus.Rejected, result.Status);
        Assert.Empty(_api.SetCalls);
    }

    [Fact]
    public async Task Restore_RecreatesPreviousOverride()
    {
        var previous = new FeatureConfiguration(Id, RTL_FEATURE_CONFIGURATION_PRIORITY.User, RTL_FEATURE_ENABLED_STATE.Disabled, Variant: 2);
        _api.Seed(FeatureStore.Runtime, previous);
        _api.Seed(FeatureStore.Boot, previous);
        var engine = CreateEngine();
        var change = await engine.ExecuteAsync(new FeatureOperationRequest(FeatureOperationKind.Enable, Id));
        Assert.Equal(FeatureOperationStatus.Succeeded, change.Status);

        var restore = await engine.ExecuteAsync(new FeatureOperationRequest(FeatureOperationKind.Restore, Id)
        {
            RestoreSnapshot = change.Before,
            RestorePriorities = change.PrioritiesTouched,
        });

        Assert.Equal(FeatureOperationStatus.Succeeded, restore.Status);
        Assert.Equal(previous, _api.Runtime[(Id, RTL_FEATURE_CONFIGURATION_PRIORITY.User)]);
        Assert.Equal(previous, _api.Boot[(Id, RTL_FEATURE_CONFIGURATION_PRIORITY.User)]);
    }

    [Fact]
    public async Task Restore_WhenNoPreviousOverride_ResetsIt()
    {
        var engine = CreateEngine();
        var change = await engine.ExecuteAsync(new FeatureOperationRequest(FeatureOperationKind.Enable, Id));
        var restore = await engine.ExecuteAsync(new FeatureOperationRequest(FeatureOperationKind.Restore, Id)
        {
            RestoreSnapshot = change.Before,
            RestorePriorities = change.PrioritiesTouched,
        });
        Assert.Equal(FeatureOperationStatus.Succeeded, restore.Status);
        Assert.Empty(_api.Runtime);
        Assert.Empty(_api.Boot);
    }

    [Fact]
    public async Task Restore_WithIncompleteSnapshot_IsRejected()
    {
        _api.FailBootRead = true;
        var engine = CreateEngine();
        var snapshot = await engine.CaptureSnapshotAsync(Id);
        Assert.False(snapshot.BootCaptured);
        _api.FailBootRead = false;

        var restore = await engine.ExecuteAsync(new FeatureOperationRequest(FeatureOperationKind.Restore, Id)
        {
            RestoreSnapshot = snapshot,
            RestorePriorities = new[] { RTL_FEATURE_CONFIGURATION_PRIORITY.User },
        });
        Assert.Equal(FeatureOperationStatus.Rejected, restore.Status);
        Assert.Contains("cannot be restored reliably", restore.Message);
    }

    [Fact]
    public async Task Batch_RunsSequentiallyAndReportsProgress()
    {
        var reported = new List<FeatureOperationResult>();
        var requests = new[] { 1u, 2u, 3u }.Select(id => new FeatureOperationRequest(FeatureOperationKind.Enable, id) { Stores = FeatureStores.Runtime });
        var results = await CreateEngine().ExecuteBatchAsync(requests, new SyncProgress(reported.Add));

        Assert.Equal(new[] { 1u, 2u, 3u }, results.Select(r => r.Request.FeatureId));
        Assert.All(results, r => Assert.Equal(FeatureOperationStatus.Succeeded, r.Status));
        Assert.Equal(3, reported.Count);
    }

    [Fact]
    public async Task Batch_CancellationStopsBeforeNextItem()
    {
        using var cts = new CancellationTokenSource();
        var reported = new List<FeatureOperationResult>();
        var requests = new[] { 1u, 2u, 3u }.Select(id => new FeatureOperationRequest(FeatureOperationKind.Enable, id) { Stores = FeatureStores.Runtime });
        var results = await CreateEngine().ExecuteBatchAsync(requests, new SyncProgress(r => { reported.Add(r); cts.Cancel(); }), cts.Token);

        Assert.Single(results);
        Assert.Single(_api.Runtime);
    }

    [Fact]
    public async Task BootStatusFileMissing_IsInitialized()
    {
        _api.BootStatusQueryResult = NtStatus.ObjectNameNotFound;
        var result = await CreateEngine().ExecuteAsync(new FeatureOperationRequest(FeatureOperationKind.Enable, Id));
        Assert.Equal(FeatureOperationStatus.Succeeded, result.Status);
        Assert.Empty(result.Warnings);
        Assert.Contains(BSD_FEATURE_CONFIGURATION_STATE.BootPending, _api.BootStatusWrites);
    }

    [Fact]
    public async Task BootStatusUpdateFailure_IsSurfacedAsWarning()
    {
        _api.SetBootStatusResult = NtStatus.AccessDenied;
        var result = await CreateEngine().ExecuteAsync(new FeatureOperationRequest(FeatureOperationKind.Enable, Id));
        Assert.Equal(FeatureOperationStatus.Succeeded, result.Status);
        Assert.Contains(result.Warnings, w => w.Contains("Last Known Good"));
    }

    [Fact]
    public async Task SystemStatus_ReportsCounts()
    {
        _api.Seed(FeatureStore.Runtime, new FeatureConfiguration(1, RTL_FEATURE_CONFIGURATION_PRIORITY.User, RTL_FEATURE_ENABLED_STATE.Enabled));
        _api.Seed(FeatureStore.Runtime, new FeatureConfiguration(2, RTL_FEATURE_CONFIGURATION_PRIORITY.Service, RTL_FEATURE_ENABLED_STATE.Enabled));
        var status = await CreateEngine().GetSystemStatusAsync();
        Assert.True(status.PlatformSupported);
        Assert.Equal(2, status.RuntimeConfigurationCount);
        Assert.Equal(1, status.RuntimeUserOverrideCount);
        Assert.Equal(42ul, status.ChangeStamp);
        Assert.Equal(BSD_FEATURE_CONFIGURATION_STATE.Committed, status.BootStatus);
    }

    private sealed class SyncProgress(Action<FeatureOperationResult> onReport) : IProgress<FeatureOperationResult>
    {
        public void Report(FeatureOperationResult value) => onReport(value);
    }
}
