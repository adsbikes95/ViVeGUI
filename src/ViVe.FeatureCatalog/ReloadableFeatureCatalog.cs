// SPDX-License-Identifier: GPL-3.0-or-later
namespace ViVe.FeatureCatalog;

/// <summary>
/// An <see cref="IFeatureCatalog"/> that can be rebuilt in place (for example after an import),
/// so every view sees the new data without restarting the application.
/// </summary>
public sealed class ReloadableFeatureCatalog : IFeatureCatalog
{
    private readonly Func<IFeatureCatalog> _factory;
    private volatile IFeatureCatalog _current;

    public ReloadableFeatureCatalog(Func<IFeatureCatalog> factory)
    {
        _factory = factory;
        _current = factory();
    }

    public event EventHandler? Reloaded;

    public void Reload()
    {
        _current = _factory();
        Reloaded?.Invoke(this, EventArgs.Empty);
    }

    public int Count => _current.Count;
    public IReadOnlyList<string> Categories => _current.Categories;
    public IReadOnlyList<string> LoadMessages => _current.LoadMessages;
    public FeatureCatalogEntry? TryGet(uint featureId) => _current.TryGet(featureId);
    public FeatureCatalogEntry? FindByName(string name) => _current.FindByName(name);
    public IReadOnlyList<FeatureCatalogEntry> Entries => _current.Entries;
    public IReadOnlyList<FeatureCatalogEntry> Search(string query, int maxResults = 200, string? category = null) => _current.Search(query, maxResults, category);
}
