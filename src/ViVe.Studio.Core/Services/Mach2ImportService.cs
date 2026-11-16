// SPDX-License-Identifier: GPL-3.0-or-later
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using ViVe.FeatureCatalog;
using ViVe.FeatureCatalog.Mach2;

namespace ViVe.Studio.Services;

/// <summary>Downloads one file. Abstracted so imports can be tested without the network.</summary>
public interface IFileDownloader
{
    /// <summary>Returns the file's bytes; throws if the response is larger than <paramref name="maxBytes"/>.</summary>
    Task<byte[]> DownloadAsync(Uri uri, long maxBytes, CancellationToken cancellationToken);

    /// <summary>Streams a file to <paramref name="path"/>. Returns false if the server says it does not exist (404).</summary>
    Task<bool> DownloadToFileAsync(Uri uri, string path, long maxBytes, CancellationToken cancellationToken);
}

/// <summary>Plain HTTPS downloads with a size cap. Content is treated as text data only.</summary>
public sealed class HttpFileDownloader : IFileDownloader, IDisposable
{
    private readonly HttpClient _http;

    public HttpFileDownloader(HttpClient? http = null)
    {
        _http = http ?? new HttpClient { Timeout = TimeSpan.FromSeconds(60) };
        if (_http.DefaultRequestHeaders.UserAgent.Count == 0)
            _http.DefaultRequestHeaders.UserAgent.ParseAdd("ViVeToolStudio-CatalogImport/1.0");
    }

    public async Task<byte[]> DownloadAsync(Uri uri, long maxBytes, CancellationToken cancellationToken)
    {
        if (uri.Scheme != Uri.UriSchemeHttps) throw new InvalidOperationException("Only HTTPS downloads are allowed.");
        using var response = await _http.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        if (response.Content.Headers.ContentLength > maxBytes) throw new InvalidDataException($"{uri} is larger than expected.");
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var buffer = new MemoryStream();
        var chunk = new byte[81920];
        int read;
        while ((read = await stream.ReadAsync(chunk, cancellationToken).ConfigureAwait(false)) > 0)
        {
            if (buffer.Length + read > maxBytes) throw new InvalidDataException($"{uri} is larger than expected.");
            buffer.Write(chunk, 0, read);
        }
        return buffer.ToArray();
    }

    public async Task<bool> DownloadToFileAsync(Uri uri, string path, long maxBytes, CancellationToken cancellationToken)
    {
        if (uri.Scheme != Uri.UriSchemeHttps) throw new InvalidOperationException("Only HTTPS downloads are allowed.");
        using var response = await _http.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        if (response.StatusCode == System.Net.HttpStatusCode.NotFound) return false;
        response.EnsureSuccessStatusCode();
        if (response.RequestMessage?.RequestUri is { } final && final.Scheme != Uri.UriSchemeHttps)
            throw new InvalidOperationException("Download was redirected to a non-HTTPS address.");
        if (response.Content.Headers.ContentLength > maxBytes) throw new InvalidDataException($"{uri} is larger than allowed.");
        await using var source = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        await using var target = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None);
        var chunk = new byte[81920];
        long total = 0;
        int read;
        while ((read = await source.ReadAsync(chunk, cancellationToken).ConfigureAwait(false)) > 0)
        {
            total += read;
            if (total > maxBytes) throw new InvalidDataException($"{uri} is larger than allowed.");
            await target.WriteAsync(chunk.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
        }
        return true;
    }

    public void Dispose() => _http.Dispose();
}

public sealed record Mach2ImportProgress(int Completed, int Total, string CurrentPath);

public sealed record Mach2ImportOutcome(bool Success, string Message, Mach2ImportReport? Report, IReadOnlyList<string> Problems);

/// <summary>What is currently imported, read back from the imported catalog file.</summary>
public sealed record Mach2ImportStatus(bool IsImported, int FeatureCount, string? Description, DateTimeOffset? FileTimeUtc);

/// <summary>
/// Imports feature names and IDs from mach2's published feature dumps into a local catalog file
/// (<see cref="IAppPaths.ImportedCatalogDirectory"/>/mach2-import.json), which the catalog loads
/// between the ViVe name dictionary and the curated catalog.
/// Import is data only: dumps are parsed as text and never executed. Downloads come from
/// raw.githubusercontent.com at a pinned commit and must match the SHA-256 in the embedded manifest.
/// </summary>
public sealed class Mach2ImportService
{
    public const string ImportFileName = "mach2-import.json";
    private const int DownloadConcurrency = 4;

    private static readonly JsonSerializerOptions CompactJson = new()
    {
        WriteIndented = false,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly IAppPaths _paths;
    private readonly IFileDownloader _downloader;
    private readonly Mach2Manifest _manifest;
    private readonly Func<DateTimeOffset> _clock;

    public Mach2ImportService(IAppPaths paths, IFileDownloader downloader, Mach2Manifest? manifest = null, Func<DateTimeOffset>? clock = null)
    {
        _paths = paths;
        _downloader = downloader;
        _manifest = manifest ?? Mach2Manifest.Embedded;
        _clock = clock ?? (() => DateTimeOffset.UtcNow);
    }

    public Mach2Manifest Manifest => _manifest;
    public string ImportFilePath => Path.Combine(_paths.ImportedCatalogDirectory, ImportFileName);

    public Mach2ImportStatus GetStatus()
    {
        if (!File.Exists(ImportFilePath)) return new Mach2ImportStatus(false, 0, null, null);
        var loaded = FeatureCatalogLoader.LoadJsonFile(ImportFilePath);
        string? description = null;
        try
        {
            using var stream = File.OpenRead(ImportFilePath);
            using var doc = JsonDocument.Parse(stream, new JsonDocumentOptions { MaxDepth = 16 });
            if (doc.RootElement.TryGetProperty("description", out var d) && d.ValueKind == JsonValueKind.String) description = d.GetString();
        }
        catch (JsonException) { }
        return new Mach2ImportStatus(true, loaded.Entries.Count, description, File.GetLastWriteTimeUtc(ImportFilePath));
    }

    /// <summary>Downloads every dump in the manifest from GitHub (about 46 MB) and imports them.</summary>
    public async Task<Mach2ImportOutcome> ImportFromGitHubAsync(IProgress<Mach2ImportProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        var problems = new List<string>();
        var builder = new Mach2CatalogBuilder();
        var completed = 0;
        var gate = new object();
        using var throttle = new SemaphoreSlim(DownloadConcurrency);
        var tasks = _manifest.Entries.Select(async entry =>
        {
            await throttle.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                var uri = Mach2Source.RawFileUri(entry.Info.RelativePath);
                byte[] data;
                try { data = await _downloader.DownloadAsync(uri, Math.Min(entry.Bytes, Mach2FeatureListParser.MaxListBytes), cancellationToken).ConfigureAwait(false); }
                catch (Exception ex) when (ex is HttpRequestException or InvalidDataException or IOException or TaskCanceledException && !cancellationToken.IsCancellationRequested)
                {
                    lock (gate) problems.Add($"{entry.Info.RelativePath}: download failed ({ex.Message}).");
                    return;
                }
                var result = ParseVerified(entry, data, problems, gate);
                lock (gate)
                {
                    if (result != null) builder.Add(result);
                    completed++;
                    progress?.Report(new Mach2ImportProgress(completed, _manifest.Entries.Count, entry.Info.RelativePath));
                }
            }
            finally { throttle.Release(); }
        }).ToList();
        await Task.WhenAll(tasks).ConfigureAwait(false);
        return await FinishAsync(builder, problems, "GitHub", cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Imports from a local copy of the mach2 repository (its root folder, containing "features").
    /// Files listed in the manifest must match their pinned hash; other dumps with a valid mach2 path are
    /// imported too, with a note that they are not covered by the pinned manifest.
    /// </summary>
    public async Task<Mach2ImportOutcome> ImportFromFolderAsync(string mach2Root, IProgress<Mach2ImportProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        var problems = new List<string>();
        var gate = new object();
        var featuresDir = Path.Combine(mach2Root, "features");
        if (!Directory.Exists(featuresDir))
            return new Mach2ImportOutcome(false, $"No \"features\" folder found in {mach2Root}. Choose the root folder of a mach2 clone.", null, problems);

        var files = Directory.EnumerateFiles(featuresDir, "*.txt", SearchOption.AllDirectories).ToList();
        var builder = new Mach2CatalogBuilder();
        var unpinned = 0;
        for (var i = 0; i < files.Count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var relative = Path.GetRelativePath(mach2Root, files[i]).Replace('\\', '/');
            var info = Mach2ListInfo.TryParse(relative);
            if (info == null) { problems.Add($"{relative}: not a mach2 dump path; skipped."); continue; }
            var length = new FileInfo(files[i]).Length;
            if (length > Mach2FeatureListParser.MaxListBytes) { problems.Add($"{relative}: file is too large; skipped."); continue; }
            var data = await File.ReadAllBytesAsync(files[i], cancellationToken).ConfigureAwait(false);
            var pinned = _manifest.Find(relative);
            Mach2ParseResult? result;
            if (pinned != null) result = ParseVerified(pinned, data, problems, gate);
            else
            {
                unpinned++;
                result = Parse(info, data);
            }
            if (result != null) builder.Add(result);
            progress?.Report(new Mach2ImportProgress(i + 1, files.Count, relative));
        }
        if (unpinned > 0)
            problems.Add($"{unpinned} dump(s) are not in the pinned manifest (commit {Mach2Source.ShortCommit}); they were imported without hash verification.");
        return await FinishAsync(builder, problems, mach2Root, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Deletes the imported catalog. Returns false if there was nothing to remove.</summary>
    public bool RemoveImport()
    {
        if (!File.Exists(ImportFilePath)) return false;
        File.Delete(ImportFilePath);
        return true;
    }

    private static Mach2ParseResult? ParseVerified(Mach2ManifestEntry entry, byte[] data, List<string> problems, object gate)
    {
        var hash = Mach2Manifest.Sha256Hex(data);
        if (!string.Equals(hash, entry.Sha256, StringComparison.Ordinal))
        {
            lock (gate) problems.Add($"{entry.Info.RelativePath}: content does not match the pinned SHA-256; skipped.");
            return null;
        }
        return Parse(entry.Info, data);
    }

    private static Mach2ParseResult Parse(Mach2ListInfo info, byte[] data)
    {
        using var reader = new StringReader(Encoding.UTF8.GetString(data));
        return Mach2FeatureListParser.Parse(reader, info);
    }

    private async Task<Mach2ImportOutcome> FinishAsync(Mach2CatalogBuilder builder, List<string> problems, string origin, CancellationToken cancellationToken)
    {
        if (builder.ListCount == 0)
            return new Mach2ImportOutcome(false, "No mach2 feature dumps could be imported. The existing import (if any) was kept.", null, problems);

        var (document, report) = builder.Build(_clock());
        var check = FeatureCatalogLoader.Validate(document);
        if (check.HasErrors) problems.AddRange(check.Errors.Take(20));

        Directory.CreateDirectory(_paths.ImportedCatalogDirectory);
        var temp = ImportFilePath + $".{Guid.NewGuid():N}.tmp";
        try
        {
            await using (var stream = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                await JsonSerializer.SerializeAsync(stream, document, CompactJson, cancellationToken).ConfigureAwait(false);
            File.Move(temp, ImportFilePath, overwrite: true);
        }
        finally
        {
            if (File.Exists(temp)) File.Delete(temp);
        }

        var message = $"Imported {report.FeatureCount:N0} named feature IDs from {report.ListCount} mach2 dumps (builds {report.OldestBuild} to {report.NewestBuild}) from {origin}.";
        if (problems.Count > 0) message += $" {problems.Count} problem(s) were reported.";
        return new Mach2ImportOutcome(true, message, report, problems);
    }
}
