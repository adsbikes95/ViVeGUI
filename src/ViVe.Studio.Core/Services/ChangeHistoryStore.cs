// SPDX-License-Identifier: GPL-3.0-or-later
using System.Text.Json;
using ViVe.Studio.Models;

namespace ViVe.Studio.Services;

public interface IChangeHistoryStore
{
    event EventHandler? Changed;

    /// <summary>Message describing a problem found while loading (for example a corrupt file that was set aside).</summary>
    string? LoadWarning { get; }

    Task<IReadOnlyList<ChangeHistoryEntry>> GetEntriesAsync(CancellationToken cancellationToken = default);
    Task AppendAsync(ChangeHistoryEntry entry, CancellationToken cancellationToken = default);
    Task ReplaceAsync(ChangeHistoryEntry entry, CancellationToken cancellationToken = default);
    Task ExportAsync(string path, CancellationToken cancellationToken = default);
}

/// <summary>
/// JSON-file change history. Writes are atomic; a corrupt file is moved aside (never silently overwritten).
/// </summary>
public sealed class JsonChangeHistoryStore : IChangeHistoryStore
{
    private readonly string _path;
    private readonly Func<DateTimeOffset> _clock;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private List<ChangeHistoryEntry>? _entries;

    public JsonChangeHistoryStore(string path, Func<DateTimeOffset>? clock = null)
    {
        _path = path;
        _clock = clock ?? (() => DateTimeOffset.UtcNow);
    }

    public event EventHandler? Changed;

    public string? LoadWarning { get; private set; }

    public async Task<IReadOnlyList<ChangeHistoryEntry>> GetEntriesAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try { return (await EnsureLoadedAsync(cancellationToken).ConfigureAwait(false)).ToList(); }
        finally { _gate.Release(); }
    }

    public async Task AppendAsync(ChangeHistoryEntry entry, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var entries = await EnsureLoadedAsync(cancellationToken).ConfigureAwait(false);
            entries.Add(entry);
            await SaveAsync(entries, cancellationToken).ConfigureAwait(false);
        }
        finally { _gate.Release(); }
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public async Task ReplaceAsync(ChangeHistoryEntry entry, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var entries = await EnsureLoadedAsync(cancellationToken).ConfigureAwait(false);
            var index = entries.FindIndex(e => e.Id == entry.Id);
            if (index < 0) throw new InvalidOperationException($"History entry {entry.Id} not found.");
            entries[index] = entry;
            await SaveAsync(entries, cancellationToken).ConfigureAwait(false);
        }
        finally { _gate.Release(); }
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public async Task ExportAsync(string path, CancellationToken cancellationToken = default)
    {
        IReadOnlyList<ChangeHistoryEntry> snapshot = await GetEntriesAsync(cancellationToken).ConfigureAwait(false);
        await JsonFile.WriteAtomicAsync(path, new ChangeHistoryDocument { Entries = snapshot, ExportedAtUtc = _clock() }, cancellationToken).ConfigureAwait(false);
    }

    private Task SaveAsync(List<ChangeHistoryEntry> entries, CancellationToken cancellationToken) =>
        JsonFile.WriteAtomicAsync(_path, new ChangeHistoryDocument { Entries = entries }, cancellationToken);

    private async Task<List<ChangeHistoryEntry>> EnsureLoadedAsync(CancellationToken cancellationToken)
    {
        if (_entries != null) return _entries;
        if (!File.Exists(_path)) return _entries = new List<ChangeHistoryEntry>();
        try
        {
            await using var stream = File.OpenRead(_path);
            var doc = await JsonSerializer.DeserializeAsync<ChangeHistoryDocument>(stream, JsonFile.Options, cancellationToken).ConfigureAwait(false);
            if (doc == null || doc.SchemaVersion > ChangeHistoryDocument.CurrentSchemaVersion)
                throw new JsonException($"Unsupported history schema version {doc?.SchemaVersion}.");
            return _entries = doc.Entries.Where(e => e != null).ToList();
        }
        catch (JsonException ex)
        {
            var aside = $"{_path}.corrupt-{_clock():yyyyMMddHHmmss}";
            try { File.Move(_path, aside, overwrite: false); } catch (IOException) { }
            LoadWarning = $"The change history file could not be read ({ex.Message}). It was moved to {aside} and a new history was started.";
            return _entries = new List<ChangeHistoryEntry>();
        }
    }
}
