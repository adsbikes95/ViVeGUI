// SPDX-License-Identifier: GPL-3.0-or-later
using System.Collections.Generic;
using System.Linq;
using Albacore.ViVe.NativeEnums;
using Albacore.ViVe.NativeStructs;

namespace ViVe.Core
{
    /// <summary>Optional properties for enable/disable operations (mirrors ViVeTool's /experiment, /variant*, /priority).</summary>
    public sealed class FeatureSetOptions
    {
        public RTL_FEATURE_ENABLED_STATE_OPTIONS EnabledStateOptions { get; set; }
        public uint Variant { get; set; }
        public RTL_FEATURE_VARIANT_PAYLOAD_KIND VariantPayloadKind { get; set; }
        public uint VariantPayload { get; set; }
        public RTL_FEATURE_CONFIGURATION_PRIORITY? Priority { get; set; }
    }

    /// <summary>
    /// Builds RTL_FEATURE_CONFIGURATION_UPDATE batches. This logic was extracted verbatim from
    /// ViVeTool's Program.cs so that the CLI and the GUI produce identical updates.
    /// </summary>
    public static class FeatureUpdateBuilder
    {
        /// <summary>/enable and /disable. Throws <see cref="Albacore.ViVe.Exceptions.FeaturePropertyOverflowException"/> for out-of-range values.</summary>
        public static RTL_FEATURE_CONFIGURATION_UPDATE[] CreateSetUpdates(IReadOnlyList<uint> featureIds, RTL_FEATURE_ENABLED_STATE state, FeatureSetOptions? options)
        {
            options ??= new FeatureSetOptions();
            var updates = new RTL_FEATURE_CONFIGURATION_UPDATE[featureIds.Count];
            for (int i = 0; i < updates.Length; i++)
            {
                updates[i] = new RTL_FEATURE_CONFIGURATION_UPDATE()
                {
                    FeatureId = featureIds[i],
                    EnabledState = state,
                    EnabledStateOptions = options.EnabledStateOptions,
                    Priority = options.Priority ?? FeaturePriorities.Default,
                    Variant = options.Variant,
                    VariantPayloadKind = options.VariantPayloadKind,
                    VariantPayload = options.VariantPayload,
                    Operation = RTL_FEATURE_CONFIGURATION_OPERATION.FeatureState | RTL_FEATURE_CONFIGURATION_OPERATION.VariantState
                };
            }
            return updates;
        }

        /// <summary>/reset with an explicit /priority.</summary>
        public static RTL_FEATURE_CONFIGURATION_UPDATE[] CreateResetUpdates(IReadOnlyList<uint> featureIds, RTL_FEATURE_CONFIGURATION_PRIORITY priority)
        {
            var updates = new RTL_FEATURE_CONFIGURATION_UPDATE[featureIds.Count];
            for (int i = 0; i < updates.Length; i++)
            {
                updates[i] = new RTL_FEATURE_CONFIGURATION_UPDATE()
                {
                    FeatureId = featureIds[i],
                    Priority = priority,
                    Operation = RTL_FEATURE_CONFIGURATION_OPERATION.ResetState
                };
            }
            return updates;
        }

        public static RTL_FEATURE_CONFIGURATION_UPDATE CreateReset(uint featureId, RTL_FEATURE_CONFIGURATION_PRIORITY priority) => new()
        {
            FeatureId = featureId,
            Priority = priority,
            Operation = RTL_FEATURE_CONFIGURATION_OPERATION.ResetState
        };

        /// <summary>
        /// /reset without /priority and /fullreset: collects ResetState updates for every mutable
        /// configuration found in <paramref name="configurations"/> (deduplicated by feature and priority).
        /// </summary>
        public static void AddResettables(
            IEnumerable<FeatureConfiguration> configurations,
            bool fullReset,
            ICollection<uint>? featureIds,
            RTL_FEATURE_CONFIGURATION_PRIORITY? priorityClamp,
            List<RTL_FEATURE_CONFIGURATION_UPDATE> targetList,
            HashSet<string>? alreadyFoundSet)
        {
            foreach (var cfg in configurations)
            {
                if (FeaturePriorities.IsImmutable(cfg.Priority) ||
                    (priorityClamp.HasValue && cfg.Priority != priorityClamp.Value))
                    continue;
                if (fullReset || (featureIds != null && featureIds.Contains(cfg.FeatureId)))
                {
                    if (alreadyFoundSet != null && !alreadyFoundSet.Add($"{cfg.FeatureId}-{(uint)cfg.Priority}"))
                        continue;
                    targetList.Add(CreateReset(cfg.FeatureId, cfg.Priority));
                }
            }
        }

        /// <summary>/import: turns exported configurations back into updates.</summary>
        public static RTL_FEATURE_CONFIGURATION_UPDATE[] ConvertConfigsToUpdates(IReadOnlyList<RTL_FEATURE_CONFIGURATION> configurations)
        {
            var updates = new RTL_FEATURE_CONFIGURATION_UPDATE[configurations.Count];
            for (int i = 0; i < updates.Length; i++)
            {
                var config = configurations[i];
                updates[i] = new RTL_FEATURE_CONFIGURATION_UPDATE()
                {
                    FeatureId = config.FeatureId,
                    Priority = config.Priority,
                    EnabledState = config.EnabledState,
                    EnabledStateOptions = config.IsWexpConfiguration ? RTL_FEATURE_ENABLED_STATE_OPTIONS.WexpConfig : RTL_FEATURE_ENABLED_STATE_OPTIONS.None,
                    Variant = config.Variant,
                    VariantPayloadKind = config.VariantPayloadKind,
                    VariantPayload = config.VariantPayload,
                    Operation = RTL_FEATURE_CONFIGURATION_OPERATION.FeatureState | RTL_FEATURE_CONFIGURATION_OPERATION.VariantState
                };
            }
            return updates;
        }

        /// <summary>/fixpriority: moves non-experiment Service-priority overrides to the User priority. Returns null when nothing needs fixing.</summary>
        public static RTL_FEATURE_CONFIGURATION_UPDATE[]? MakePriorityFixUpdates(IEnumerable<RTL_FEATURE_CONFIGURATION> configurations)
        {
            var configsToFix = configurations.Where(x => x.Priority == RTL_FEATURE_CONFIGURATION_PRIORITY.Service && !x.IsWexpConfiguration).ToList();
            if (configsToFix.Count == 0)
                return null;
            var priorityFixUpdates = new RTL_FEATURE_CONFIGURATION_UPDATE[configsToFix.Count * 2];
            var updatesCreated = 0;
            foreach (var cfg in configsToFix)
            {
                priorityFixUpdates[updatesCreated] = CreateReset(cfg.FeatureId, cfg.Priority);
                priorityFixUpdates[updatesCreated + 1] = new RTL_FEATURE_CONFIGURATION_UPDATE()
                {
                    FeatureId = cfg.FeatureId,
                    Priority = RTL_FEATURE_CONFIGURATION_PRIORITY.User,
                    EnabledState = cfg.EnabledState,
                    Variant = cfg.Variant,
                    VariantPayloadKind = cfg.VariantPayloadKind,
                    VariantPayload = cfg.VariantPayload,
                    Operation = RTL_FEATURE_CONFIGURATION_OPERATION.FeatureState | RTL_FEATURE_CONFIGURATION_OPERATION.VariantState
                };
                updatesCreated += 2;
            }
            return priorityFixUpdates;
        }
    }
}
