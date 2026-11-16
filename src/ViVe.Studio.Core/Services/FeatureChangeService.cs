// SPDX-License-Identifier: GPL-3.0-or-later
using ViVe.Core;
using ViVe.FeatureCatalog;
using ViVe.Studio.Models;

namespace ViVe.Studio.Services;

/// <summary>
/// Runs feature operations through the shared engine and records every attempt in the change history.
/// This is the only path the GUI uses to modify feature state.
/// </summary>
public sealed class FeatureChangeService
{
    private readonly IFeatureManager _manager;
    private readonly IChangeHistoryStore _history;
    private readonly IFeatureCatalog _catalog;
    private readonly ISystemInfoProvider _systemInfo;
    private readonly IAppLogger _logger;

    public FeatureChangeService(IFeatureManager manager, IChangeHistoryStore history, IFeatureCatalog catalog, ISystemInfoProvider systemInfo, IAppLogger logger)
    {
        _manager = manager;
        _history = history;
        _catalog = catalog;
        _systemInfo = systemInfo;
        _logger = logger;
    }

    /// <summary>Executes requests one at a time, recording each. <paramref name="onResult"/> runs on the caller's context.</summary>
    public async Task<IReadOnlyList<FeatureOperationResult>> ExecuteQueueAsync(
        IReadOnlyList<FeatureOperationRequest> requests,
        Action<FeatureOperationResult>? onResult = null,
        CancellationToken cancellationToken = default)
    {
        var results = new List<FeatureOperationResult>();
        foreach (var request in requests)
        {
            if (cancellationToken.IsCancellationRequested) break;
            var result = await ExecuteAsync(request).ConfigureAwait(true);
            results.Add(result);
            onResult?.Invoke(result);
        }
        return results;
    }

    public async Task<FeatureOperationResult> ExecuteAsync(FeatureOperationRequest request, Guid? rollbackOf = null)
    {
        _logger.Log(LogLevel.Information, "Requested: " + request.Describe());
        var result = await _manager.ExecuteAsync(request).ConfigureAwait(true);
        _logger.Log(result.IsSuccess ? LogLevel.Information : LogLevel.Warning, $"Result for {request.FeatureId}: {result.Status} - {result.Message}");
        var entry = ChangeHistoryEntry.FromResult(result, _catalog.TryGet(request.FeatureId)?.Name, SafeDisplayVersion(), rollbackOf);
        try
        {
            await _history.AppendAsync(entry).ConfigureAwait(true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.Log(LogLevel.Error, "Could not write change history: " + ex.Message);
            return result with { Warnings = result.Warnings.Concat(new[] { "The change history could not be saved: " + ex.Message }).ToArray() };
        }
        return result;
    }

    public RollbackEligibility EvaluateRollback(ChangeHistoryEntry entry, IReadOnlyList<ChangeHistoryEntry> history) =>
        RollbackPolicy.Evaluate(entry, history, _manager.OsBuild);

    public FeatureOperationRequest CreateRollbackRequest(ChangeHistoryEntry entry) =>
        new(FeatureOperationKind.Restore, entry.FeatureId)
        {
            Stores = entry.Stores,
            RestoreSnapshot = entry.Before,
            RestorePriorities = entry.PrioritiesTouched,
        };

    /// <summary>Rolls back an entry after re-checking eligibility. Marks the original entry when the restore succeeds.</summary>
    public async Task<FeatureOperationResult?> RollbackAsync(ChangeHistoryEntry entry)
    {
        var history = await _history.GetEntriesAsync().ConfigureAwait(true);
        var current = history.FirstOrDefault(e => e.Id == entry.Id) ?? entry;
        var eligibility = EvaluateRollback(current, history);
        if (!eligibility.CanRollback)
            return null;

        var result = await ExecuteAsync(CreateRollbackRequest(current), current.Id).ConfigureAwait(true);
        if (result.ChangedState)
        {
            var updatedHistory = await _history.GetEntriesAsync().ConfigureAwait(true);
            var restoreEntry = updatedHistory.LastOrDefault(e => e.RollbackOf == current.Id);
            await _history.ReplaceAsync(current with { RolledBackBy = restoreEntry?.Id ?? Guid.Empty }).ConfigureAwait(true);
        }
        return result;
    }

    private string? SafeDisplayVersion()
    {
        try { return _systemInfo.GetSystemInfo().DisplayVersion; }
        catch (Exception) { return null; }
    }
}
