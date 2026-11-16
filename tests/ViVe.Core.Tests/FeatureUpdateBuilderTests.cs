// SPDX-License-Identifier: GPL-3.0-or-later
using Albacore.ViVe;
using Albacore.ViVe.Exceptions;
using Albacore.ViVe.NativeEnums;
using Albacore.ViVe.NativeStructs;
using ViVe.Core;
using Xunit;

namespace ViVe.Core.Tests;

public class FeatureUpdateBuilderTests
{
    [Fact]
    public void SetUpdates_DefaultToUserPriority_WithStateAndVariantOperation()
    {
        var updates = FeatureUpdateBuilder.CreateSetUpdates(new uint[] { 1, 2 }, RTL_FEATURE_ENABLED_STATE.Enabled, null);
        Assert.Equal(2, updates.Length);
        Assert.All(updates, u =>
        {
            Assert.Equal(RTL_FEATURE_CONFIGURATION_PRIORITY.User, u.Priority);
            Assert.Equal(RTL_FEATURE_ENABLED_STATE.Enabled, u.EnabledState);
            Assert.Equal(RTL_FEATURE_CONFIGURATION_OPERATION.FeatureState | RTL_FEATURE_CONFIGURATION_OPERATION.VariantState, u.Operation);
        });
    }

    [Fact]
    public void SetUpdates_VariantOverflow_Throws()
    {
        Assert.Throws<FeaturePropertyOverflowException>(() =>
            FeatureUpdateBuilder.CreateSetUpdates(new uint[] { 1 }, RTL_FEATURE_ENABLED_STATE.Enabled, new FeatureSetOptions { Variant = 64 }));
    }

    [Fact]
    public void Resettables_SkipImmutable_Deduplicate_AndHonourClamp()
    {
        var configs = new[]
        {
            new FeatureConfiguration(1, RTL_FEATURE_CONFIGURATION_PRIORITY.User, RTL_FEATURE_ENABLED_STATE.Enabled),
            new FeatureConfiguration(1, RTL_FEATURE_CONFIGURATION_PRIORITY.ImageDefault, RTL_FEATURE_ENABLED_STATE.Enabled),
            new FeatureConfiguration(1, RTL_FEATURE_CONFIGURATION_PRIORITY.Service, RTL_FEATURE_ENABLED_STATE.Enabled),
            new FeatureConfiguration(2, RTL_FEATURE_CONFIGURATION_PRIORITY.User, RTL_FEATURE_ENABLED_STATE.Enabled),
        };
        var list = new List<RTL_FEATURE_CONFIGURATION_UPDATE>();
        var seen = new HashSet<string>();
        FeatureUpdateBuilder.AddResettables(configs, false, new uint[] { 1 }, null, list, seen);
        FeatureUpdateBuilder.AddResettables(configs, false, new uint[] { 1 }, null, list, seen); // second store, same entries
        Assert.Equal(2, list.Count);
        Assert.All(list, u => Assert.Equal(RTL_FEATURE_CONFIGURATION_OPERATION.ResetState, u.Operation));

        var clamped = new List<RTL_FEATURE_CONFIGURATION_UPDATE>();
        FeatureUpdateBuilder.AddResettables(configs, true, null, RTL_FEATURE_CONFIGURATION_PRIORITY.User, clamped, new HashSet<string>());
        Assert.Equal(new uint[] { 1, 2 }, clamped.Select(u => u.FeatureId));
    }

    [Fact]
    public void PriorityFix_MovesServiceOverridesToUser()
    {
        var service = new RTL_FEATURE_CONFIGURATION { FeatureId = 5 };
        service.Priority = RTL_FEATURE_CONFIGURATION_PRIORITY.Service;
        service.EnabledState = RTL_FEATURE_ENABLED_STATE.Enabled;
        var experiment = new RTL_FEATURE_CONFIGURATION { FeatureId = 6 };
        experiment.Priority = RTL_FEATURE_CONFIGURATION_PRIORITY.Service;
        experiment.IsWexpConfiguration = true;

        var updates = FeatureUpdateBuilder.MakePriorityFixUpdates(new[] { service, experiment })!;
        Assert.Equal(2, updates.Length);
        Assert.Equal(RTL_FEATURE_CONFIGURATION_OPERATION.ResetState, updates[0].Operation);
        Assert.Equal(RTL_FEATURE_CONFIGURATION_PRIORITY.User, updates[1].Priority);
        Assert.Equal(RTL_FEATURE_ENABLED_STATE.Enabled, updates[1].EnabledState);
        Assert.Null(FeatureUpdateBuilder.MakePriorityFixUpdates(new[] { experiment }));
    }

    [Fact]
    public void ConvertConfigs_PreservesProperties()
    {
        var cfg = new FeatureConfiguration(9, RTL_FEATURE_CONFIGURATION_PRIORITY.User, RTL_FEATURE_ENABLED_STATE.Disabled, true, 4, RTL_FEATURE_VARIANT_PAYLOAD_KIND.External, 77).ToNative();
        var update = FeatureUpdateBuilder.ConvertConfigsToUpdates(new[] { cfg }).Single();
        Assert.Equal(RTL_FEATURE_ENABLED_STATE_OPTIONS.WexpConfig, update.EnabledStateOptions);
        Assert.Equal(4u, update.Variant);
        Assert.Equal(RTL_FEATURE_VARIANT_PAYLOAD_KIND.External, update.VariantPayloadKind);
        Assert.Equal(77u, update.VariantPayload);
    }
}

public class NativeStructTests
{
    [Fact]
    public void CompactState_RoundTrips()
    {
        var original = new FeatureConfiguration(123, RTL_FEATURE_CONFIGURATION_PRIORITY.Test, RTL_FEATURE_ENABLED_STATE.Enabled, true, 63, RTL_FEATURE_VARIANT_PAYLOAD_KIND.Resident, 99, true);
        Assert.Equal(original, FeatureConfiguration.FromNative(original.ToNative()));
    }

    [Theory]
    [InlineData(1u)]
    [InlineData(12345678u)]
    [InlineData(uint.MaxValue)]
    public void Obfuscation_RoundTrips(uint id) =>
        Assert.Equal(id, ObfuscationHelpers.DeobfuscateFeatureId(ObfuscationHelpers.ObfuscateFeatureId(id)));

    [Fact]
    public void UserPolicyRestoreUpdate_OnlyWritesState()
    {
        var update = new FeatureConfiguration(1, RTL_FEATURE_CONFIGURATION_PRIORITY.UserPolicy, RTL_FEATURE_ENABLED_STATE.Enabled).ToRestoreUpdate();
        Assert.Equal(RTL_FEATURE_CONFIGURATION_OPERATION.FeatureState, update.Operation);
        Assert.True(update.UserPolicyPriorityCompatible);
    }

    [Fact]
    public void MutablePriorities_ExcludeImmutable()
    {
        Assert.DoesNotContain(FeaturePriorities.Mutable, FeaturePriorities.IsImmutable);
        Assert.Contains(RTL_FEATURE_CONFIGURATION_PRIORITY.User, FeaturePriorities.Mutable);
    }
}

public class FeatureStoreWriterTests
{
    [Fact]
    public void ImmutablePriority_ArgumentErrorIsReported()
    {
        var api = new FakeFeatureConfigurationApi();
        var writer = new FeatureStoreWriter(api);
        var update = new RTL_FEATURE_CONFIGURATION_UPDATE { FeatureId = 1, Priority = RTL_FEATURE_CONFIGURATION_PRIORITY.EKB, Operation = RTL_FEATURE_CONFIGURATION_OPERATION.ResetState };
        var result = writer.Apply(new[] { update }, new[] { update });
        Assert.False(result.Success);
        Assert.Equal(FeatureStore.Runtime, result.FailedStore);
        Assert.Contains("immutable", result.ArgumentError);
    }

    [Fact]
    public void RuntimeOnly_DoesNotTouchBootStatus()
    {
        var api = new FakeFeatureConfigurationApi();
        var update = FeatureUpdateBuilder.CreateSetUpdates(new uint[] { 1 }, RTL_FEATURE_ENABLED_STATE.Enabled, null);
        var result = new FeatureStoreWriter(api).Apply(update, null);
        Assert.True(result.Success);
        Assert.False(result.BootStoreWritten);
        Assert.Empty(api.BootStatusWrites);
    }

    [Fact]
    public void BootStatus_InitFailure_StopsWithIssue()
    {
        var api = new FakeFeatureConfigurationApi { BootStatusQueryResult = NtStatus.ObjectNameNotFound, InitializeBootStatusResult = NtStatus.AccessDenied };
        var issues = new FeatureStoreWriter(api).UpdateBootStatus(BSD_FEATURE_CONFIGURATION_STATE.BootPending);
        Assert.Equal(BootStatusIssueKind.InitializeFailed, Assert.Single(issues).Kind);
        Assert.Empty(api.BootStatusWrites);
    }

    [Fact]
    public void BootStatus_AlreadyPending_IsNotRewritten()
    {
        var api = new FakeFeatureConfigurationApi { BootStatus = BSD_FEATURE_CONFIGURATION_STATE.BootPending };
        Assert.Empty(new FeatureStoreWriter(api).UpdateBootStatus(BSD_FEATURE_CONFIGURATION_STATE.BootPending));
        Assert.Empty(api.BootStatusWrites);
    }
}
