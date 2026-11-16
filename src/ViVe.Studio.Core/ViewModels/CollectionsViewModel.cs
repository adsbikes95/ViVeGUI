// SPDX-License-Identifier: GPL-3.0-or-later
using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ViVe.Core;
using ViVe.FeatureCatalog;

namespace ViVe.Studio.ViewModels;

/// <summary>One collection in the list on the left of the Collections page.</summary>
public sealed record CollectionSummary(string Name, string Description, int Count)
{
    public string Display => $"{Name} ({Count:N0})";
}

/// <summary>A feature row on the Collections page that can be ticked to send it to Feature Explorer.</summary>
public sealed partial class CollectionFeatureItem : ObservableObject
{
    private readonly Action<CollectionFeatureItem> _changed;

    internal CollectionFeatureItem(FeatureCatalogEntry entry, CollectionAssignment assignment, string catalogLine, bool selected, Action<CollectionFeatureItem> changed)
    {
        Entry = entry;
        Assignment = assignment;
        CatalogLine = catalogLine;
        _isSelected = selected;
        _changed = changed;
    }

    public FeatureCatalogEntry Entry { get; }
    public CollectionAssignment Assignment { get; }
    public uint Id => Entry.Id;
    public string Name => Entry.Name ?? "(no name)";
    public string IdText => Entry.Id.ToString(CultureInfo.InvariantCulture);
    public string ReasonText => Assignment.Reason;
    public string CatalogLine { get; }

    [ObservableProperty] private bool _isSelected;

    partial void OnIsSelectedChanged(bool value) => _changed(this);
}

/// <summary>
/// Browses named features grouped into collections. Grouping is inferred from internal names (see
/// <see cref="FeatureCollectionRules"/>) and is never a claim about what a feature does or whether it is safe.
/// Nothing here changes Windows: ticked IDs are handed to Feature Explorer, which previews and confirms every change.
/// </summary>
public sealed partial class CollectionsViewModel : ObservableObject
{
    public const int MaxShown = 1000;
    public const string InferenceNotice =
        "Collections are guessed from words in each feature's internal name (or the file it was found in). They say nothing about what a feature does, " +
        "whether it shows anything, or whether it is safe to turn on. Many internal features are unfinished or are fixes and test switches.";

    private readonly IFeatureCatalog _catalog;
    private readonly FeaturesViewModel _features;
    private readonly FeatureCollectionRules _rules;
    private readonly int _build;
    private readonly HashSet<uint> _selected = new();
    private List<(FeatureCatalogEntry Entry, CollectionAssignment Assignment)> _assigned = new();

    public CollectionsViewModel(IFeatureCatalog catalog, FeaturesViewModel features, IFeatureManager manager, FeatureCollectionRules? rules = null)
    {
        _catalog = catalog;
        _features = features;
        _rules = rules ?? FeatureCollectionRules.Default;
        _build = manager.OsBuild;
        if (catalog is ReloadableFeatureCatalog reloadable) reloadable.Reloaded += (_, _) => Rebuild();
        Rebuild();
    }

    /// <summary>Raised when ticked features were put into Feature Explorer, so the shell can switch to it.</summary>
    public event EventHandler? SentToExplorer;

    public ObservableCollection<CollectionSummary> Collections { get; } = new();
    public ObservableCollection<CollectionFeatureItem> Features { get; } = new();

    public string Notice => InferenceNotice;
    public string OnlyThisBuildLabel => _build > 0 ? $"Only features a source lists for this build ({_build})" : "Only features a source lists for this build";

    [ObservableProperty] private CollectionSummary? _selectedCollection;
    [ObservableProperty] private string _filterText = string.Empty;
    [ObservableProperty] private bool _onlyThisBuild = true;
    [ObservableProperty] private string _summary = string.Empty;
    [ObservableProperty] private string _selectionText = "Nothing ticked.";

    partial void OnSelectedCollectionChanged(CollectionSummary? value) => RefreshFeatures();
    partial void OnFilterTextChanged(string value) => RefreshFeatures();
    partial void OnOnlyThisBuildChanged(bool value) => RefreshCollections();

    public int SelectedCount => _selected.Count;
    public IReadOnlyCollection<uint> SelectedIds => _selected;

    private void Rebuild()
    {
        _assigned = _catalog.Entries.Where(e => e.Name != null).Select(e => (e, _rules.Assign(e))).ToList();
        _selected.IntersectWith(_assigned.Select(a => a.Entry.Id));
        RefreshCollections();
    }

    private IEnumerable<(FeatureCatalogEntry Entry, CollectionAssignment Assignment)> Visible() =>
        OnlyThisBuild && _build > 0 ? _assigned.Where(a => a.Entry.IsCompatibleWith(_build) == true) : _assigned;

    private void RefreshCollections()
    {
        var counts = Visible().GroupBy(a => a.Assignment.Collection, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.Count(), StringComparer.OrdinalIgnoreCase);
        var known = _rules.Collections.Select(c => new CollectionSummary(c.Name, c.Description, counts.GetValueOrDefault(c.Name))).ToList();
        // Curated, cited categories that are not one of the rule collections.
        var extra = counts.Keys.Where(k => known.All(c => !string.Equals(c.Name, k, StringComparison.OrdinalIgnoreCase)))
            .OrderBy(k => k, StringComparer.OrdinalIgnoreCase)
            .Select(k => new CollectionSummary(k, "Category from a cited catalog source.", counts[k]));
        var previous = SelectedCollection?.Name;
        Collections.Clear();
        foreach (var c in known.Concat(extra)) Collections.Add(c);
        SelectedCollection = Collections.FirstOrDefault(c => c.Name == previous) ?? Collections.FirstOrDefault(c => c.Count > 0) ?? Collections.FirstOrDefault();
        RefreshFeatures();
    }

    private void RefreshFeatures()
    {
        Features.Clear();
        var collection = SelectedCollection;
        if (collection == null) { Summary = "No feature names are loaded."; return; }
        var filter = FilterText.Trim();
        var matches = Visible()
            .Where(a => string.Equals(a.Assignment.Collection, collection.Name, StringComparison.OrdinalIgnoreCase))
            .Where(a => filter.Length == 0 || a.Entry.Id.ToString(CultureInfo.InvariantCulture).StartsWith(filter, StringComparison.Ordinal) ||
                        a.Entry.Name!.Contains(filter, StringComparison.OrdinalIgnoreCase) ||
                        a.Entry.Aliases.Any(n => n.Contains(filter, StringComparison.OrdinalIgnoreCase)))
            .ToList();
        foreach (var (entry, assignment) in matches.Take(MaxShown))
            Features.Add(new CollectionFeatureItem(entry, assignment, FeatureResultItem.CatalogLine(entry, _build), _selected.Contains(entry.Id), OnItemChanged));
        Summary = matches.Count == 0 ? "No features match."
            : matches.Count > MaxShown ? $"Showing the first {MaxShown:N0} of {matches.Count:N0} features. Type in the filter to narrow the list."
            : $"{matches.Count:N0} features.";
    }

    private void OnItemChanged(CollectionFeatureItem item)
    {
        if (item.IsSelected) _selected.Add(item.Id); else _selected.Remove(item.Id);
        UpdateSelection();
    }

    private void UpdateSelection()
    {
        SelectionText = _selected.Count == 0 ? "Nothing ticked." : $"{_selected.Count:N0} feature{(_selected.Count == 1 ? "" : "s")} ticked.";
        OnPropertyChanged(nameof(SelectedCount));
        SendToExplorerCommand.NotifyCanExecuteChanged();
        ClearSelectionCommand.NotifyCanExecuteChanged();
    }

    private bool HasSelection() => _selected.Count > 0;

    [RelayCommand(CanExecute = nameof(HasSelection))]
    private void ClearSelection()
    {
        _selected.Clear();
        foreach (var item in Features) item.IsSelected = false;
        UpdateSelection();
    }

    /// <summary>
    /// Puts the ticked IDs into Feature Explorer's ID box. Nothing is changed here: Feature Explorer's Enable, Disable
    /// and Reset buttons show the usual preview and ask for confirmation first.
    /// </summary>
    [RelayCommand(CanExecute = nameof(HasSelection))]
    private void SendToExplorer()
    {
        var ids = _selected.OrderBy(i => i).Select(i => i.ToString(CultureInfo.InvariantCulture));
        _features.FeatureInput = string.Join(", ", ids);
        _features.SetStatus($"{_selected.Count:N0} feature ID{(_selected.Count == 1 ? "" : "s")} from Collections. Query them to see their current state; " +
                            "Enable, Disable and Reset show a preview and ask before changing anything.", StatusSeverity.Info);
        SentToExplorer?.Invoke(this, EventArgs.Empty);
    }
}
