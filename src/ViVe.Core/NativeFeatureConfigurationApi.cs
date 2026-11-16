// SPDX-License-Identifier: GPL-3.0-or-later
using System;
using System.Collections.Generic;
using System.Linq;
using Albacore.ViVe;
using Albacore.ViVe.NativeEnums;
using Albacore.ViVe.NativeStructs;
using Microsoft.Win32;

namespace ViVe.Core
{
    /// <summary>
    /// Production implementation of <see cref="IFeatureConfigurationApi"/> that calls the upstream
    /// ViVe library (ntdll.dll Rtl*FeatureConfiguration exports and the FeatureManagement registry keys).
    /// Windows only.
    /// </summary>
    public sealed class NativeFeatureConfigurationApi : IFeatureConfigurationApi
    {
        public const int MinimumSupportedBuild = 18963;

        private const string OverridesRoot = @"SYSTEM\CurrentControlSet\Control\FeatureManagement\Overrides";
        private const string PolicyOverridesKey = @"SYSTEM\CurrentControlSet\Policies\Microsoft\FeatureManagement\Overrides";

        public int OsBuild => NtStatus.IsWindows() ? Environment.OSVersion.Version.Build : 0;

        public bool IsPlatformSupported => NtStatus.IsWindows() && OsBuild >= MinimumSupportedBuild;

        public IReadOnlyList<FeatureConfiguration>? QueryAll(FeatureStore store)
        {
            var configs = FeatureManager.QueryAllFeatureConfigurations(store.ToNative());
            return configs?.Select(FeatureConfiguration.FromNative).ToArray();
        }

        public FeatureConfiguration? Query(uint featureId, FeatureStore store)
        {
            var config = FeatureManager.QueryFeatureConfiguration(featureId, store.ToNative());
            return config.HasValue ? FeatureConfiguration.FromNative(config.Value) : null;
        }

        public ulong QueryChangeStamp() => FeatureManager.QueryFeatureConfigurationChangeStamp();

        public int SetConfigurations(IReadOnlyList<RTL_FEATURE_CONFIGURATION_UPDATE> updates, FeatureStore store) =>
            FeatureManager.SetFeatureConfigurations(updates.ToArray(), store.ToNative());

        public int GetBootStatus(out BSD_FEATURE_CONFIGURATION_STATE state) => FeatureManager.GetBootFeatureConfigurationState(out state);

        public int SetBootStatus(BSD_FEATURE_CONFIGURATION_STATE state) => FeatureManager.SetBootFeatureConfigurationState(state);

        public int InitializeBootStatusDataFile() => FeatureManager.InitializeBootStatusDataFile();

        public BootOverrideReadResult ReadBootOverrides(uint featureId)
        {
            if (!NtStatus.IsWindows())
                return BootOverrideReadResult.Failed("The boot store is only available on Windows.");
            try
            {
                var obfuscatedId = ObfuscationHelpers.ObfuscateFeatureId(featureId).ToString();
                var entries = new List<FeatureConfiguration>();
                foreach (var priority in FeaturePriorities.Mutable)
                {
                    if (priority == RTL_FEATURE_CONFIGURATION_PRIORITY.UserPolicy)
                    {
                        using var policyKey = Registry.LocalMachine.OpenSubKey(PolicyOverridesKey);
                        var value = policyKey?.GetValue(obfuscatedId);
                        if (value is int policyState)
                            entries.Add(new FeatureConfiguration(featureId, priority, (RTL_FEATURE_ENABLED_STATE)policyState));
                        continue;
                    }

                    using var key = Registry.LocalMachine.OpenSubKey($@"{OverridesRoot}\{(int)priority}\{obfuscatedId}");
                    if (key == null)
                        continue;
                    entries.Add(new FeatureConfiguration(
                        featureId,
                        priority,
                        (RTL_FEATURE_ENABLED_STATE)ReadInt(key, "EnabledState"),
                        ReadInt(key, "EnabledStateOptions") == (int)RTL_FEATURE_ENABLED_STATE_OPTIONS.WexpConfig,
                        (uint)ReadInt(key, "Variant"),
                        (RTL_FEATURE_VARIANT_PAYLOAD_KIND)ReadInt(key, "VariantPayloadKind"),
                        unchecked((uint)ReadInt(key, "VariantPayload"))));
                }
                return new BootOverrideReadResult(true, entries);
            }
            catch (Exception ex)
            {
                return BootOverrideReadResult.Failed(ex.Message);
            }
        }

        private static int ReadInt(RegistryKey key, string name) => key.GetValue(name) is int value ? value : 0;
    }
}
