// SPDX-License-Identifier: GPL-3.0-or-later
using System.Collections.ObjectModel;
using System.Text;
using Albacore.ViVe.NativeEnums;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ViVe.Core;
using ViVe.FeatureCatalog;
using ViVe.Studio.Services;

namespace ViVe.Studio.ViewModels;

/// <summary>
/// Feature Explorer (MVP): manual feature-ID input, name/ID search over the catalog,
/// and query / enable / disable / reset through the real engine.
/// </summary>
public sealed partial class FeaturesViewModel : ObservableObject
{
    private readonly IFeatureManager _manager;
    private readonly FeatureChangeService _changes;
    private readonly IFeatureCatalog _catalog;
    private readonly IDialogService _dialogs;
    private readonly IElevationService _elevation;

    public FeaturesViewModel(IFeatureManager manager, FeatureChangeService changes, IFeatureCatalog catalog,
        IDialogService dialogs, IElevationService elevation, ISettingsService settings)
    {
        _manager = manager;
        _changes = changes;
        _catalog = catalog;
        _dialogs = dialogs;
        _elevation = elevation;
        _selectedPriority = PriorityOption.Writable.First(p => p.Priority == FeaturePriorities.Default);
        _selectedStores = StoreOption.For(settings.Current.DefaultStores);
        settings.Changed += (_, _) => SelectedStores = StoreOption.For(settings.Current.DefaultStores);
    }

    public IReadOnlyList<PriorityOption> Priorities => PriorityOption.Writable;
    public IReadOnlyList<StoreOption> StoreOptions => StoreOption.All;
    public IReadOnlyList<RTL_FEATURE_VARIANT_PAYLOAD_KIND> PayloadKinds { get; } =
        new[] { RTL_FEATURE_VARIANT_PAYLOAD_KIND.None, RTL_FEATURE_VARIANT_PAYLOAD_KIND.Resident, RTL_FEATURE_VARIANT_PAYLOAD_KIND.External };

    public ObservableCollection<FeatureCatalogEntry> SearchResults { get; } = new();
    public ObservableCollection<FeatureResultItem> Results { get; } = new();

    public bool IsElevated => _elevation.IsElevated;
    public bool IsPlatformSupported => _manager.IsPlatformSupported;
    public int CatalogCount => _catalog.Count;

    [ObservableProperty] private string _featureInput = string.Empty;
    [ObservableProperty] private string _searchText = string.Empty;
    [ObservableProperty] private string _searchSummary = string.Empty;
    [ObservableProperty] private FeatureCatalogEntry? _selectedSearchResult;
    [ObservableProperty] private PriorityOption _selectedPriority;
    [ObservableProperty] private StoreOption _selectedStores;
    [ObservableProperty] private string _variantText = "0";
    [ObservableProperty] private RTL_FEATURE_VARIANT_PAYLOAD_KIND _selectedPayloadKind = RTL_FEATURE_VARIANT_PAYLOAD_KIND.None;
    [ObservableProperty] private string _payloadText = "0";
    [ObservableProperty] private bool _experiment;
    [ObservableProperty] private bool _resetAllPriorities;
    [ObservableProperty] private string? _statusMessage;
    [ObservableProperty] private StatusSeverity _statusSeverity;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(QueryCommand), nameof(EnableCommand), nameof(DisableCommand), nameof(ResetCommand))]
    private bool _isBusy;

    private bool CanRun() => !IsBusy;

    partial void OnSearchTextChanged(string value)
    {
        SearchResults.Clear();
        if (string.IsNullOrWhiteSpace(value) || value.Trim().Length < 2)
        {
            SearchSummary = _catalog.Count == 0 ? "No feature catalog loaded." : $"Type at least 2 characters to search {_catalog.Count:N0} known feature names and IDs.";
            return;
        }
        const int max = 100;
        var results = _catalog.Search(value, max);
        foreach (var entry in results) SearchResults.Add(entry);
        SearchSummary = results.Count == 0 ? "No matches. Unknown IDs can still be entered manually." :
            results.Count >= max ? $"Showing the first {max} matches." : $"{results.Count} match(es).";
    }

    [RelayCommand]
    private void AddToInput(FeatureCatalogEntry? entry)
    {
        entry ??= SelectedSearchResult;
        if (entry == null) return;
        var existing = FeatureIdParser.ParseList(FeatureInput).Ids;
        if (existing.Contains(entry.Id)) return;
        FeatureInput = string.IsNullOrWhiteSpace(FeatureInput) ? entry.Id.ToString(System.Globalization.CultureInfo.InvariantCulture) : $"{FeatureInput.TrimEnd().TrimEnd(',')}, {entry.Id}";
    }

    [RelayCommand]
    private void ClearResults() => Results.Clear();

    [RelayCommand(CanExecute = nameof(CanRun))]
    private async Task QueryAsync()
    {
        var parsed = ParseIds();
        if (parsed == null) return;
        IsBusy = true;
        try
        {
            SetStatus($"Querying {parsed.Count} feature(s)...", StatusSeverity.Info);
            var failures = 0;
            foreach (var id in parsed)
            {
                var result = await _manager.QueryAsync(id);
                if (!result.Success) failures++;
                Results.Insert(0, FeatureResultItem.FromQuery(result, _catalog.TryGet(id), _manager.OsBuild));
            }
            SetStatus(failures == 0 ? $"Queried {parsed.Count} feature(s)." : $"{failures} of {parsed.Count} queries failed.",
                failures == 0 ? StatusSeverity.Success : StatusSeverity.Error);
        }
        finally { IsBusy = false; }
    }

    [RelayCommand(CanExecute = nameof(CanRun))]
    private Task EnableAsync() => ModifyAsync(FeatureOperationKind.Enable);

    [RelayCommand(CanExecute = nameof(CanRun))]
    private Task DisableAsync() => ModifyAsync(FeatureOperationKind.Disable);

    [RelayCommand(CanExecute = nameof(CanRun))]
    private Task ResetAsync() => ModifyAsync(FeatureOperationKind.Reset);

    private async Task ModifyAsync(FeatureOperationKind kind)
    {
        var ids = ParseIds();
        if (ids == null) return;
        if (!TryBuildRequests(kind, ids, out var requests)) return;

        var plans = requests.Select(_manager.Plan).ToList();
        var invalid = plans.Where(p => !p.IsValid).ToList();
        if (invalid.Count > 0)
        {
            SetStatus(string.Join(" ", invalid.SelectMany(p => p.ValidationErrors).Distinct()), StatusSeverity.Error);
            return;
        }

        if (!_elevation.IsElevated)
        {
            var restart = _dialogs.Confirm("Administrator rights required",
                "Changing feature configuration requires administrator rights. ViVeTool Studio is running without them, so it can only query features.\n\n" +
                "Restart ViVeTool Studio as administrator now? (Windows will show a User Account Control prompt.)", highRisk: false);
            if (restart && !_elevation.TryRestartElevated())
                SetStatus("The application was not restarted as administrator. Nothing was changed.", StatusSeverity.Warning);
            else if (!restart)
                SetStatus("Cancelled. Nothing was changed.", StatusSeverity.Info);
            return;
        }

        IsBusy = true;
        try
        {
            // Show the proposed operation (including current state) before anything is written.
            var preview = new StringBuilder();
            preview.AppendLine("The following will be applied:");
            preview.AppendLine();
            foreach (var plan in plans)
            {
                var meta = _catalog.TryGet(plan.Request.FeatureId);
                var current = await _manager.QueryAsync(plan.Request.FeatureId);
                preview.AppendLine("• " + plan.Summary + (meta?.Name is null ? string.Empty : $" [{meta.Name}]"));
                preview.AppendLine("   Current state: " + current.StateSummary);
                preview.AppendLine("   " + FeatureResultItem.CatalogLine(meta, _manager.OsBuild));
            }
            var warnings = plans.SelectMany(p => p.Warnings).Distinct().ToList();
            warnings.Add("Compatibility of these feature IDs with this Windows build has not been verified. Changes can break Windows functionality.");
            warnings.Add("The previous feature state is recorded for rollback, but this is not a system backup.");
            preview.AppendLine();
            preview.AppendLine("Warnings:");
            foreach (var w in warnings) preview.AppendLine("• " + w);
            var highRisk = plans.Any(p => p.Risk == OperationRisk.High);
            if (highRisk)
            {
                preview.AppendLine();
                preview.AppendLine("This is a HIGHER-RISK change (non-default priority, experiment flag or multi-priority reset).");
            }
            preview.AppendLine();
            preview.Append("Continue?");

            if (!_dialogs.Confirm(highRisk ? $"Confirm higher-risk {kind.ToString().ToLowerInvariant()}" : $"Confirm {kind.ToString().ToLowerInvariant()}", preview.ToString(), highRisk))
            {
                SetStatus("Cancelled. Nothing was changed.", StatusSeverity.Info);
                return;
            }

            SetStatus($"Applying {requests.Count} operation(s)...", StatusSeverity.Info);
            var results = await _changes.ExecuteQueueAsync(requests,
                r => Results.Insert(0, FeatureResultItem.FromOperation(r, _catalog.TryGet(r.Request.FeatureId), _manager.OsBuild)));
            Summarize(results);
        }
        finally { IsBusy = false; }
    }

    private void Summarize(IReadOnlyList<FeatureOperationResult> results)
    {
        var verified = results.Count(r => r.Status == FeatureOperationStatus.Succeeded);
        var unverified = results.Count(r => r.Status == FeatureOperationStatus.SucceededUnverified);
        var noChange = results.Count(r => r.Status == FeatureOperationStatus.NoChange);
        var failed = results.Count(r => !r.IsSuccess);
        var parts = new List<string>();
        if (verified > 0) parts.Add($"{verified} applied and verified");
        if (unverified > 0) parts.Add($"{unverified} reported success but could not be verified");
        if (noChange > 0) parts.Add($"{noChange} needed no change");
        if (failed > 0) parts.Add($"{failed} failed");
        var reboot = results.Any(r => r.RebootRequired) ? " Restart Windows for boot store changes to take full effect." : string.Empty;
        SetStatus(string.Join(", ", parts) + "." + reboot,
            failed > 0 ? StatusSeverity.Error : unverified > 0 ? StatusSeverity.Warning : StatusSeverity.Success);
    }

    private bool TryBuildRequests(FeatureOperationKind kind, IReadOnlyList<uint> ids, out IReadOnlyList<FeatureOperationRequest> requests)
    {
        requests = Array.Empty<FeatureOperationRequest>();
        uint variant = 0, payload = 0;
        if (kind != FeatureOperationKind.Reset)
        {
            if (!Format.TryParseNumber(VariantText, out variant) || variant > 63)
            {
                SetStatus("Variant must be a number from 0 to 63.", StatusSeverity.Error);
                return false;
            }
            if (!Format.TryParseNumber(PayloadText, out payload))
            {
                SetStatus("Variant payload must be a 32-bit number (decimal or 0x hexadecimal).", StatusSeverity.Error);
                return false;
            }
        }
        requests = ids.Select(id => new FeatureOperationRequest(kind, id)
        {
            Stores = SelectedStores.Stores,
            Priority = SelectedPriority.Priority,
            ResetAllPriorities = kind == FeatureOperationKind.Reset && ResetAllPriorities,
            Variant = kind == FeatureOperationKind.Reset ? 0 : variant,
            VariantPayloadKind = kind == FeatureOperationKind.Reset ? RTL_FEATURE_VARIANT_PAYLOAD_KIND.None : SelectedPayloadKind,
            VariantPayload = kind == FeatureOperationKind.Reset ? 0 : payload,
            Experiment = kind != FeatureOperationKind.Reset && Experiment,
        }).ToList();
        return true;
    }

    private IReadOnlyList<uint>? ParseIds()
    {
        var parsed = FeatureIdParser.ParseList(FeatureInput);
        if (!parsed.IsValid)
        {
            SetStatus(string.Join(" ", parsed.Errors), StatusSeverity.Error);
            return null;
        }
        return parsed.Ids;
    }

    private void SetStatus(string message, StatusSeverity severity)
    {
        StatusMessage = message;
        StatusSeverity = severity;
    }
}
