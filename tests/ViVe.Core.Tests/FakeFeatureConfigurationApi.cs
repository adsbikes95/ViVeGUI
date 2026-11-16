// SPDX-License-Identifier: GPL-3.0-or-later
using Albacore.ViVe.NativeEnums;
using Albacore.ViVe.NativeStructs;
using ViVe.Core;

namespace ViVe.Core.Tests;

/// <summary>
/// In-memory simulation of the runtime store and the persisted boot store. No Windows APIs are called,
/// so tests never modify real feature configuration.
/// </summary>
public sealed class FakeFeatureConfigurationApi : IFeatureConfigurationApi
{
    public int OsBuild { get; set; } = 26100;
    public bool IsPlatformSupported { get; set; } = true;

    public Dictionary<(uint Id, RTL_FEATURE_CONFIGURATION_PRIORITY Priority), FeatureConfiguration> Runtime { get; } = new();
    public Dictionary<(uint Id, RTL_FEATURE_CONFIGURATION_PRIORITY Priority), FeatureConfiguration> Boot { get; } = new();

    public int RuntimeSetStatus { get; set; }
    public int BootSetStatus { get; set; }
    public bool FailRuntimeQuery { get; set; }
    public bool FailBootRead { get; set; }
    /// <summary>Simulates Windows returning success without actually applying the change.</summary>
    public bool IgnoreWrites { get; set; }
    public int BootStatusQueryResult { get; set; }
    public BSD_FEATURE_CONFIGURATION_STATE BootStatus { get; set; } = BSD_FEATURE_CONFIGURATION_STATE.Committed;
    public int SetBootStatusResult { get; set; }
    public int InitializeBootStatusResult { get; set; }
    public ulong ChangeStamp { get; set; } = 42;

    public List<(FeatureStore Store, RTL_FEATURE_CONFIGURATION_UPDATE[] Updates)> SetCalls { get; } = new();
    public List<BSD_FEATURE_CONFIGURATION_STATE> BootStatusWrites { get; } = new();

    public void Seed(FeatureStore store, FeatureConfiguration config) =>
        (store == FeatureStore.Runtime ? Runtime : Boot)[(config.FeatureId, config.Priority)] = config;

    public IReadOnlyList<FeatureConfiguration>? QueryAll(FeatureStore store)
    {
        if (store == FeatureStore.Runtime && FailRuntimeQuery) return null;
        return (store == FeatureStore.Runtime ? Runtime : Boot).Values.ToList();
    }

    public FeatureConfiguration? Query(uint featureId, FeatureStore store) =>
        (store == FeatureStore.Runtime ? Runtime : Boot).Values
            .Where(c => c.FeatureId == featureId)
            .OrderByDescending(c => (uint)c.Priority)
            .FirstOrDefault();

    public ulong QueryChangeStamp() => ChangeStamp;

    public int SetConfigurations(IReadOnlyList<RTL_FEATURE_CONFIGURATION_UPDATE> updates, FeatureStore store)
    {
        // Same validation as Albacore.ViVe.FeatureManager.SetFeatureConfigurations.
        foreach (var update in updates)
            if (FeaturePriorities.IsImmutable(update.Priority))
                throw new ArgumentException($"{update.Priority} ({(int)update.Priority}) is an immutable priority and can't be written to.");
            else if (update.Priority == RTL_FEATURE_CONFIGURATION_PRIORITY.UserPolicy && !update.UserPolicyPriorityCompatible)
                throw new ArgumentException("UserPolicy priority overrides do not support persisting properties other than EnabledState.");

        SetCalls.Add((store, updates.ToArray()));
        var status = store == FeatureStore.Runtime ? RuntimeSetStatus : BootSetStatus;
        if (status != 0 || IgnoreWrites) return status;

        var target = store == FeatureStore.Runtime ? Runtime : Boot;
        foreach (var u in updates)
        {
            var key = (u.FeatureId, u.Priority);
            if (u.Operation.HasFlag(RTL_FEATURE_CONFIGURATION_OPERATION.ResetState))
                target.Remove(key);
            else
                target[key] = new FeatureConfiguration(u.FeatureId, u.Priority, u.EnabledState,
                    u.EnabledStateOptions == RTL_FEATURE_ENABLED_STATE_OPTIONS.WexpConfig, u.Variant, u.VariantPayloadKind, u.VariantPayload);
        }
        ChangeStamp++;
        return 0;
    }

    public int GetBootStatus(out BSD_FEATURE_CONFIGURATION_STATE state)
    {
        state = BootStatusQueryResult == 0 ? BootStatus : BSD_FEATURE_CONFIGURATION_STATE.Uninitialized;
        return BootStatusQueryResult;
    }

    public int SetBootStatus(BSD_FEATURE_CONFIGURATION_STATE state)
    {
        BootStatusWrites.Add(state);
        if (SetBootStatusResult == 0) BootStatus = state;
        return SetBootStatusResult;
    }

    public int InitializeBootStatusDataFile() => InitializeBootStatusResult;

    public BootOverrideReadResult ReadBootOverrides(uint featureId) =>
        FailBootRead
            ? BootOverrideReadResult.Failed("registry unavailable")
            : new BootOverrideReadResult(true, Boot.Values.Where(c => c.FeatureId == featureId).ToList());
}
