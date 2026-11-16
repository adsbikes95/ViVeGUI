// SPDX-License-Identifier: GPL-3.0-or-later
using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ViVe.Core;
using ViVe.Studio.Models;
using ViVe.Studio.Services;

namespace ViVe.Studio.ViewModels;

public sealed class HistoryItemViewModel
{
    public HistoryItemViewModel(ChangeHistoryEntry entry, RollbackEligibility eligibility)
    {
        Entry = entry;
        Eligibility = eligibility;
    }

    public ChangeHistoryEntry Entry { get; }
    public RollbackEligibility Eligibility { get; }
    public bool CanRollback => Eligibility.CanRollback;
    public string RollbackReason => Eligibility.Reason;

    public string TimeText => Entry.TimestampUtc.ToLocalTime().ToString("g", CultureInfo.CurrentCulture);
    public string FeatureText => Entry.FeatureName is null ? Entry.FeatureId.ToString(CultureInfo.InvariantCulture) : $"{Entry.FeatureId} · {Entry.FeatureName}";
    public string OperationText
    {
        get
        {
            var op = Entry.RollbackOf != null ? "Rollback" : Entry.Operation.ToString();
            var priority = Entry.ResetAllPriorities ? "all priorities" : Entry.Priority?.ToString();
            return priority is null ? $"{op} ({Format.Stores(Entry.Stores)})" : $"{op} · {priority} ({Format.Stores(Entry.Stores)})";
        }
    }
    public string StatusText => Format.Status(Entry.Status) + (Entry.RolledBackBy != null ? " · rolled back" : string.Empty);
    public StatusSeverity Severity => Format.Severity(Entry.Status);
    public string BuildText => Entry.OsDisplayVersion is null ? $"Build {Entry.OsBuild}" : $"{Entry.OsDisplayVersion} (build {Entry.OsBuild})";
    public string BeforeText => Entry.Before == null ? "Previous state: not recorded" :
        "Previous state: runtime " + (Entry.Before.RuntimeCaptured ? Format.Entries(Entry.Before.RuntimeEntries) : "not captured") +
        " | boot " + (Entry.Before.BootCaptured ? Format.Entries(Entry.Before.BootEntries) : "not captured");
    public string Message => Entry.Message + (Entry.Warnings.Count > 0 ? " " + string.Join(" ", Entry.Warnings) : string.Empty);
}

public sealed partial class HistoryViewModel : ObservableObject
{
    private readonly IChangeHistoryStore _history;
    private readonly FeatureChangeService _changes;
    private readonly IDialogService _dialogs;
    private readonly IElevationService _elevation;

    public HistoryViewModel(IChangeHistoryStore history, FeatureChangeService changes, IDialogService dialogs, IElevationService elevation)
    {
        _history = history;
        _changes = changes;
        _dialogs = dialogs;
        _elevation = elevation;
    }

    public ObservableCollection<HistoryItemViewModel> Entries { get; } = new();

    [ObservableProperty] private HistoryItemViewModel? _selectedEntry;
    [ObservableProperty] private string? _statusMessage;
    [ObservableProperty] private StatusSeverity _statusSeverity;
    [ObservableProperty] private bool _isBusy;

    [RelayCommand]
    public async Task RefreshAsync()
    {
        var entries = await _history.GetEntriesAsync();
        Entries.Clear();
        foreach (var entry in entries.OrderByDescending(e => e.TimestampUtc))
            Entries.Add(new HistoryItemViewModel(entry, _changes.EvaluateRollback(entry, entries)));
        if (_history.LoadWarning != null)
            SetStatus(_history.LoadWarning, StatusSeverity.Warning);
    }

    [RelayCommand]
    private async Task ExportAsync()
    {
        var path = _dialogs.PickSaveFile("Export change history", "JSON files (*.json)|*.json", $"vivetool-studio-history-{DateTime.Now:yyyyMMdd-HHmmss}.json");
        if (path == null) return;
        try
        {
            await _history.ExportAsync(path);
            SetStatus($"Exported {Entries.Count} entries to {path}.", StatusSeverity.Success);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            SetStatus("Export failed: " + ex.Message, StatusSeverity.Error);
        }
    }

    [RelayCommand]
    private async Task RollbackAsync(HistoryItemViewModel? item)
    {
        item ??= SelectedEntry;
        if (item == null || IsBusy) return;
        if (!item.CanRollback)
        {
            SetStatus(item.RollbackReason, StatusSeverity.Warning);
            return;
        }
        if (!_elevation.IsElevated)
        {
            SetStatus("Rollback changes feature configuration and requires administrator rights. Restart ViVeTool Studio as administrator.", StatusSeverity.Warning);
            return;
        }
        var request = _changes.CreateRollbackRequest(item.Entry);
        var highRisk = item.Entry.PrioritiesTouched.Any(FeaturePriorities.IsHigherRisk);
        var message = $"{request.Describe()}.\n\n{item.BeforeText}\n\nThis restores only this feature's override entries at the affected priorities. It is not a system restore.\n\nContinue?";
        if (!_dialogs.Confirm("Confirm rollback", message, highRisk))
        {
            SetStatus("Rollback cancelled. Nothing was changed.", StatusSeverity.Info);
            return;
        }
        IsBusy = true;
        try
        {
            var result = await _changes.RollbackAsync(item.Entry);
            if (result == null)
                SetStatus("Rollback is no longer possible for this entry.", StatusSeverity.Warning);
            else
                SetStatus($"Rollback: {Format.Status(result.Status)}. {result.Message}", Format.Severity(result.Status));
            await RefreshAsync();
        }
        finally { IsBusy = false; }
    }

    private void SetStatus(string message, StatusSeverity severity)
    {
        StatusMessage = message;
        StatusSeverity = severity;
    }
}
