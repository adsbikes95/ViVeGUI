// SPDX-License-Identifier: GPL-3.0-or-later
using System.Collections.Generic;
using Albacore.ViVe.NativeEnums;
using Albacore.ViVe.NativeStructs;

namespace ViVe.Core
{
    /// <summary>
    /// Thin, mockable wrapper over the native feature configuration APIs exposed by the
    /// upstream ViVe library (<c>Albacore.ViVe.FeatureManager</c>). No policy lives here:
    /// it only translates calls and returns raw NTSTATUS values.
    /// </summary>
    public interface IFeatureConfigurationApi
    {
        /// <summary>The Windows build number (0 when not running on Windows).</summary>
        int OsBuild { get; }

        /// <summary>True when running on Windows build 18963 or newer (the minimum ViVe supports).</summary>
        bool IsPlatformSupported { get; }

        /// <summary>Returns every configuration entry in a store, or null if the query failed.</summary>
        IReadOnlyList<FeatureConfiguration>? QueryAll(FeatureStore store);

        /// <summary>Returns the effective (winning) configuration for a feature, or null if there is none / the query failed.</summary>
        FeatureConfiguration? Query(uint featureId, FeatureStore store);

        ulong QueryChangeStamp();

        /// <summary>Applies updates to a store. Returns NTSTATUS (0 = success). Throws <see cref="System.ArgumentException"/> for immutable priorities.</summary>
        int SetConfigurations(IReadOnlyList<RTL_FEATURE_CONFIGURATION_UPDATE> updates, FeatureStore store);

        int GetBootStatus(out BSD_FEATURE_CONFIGURATION_STATE state);

        int SetBootStatus(BSD_FEATURE_CONFIGURATION_STATE state);

        int InitializeBootStatusDataFile();

        /// <summary>
        /// Reads the persisted (registry) boot-store overrides for one feature at every mutable priority.
        /// This is the source of truth for pending boot changes, unlike a Boot-store query which
        /// reflects what was loaded at the last boot.
        /// </summary>
        BootOverrideReadResult ReadBootOverrides(uint featureId);
    }

    /// <summary>Result of reading persisted boot-store overrides for a feature.</summary>
    public sealed record BootOverrideReadResult(bool Success, IReadOnlyList<FeatureConfiguration> Entries, string? Error = null)
    {
        public static BootOverrideReadResult Failed(string error) => new(false, new FeatureConfiguration[0], error);
    }
}
