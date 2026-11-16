// SPDX-License-Identifier: GPL-3.0-or-later
using System.Collections.Generic;
using System.Linq;
using Albacore.ViVe;
using Albacore.ViVe.NativeEnums;

namespace ViVe.Core
{
    /// <summary>Helpers describing which priorities can be written and how risky they are.</summary>
    public static class FeaturePriorities
    {
        /// <summary>The priority ViVeTool uses when none is specified.</summary>
        public const RTL_FEATURE_CONFIGURATION_PRIORITY Default = RTL_FEATURE_CONFIGURATION_PRIORITY.User;

        /// <summary>Priorities that the OS/servicing owns and that cannot be written (from upstream ViVe).</summary>
        public static IReadOnlyList<RTL_FEATURE_CONFIGURATION_PRIORITY> Immutable => FeatureManager.ImmutablePriorities;

        public static bool IsImmutable(RTL_FEATURE_CONFIGURATION_PRIORITY priority) => FeatureManager.ImmutablePriorities.Contains(priority);

        /// <summary>All named priorities that can be written to.</summary>
        public static IReadOnlyList<RTL_FEATURE_CONFIGURATION_PRIORITY> Mutable { get; } =
            new[]
            {
                RTL_FEATURE_CONFIGURATION_PRIORITY.Safeguard,
                RTL_FEATURE_CONFIGURATION_PRIORITY.Service,
                RTL_FEATURE_CONFIGURATION_PRIORITY.Dynamic,
                RTL_FEATURE_CONFIGURATION_PRIORITY.User,
                RTL_FEATURE_CONFIGURATION_PRIORITY.UserPolicy,
                RTL_FEATURE_CONFIGURATION_PRIORITY.Test,
            }.Where(p => !IsImmutable(p)).ToArray();

        /// <summary>
        /// Writing anything other than the User priority overrides configuration that Windows
        /// servicing, policy or safeguards manage, so the GUI treats it as a higher-risk change.
        /// </summary>
        public static bool IsHigherRisk(RTL_FEATURE_CONFIGURATION_PRIORITY priority) => priority != Default;
    }
}
