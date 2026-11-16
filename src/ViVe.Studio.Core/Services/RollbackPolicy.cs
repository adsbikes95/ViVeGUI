// SPDX-License-Identifier: GPL-3.0-or-later
using ViVe.Core;
using ViVe.Studio.Models;

namespace ViVe.Studio.Services;

public sealed record RollbackEligibility(bool CanRollback, string Reason);

/// <summary>
/// Decides whether a history entry can be rolled back <em>reliably</em>. Rollback restores only the
/// override entries for that feature at the priorities the change touched; it is not a system restore.
/// </summary>
public static class RollbackPolicy
{
    public static RollbackEligibility Evaluate(ChangeHistoryEntry entry, IReadOnlyList<ChangeHistoryEntry> history, int currentOsBuild)
    {
        if (entry.Operation == FeatureOperationKind.Restore)
            return new(false, "Rollback entries cannot themselves be rolled back. Make a new change instead.");
        if (!entry.ChangedState)
            return new(false, "This operation did not change the feature state.");
        if (entry.RolledBackBy != null)
            return new(false, "This change has already been rolled back.");
        if (entry.Before == null)
            return new(false, "The previous state was not recorded.");
        if (!entry.Before.IsCompleteFor(entry.Stores))
            return new(false, "The previous state could not be fully captured, so it cannot be restored reliably.");
        if (entry.PrioritiesTouched.Count == 0)
            return new(false, "No priorities were changed.");
        if (entry.OsBuild != 0 && currentOsBuild != 0 && entry.OsBuild != currentOsBuild)
            return new(false, $"The change was made on build {entry.OsBuild}; Windows is now build {currentOsBuild}. Updates may have changed feature configuration.");
        var later = history.Where(e => e.FeatureId == entry.FeatureId && e.Id != entry.Id && e.ChangedState && e.TimestampUtc > entry.TimestampUtc && e.RollbackOf != entry.Id);
        if (later.Any())
            return new(false, "A later change to this feature exists. Roll back the most recent change first.");
        return new(true, "The previous state was captured and can be restored.");
    }
}
