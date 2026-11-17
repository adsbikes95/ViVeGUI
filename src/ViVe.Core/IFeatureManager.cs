// SPDX-License-Identifier: GPL-3.0-or-later
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace ViVe.Core
{
    /// <summary>
    /// High-level feature-management engine shared by ViVeTool Studio and testable through mocks.
    /// All modifications are validated, snapshotted beforehand and verified afterwards.
    /// </summary>
    public interface IFeatureManager
    {
        bool IsPlatformSupported { get; }

        int OsBuild { get; }

        /// <summary>Validates a request and describes what it would do, without changing anything.</summary>
        FeatureOperationPlan Plan(FeatureOperationRequest request);

        Task<FeatureQueryResult> QueryAsync(uint featureId, CancellationToken cancellationToken = default);

        /// <summary>Captures the current override state of a feature (used before modifications and for rollback).</summary>
        Task<FeatureSnapshot> CaptureSnapshotAsync(uint featureId, CancellationToken cancellationToken = default);

        Task<FeatureOperationResult> ExecuteAsync(FeatureOperationRequest request, CancellationToken cancellationToken = default);

        /// <summary>
        /// Executes requests one at a time, in order (an operation queue). Cancellation stops before the
        /// next item; an item that has started always runs to completion.
        /// </summary>
        Task<IReadOnlyList<FeatureOperationResult>> ExecuteBatchAsync(
            IEnumerable<FeatureOperationRequest> requests,
            IProgress<FeatureOperationResult>? progress = null,
            CancellationToken cancellationToken = default);

        /// <summary>
        /// Distinct feature IDs that have any configuration in the runtime store (what "ViVeTool /query" lists),
        /// or null if the store could not be read.
        /// </summary>
        Task<IReadOnlyList<uint>?> GetConfiguredFeatureIdsAsync(CancellationToken cancellationToken = default);

        Task<FeatureSystemStatus> GetSystemStatusAsync(CancellationToken cancellationToken = default);
    }
}
