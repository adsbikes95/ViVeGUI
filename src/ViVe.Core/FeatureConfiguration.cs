// SPDX-License-Identifier: GPL-3.0-or-later
using Albacore.ViVe.NativeEnums;
using Albacore.ViVe.NativeStructs;

namespace ViVe.Core
{
    /// <summary>
    /// Managed, immutable view of one feature configuration entry (one feature at one priority in one store).
    /// This is feature <em>state</em>; descriptive metadata lives in the feature catalog.
    /// </summary>
    public sealed record FeatureConfiguration(
        uint FeatureId,
        RTL_FEATURE_CONFIGURATION_PRIORITY Priority,
        RTL_FEATURE_ENABLED_STATE EnabledState,
        bool IsExperiment = false,
        uint Variant = 0,
        RTL_FEATURE_VARIANT_PAYLOAD_KIND VariantPayloadKind = RTL_FEATURE_VARIANT_PAYLOAD_KIND.None,
        uint VariantPayload = 0,
        bool HasSubscriptions = false)
    {
        public static FeatureConfiguration FromNative(RTL_FEATURE_CONFIGURATION config) => new(
            config.FeatureId,
            config.Priority,
            config.EnabledState,
            config.IsWexpConfiguration,
            config.Variant,
            config.VariantPayloadKind,
            config.VariantPayload,
            config.HasSubscriptions);

        public RTL_FEATURE_CONFIGURATION ToNative()
        {
            var native = new RTL_FEATURE_CONFIGURATION
            {
                FeatureId = FeatureId,
                VariantPayload = VariantPayload,
            };
            native.Priority = Priority;
            native.EnabledState = EnabledState;
            native.IsWexpConfiguration = IsExperiment;
            native.HasSubscriptions = HasSubscriptions;
            native.Variant = Variant;
            native.VariantPayloadKind = VariantPayloadKind;
            return native;
        }

        /// <summary>Builds the update that re-creates exactly this entry (state + variant).</summary>
        public RTL_FEATURE_CONFIGURATION_UPDATE ToRestoreUpdate() => new()
        {
            FeatureId = FeatureId,
            Priority = Priority,
            EnabledState = EnabledState,
            EnabledStateOptions = IsExperiment ? RTL_FEATURE_ENABLED_STATE_OPTIONS.WexpConfig : RTL_FEATURE_ENABLED_STATE_OPTIONS.None,
            Variant = Variant,
            VariantPayloadKind = VariantPayloadKind,
            VariantPayload = VariantPayload,
            Operation = Priority == RTL_FEATURE_CONFIGURATION_PRIORITY.UserPolicy
                ? RTL_FEATURE_CONFIGURATION_OPERATION.FeatureState
                : RTL_FEATURE_CONFIGURATION_OPERATION.FeatureState | RTL_FEATURE_CONFIGURATION_OPERATION.VariantState,
        };

        public override string ToString() =>
            $"{FeatureId} @ {Priority}: {EnabledState}" +
            (IsExperiment ? " (experiment)" : string.Empty) +
            (Variant != 0 ? $", variant {Variant}" : string.Empty) +
            (VariantPayloadKind != RTL_FEATURE_VARIANT_PAYLOAD_KIND.None || VariantPayload != 0 ? $", payload {VariantPayloadKind}:{VariantPayload}" : string.Empty);
    }
}
