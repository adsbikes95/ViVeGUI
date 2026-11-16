// SPDX-License-Identifier: GPL-3.0-or-later
using System;
using Albacore.ViVe.NativeEnums;

namespace ViVe.Core
{
    /// <summary>A single Windows feature configuration store.</summary>
    public enum FeatureStore
    {
        /// <summary>Live, in-memory configuration. Changes apply immediately (for components that re-read it).</summary>
        Runtime = 1,
        /// <summary>Persistent configuration in the registry, loaded at boot. Changes need a reboot.</summary>
        Boot = 0,
    }

    /// <summary>Set of stores an operation targets. ViVeTool's default is both.</summary>
    [Flags]
    public enum FeatureStores
    {
        None = 0,
        Runtime = 1,
        Boot = 2,
        Both = Runtime | Boot,
    }

    public static class FeatureStoreExtensions
    {
        public static RTL_FEATURE_CONFIGURATION_TYPE ToNative(this FeatureStore store) =>
            store == FeatureStore.Boot ? RTL_FEATURE_CONFIGURATION_TYPE.Boot : RTL_FEATURE_CONFIGURATION_TYPE.Runtime;

        public static bool Includes(this FeatureStores stores, FeatureStore store) =>
            store == FeatureStore.Runtime ? (stores & FeatureStores.Runtime) != 0 : (stores & FeatureStores.Boot) != 0;
    }
}
