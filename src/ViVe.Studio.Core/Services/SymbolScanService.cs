// SPDX-License-Identifier: GPL-3.0-or-later
using System.Collections.Concurrent;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using ViVe.FeatureCatalog;
using ViVe.FeatureCatalog.Symbols;

namespace ViVe.Studio.Services;

public sealed record ScanRoot(string Path, bool Recursive);

public sealed record SymbolScanProgress(string Phase, int Completed, int Total, string? Current, long BytesDownloaded);

public sealed record SymbolScanOutcome(
    bool Success,
    bool Cancelled,
    string Message,
    int BinariesScanned,
    int BinariesWithFeatures,
    int FeatureCount,
    IReadOnlyList<string> NewNamesSincePreviousScan,
    IReadOnlyList<string> Problems);

/// <summary>Result of one local scan, saved per build so scans of different builds can be compared.</summary>
public sealed record BuildScanResult
{
    [JsonPropertyName("buildText")] public string BuildText { get; init; } = string.Empty;
    [JsonPropertyName("build")] public int Build { get; init; }
    [JsonPropertyName("scannedAtUtc")] public DateTimeOffset ScannedAtUtc { get; init; }
    [JsonPropertyName("binaries")] public IReadOnlyList<ScannedBinary> Binaries { get; init; } = Array.Empty<ScannedBinary>();
}

public sealed record ScannedBinary
{
    [JsonPropertyName("file")] public string File { get; init; } = string.Empty;
    [JsonPropertyName("pdb")] public string Pdb { get; init; } = string.Empty;
    [JsonPropertyName("features")] public IReadOnlyList<ScannedFeature> Features { get; init; } = Array.Empty<ScannedFeature>();
}

/// <summary>
/// "Scan this build": finds the feature names and IDs compiled into this PC's Windows binaries.
/// For every 64-bit binary that references the feature-staging API it downloads the matching public PDB
/// from the Microsoft symbol server (msdl.microsoft.com, HTTPS), confirms the PDB's GUID and age match the
/// binary, and reads <c>Feature_&lt;Name&gt;__descriptor</c> symbols (method after mach2 by riverar).
/// Everything is read as data. Results are cached per PDB, so a re-scan after an update only downloads the
/// symbols of binaries that changed, and each build's results are kept for comparison.
/// </summary>
public sealed class SymbolScanService
{
    public const string CatalogFileName = "local-scan.json";
    public const string SymbolServer = "https://msdl.microsoft.com/download/symbols";
    private const long MaxBinaryBytes = 512L * 1024 * 1024;
    private const long MaxPdbBytes = 2L * 1024 * 1024 * 1024;
    private const int DownloadConcurrency = 4;
    private static readonly string[] Extensions = { ".dll", ".exe", ".sys" };

    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = false,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        MaxDepth = 16,
    };

    private readonly IAppPaths _paths;
    private readonly IFileDownloader _downloader;
    private readonly Func<DateTimeOffset> _clock;

    public SymbolScanService(IAppPaths paths, IFileDownloader downloader, Func<DateTimeOffset>? clock = null)
    {
        _paths = paths;
        _downloader = downloader;
        _clock = clock ?? (() => DateTimeOffset.UtcNow);
    }

    public string ScanDirectory => Path.Combine(_paths.DataDirectory, "symbol-scan");
    public string CatalogPath => Path.Combine(_paths.ImportedCatalogDirectory, CatalogFileName);
    private string CachePath => Path.Combine(ScanDirectory, "pdb-cache.json");

    /// <summary>The Windows folders scanned by default (64-bit system binaries and inbox apps).</summary>
    public static IReadOnlyList<ScanRoot> DefaultRoots()
    {
        var windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        if (string.IsNullOrEmpty(windows)) return Array.Empty<ScanRoot>();
        return new[]
        {
            new ScanRoot(windows, false),
            new ScanRoot(Path.Combine(windows, "System32"), true),
            new ScanRoot(Path.Combine(windows, "SystemApps"), true),
            new ScanRoot(Path.Combine(windows, "ImmersiveControlPanel"), true),
        };
    }

    public static Uri PdbUri(PdbIdentity id) =>
        new($"{SymbolServer}/{Uri.EscapeDataString(id.PdbFileName)}/{id.SymbolServerKey}/{Uri.EscapeDataString(id.PdbFileName)}");

    public IReadOnlyList<BuildScanResult> LoadScans()
    {
        if (!Directory.Exists(ScanDirectory)) return Array.Empty<BuildScanResult>();
        var list = new List<BuildScanResult>();
        foreach (var file in Directory.EnumerateFiles(ScanDirectory, "scan-*.json"))
        {
            try
            {
                using var s = File.OpenRead(file);
                if (JsonSerializer.Deserialize<BuildScanResult>(s, Json) is { } r) list.Add(r);
            }
            catch (Exception ex) when (ex is JsonException or IOException) { }
        }
        return list.OrderBy(r => r.ScannedAtUtc).ToList();
    }

    public async Task<SymbolScanOutcome> ScanAsync(string buildText, int build, IReadOnlyList<ScanRoot> roots,
        IProgress<SymbolScanProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        var problems = new ConcurrentQueue<string>();
        Directory.CreateDirectory(ScanDirectory);
        var cache = LoadCache();

        // 1. Find candidate binaries (cheap local reads only).
        var files = EnumerateFiles(roots, problems).ToList();
        var candidates = new Dictionary<string, (string File, PdbIdentity Id)>(StringComparer.OrdinalIgnoreCase);
        var perFileKey = new List<(string File, string Key)>();
        for (var i = 0; i < files.Count; i++)
        {
            if (cancellationToken.IsCancellationRequested) return Cancelled(problems);
            if (i % 50 == 0) progress?.Report(new SymbolScanProgress("Finding binaries", i, files.Count, files[i], 0));
            byte[] bytes;
            try
            {
                var length = new FileInfo(files[i]).Length;
                if (length < 1024 || length > MaxBinaryBytes) continue;
                bytes = await File.ReadAllBytesAsync(files[i], cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { continue; }
            catch (OperationCanceledException) { return Cancelled(problems); }
            if (!PeImage.ReferencesFeatureStaging(bytes)) continue;
            var image = PeImage.TryParse(bytes);
            if (image is not { Is64BitWindows: true }) continue;
            var id = image.GetPdbIdentity();
            if (id == null) continue;
            var key = CacheKey(id);
            candidates.TryAdd(key, (files[i], id));
            perFileKey.Add((files[i], key));
        }

        // 2. Scan each distinct PDB (cached results are reused).
        var completed = 0;
        long downloaded = 0;
        var tempDir = Path.Combine(ScanDirectory, "tmp");
        Directory.CreateDirectory(tempDir);
        using var throttle = new SemaphoreSlim(DownloadConcurrency);
        var work = candidates.Where(c => !cache.ContainsKey(c.Key)).ToList();
        var tasks = work.Select(async c =>
        {
            await throttle.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                var entry = await ScanOneAsync(c.Value.File, c.Value.Id, tempDir, problems, b => Interlocked.Add(ref downloaded, b), cancellationToken).ConfigureAwait(false);
                if (entry != null) cache[c.Key] = entry;
                var done = Interlocked.Increment(ref completed);
                progress?.Report(new SymbolScanProgress("Reading symbols", done, work.Count, Path.GetFileName(c.Value.File), Interlocked.Read(ref downloaded)));
                if (done % 25 == 0) await SaveCacheAsync(cache).ConfigureAwait(false);
            }
            finally { throttle.Release(); }
        }).ToList();
        try { await Task.WhenAll(tasks).ConfigureAwait(false); }
        catch (OperationCanceledException)
        {
            await SaveCacheAsync(cache).ConfigureAwait(false);
            return Cancelled(problems);
        }
        finally { TryDeleteDirectory(tempDir); }
        await SaveCacheAsync(cache).ConfigureAwait(false);

        // 3. Save this build's result and rebuild the scan catalog from every saved build.
        var previous = LoadScans().Where(s => s.BuildText != buildText).OrderBy(s => s.Build).ThenBy(s => s.ScannedAtUtc).LastOrDefault();
        var binaries = perFileKey
            .Where(f => cache.TryGetValue(f.Key, out var e) && e.Features.Count > 0)
            .Select(f => new ScannedBinary { File = f.File, Pdb = candidates[f.Key].Id.PdbFileName, Features = cache[f.Key].Features })
            .ToList();
        var result = new BuildScanResult { BuildText = buildText, Build = build, ScannedAtUtc = _clock(), Binaries = binaries };
        await WriteJsonAtomicAsync(Path.Combine(ScanDirectory, $"scan-{SafeName(buildText)}.json"), result).ConfigureAwait(false);
        await WriteJsonAtomicAsync(CatalogPath, BuildCatalog(LoadScans())).ConfigureAwait(false);

        var names = binaries.SelectMany(b => b.Features).Select(f => f.Name).Distinct(StringComparer.Ordinal).ToList();
        var newNames = previous == null
            ? Array.Empty<string>()
            : names.Except(previous.Binaries.SelectMany(b => b.Features).Select(f => f.Name), StringComparer.Ordinal).OrderBy(n => n, StringComparer.OrdinalIgnoreCase).ToArray();
        var featureCount = binaries.SelectMany(b => b.Features).Select(f => f.Id).Distinct().Count();
        var message = $"Scanned {candidates.Count:N0} binaries of build {buildText}: {featureCount:N0} named feature IDs in {binaries.Count:N0} of them.";
        if (previous != null) message += $" {newNames.Length:N0} names are new since the scan of build {previous.BuildText}.";
        if (downloaded > 0) message += $" Downloaded {downloaded / (1024.0 * 1024.0):N0} MB of symbols.";
        var problemList = problems.Take(50).ToList();
        if (!problems.IsEmpty) message += $" {problems.Count} problem(s) were reported.";
        return new SymbolScanOutcome(true, false, message, candidates.Count, binaries.Count, featureCount, newNames, problemList);
    }

    private async Task<CachedPdb?> ScanOneAsync(string file, PdbIdentity id, string tempDir, ConcurrentQueue<string> problems,
        Action<long> addDownloaded, CancellationToken cancellationToken)
    {
        var temp = Path.Combine(tempDir, Guid.NewGuid().ToString("N") + ".pdb");
        try
        {
            bool found;
            try { found = await _downloader.DownloadToFileAsync(PdbUri(id), temp, MaxPdbBytes, cancellationToken).ConfigureAwait(false); }
            catch (Exception ex) when (ex is HttpRequestException or InvalidDataException or IOException or InvalidOperationException ||
                                       ex is TaskCanceledException && !cancellationToken.IsCancellationRequested)
            {
                problems.Enqueue($"{Path.GetFileName(file)}: symbol download failed ({ex.Message}).");
                return null; // not cached, retried next time
            }
            if (!found) return new CachedPdb { NoSymbols = true };
            addDownloaded(new FileInfo(temp).Length);

            var bytes = await File.ReadAllBytesAsync(file, cancellationToken).ConfigureAwait(false);
            var image = PeImage.TryParse(bytes);
            if (image == null || image.GetPdbIdentity() is not { } current || CacheKey(current) != CacheKey(id))
            {
                problems.Enqueue($"{Path.GetFileName(file)} changed during the scan; skipped.");
                return null;
            }
            await using var stream = new FileStream(temp, FileMode.Open, FileAccess.Read, FileShare.Read);
            var pdb = PdbReader.Open(stream);
            var (guid, _) = pdb.ReadIdentity();
            if (guid != id.Guid)
            {
                problems.Enqueue($"{Path.GetFileName(file)}: the symbol server returned a PDB that does not match the binary; skipped.");
                return null;
            }
            var scan = FeatureSymbolScanner.Scan(image, pdb);
            foreach (var w in scan.Warnings) problems.Enqueue($"{Path.GetFileName(file)}: {w}");
            return new CachedPdb { Features = scan.Features, UnnamedCount = scan.UnnamedCount };
        }
        catch (InvalidDataException ex)
        {
            problems.Enqueue($"{Path.GetFileName(file)}: unreadable PDB ({ex.Message}).");
            return new CachedPdb { Unreadable = true };
        }
        finally
        {
            try { if (File.Exists(temp)) File.Delete(temp); } catch (IOException) { }
        }
    }

    /// <summary>
    /// Catalog entries from every saved scan: the name seen in the newest scanned build, older names as aliases,
    /// and one build entry per scanned build the ID was found in (scans prove presence only for those builds).
    /// </summary>
    /// <summary>File name of a stored Windows path, also when the scan is read on another OS.</summary>
    private static string FileNameOf(string path) => path[(path.LastIndexOfAny(new[] { '\\', '/' }) + 1)..];

    internal static FeatureCatalogDocument BuildCatalog(IReadOnlyList<BuildScanResult> scans)
    {
        var ordered = scans.OrderBy(s => s.Build).ThenBy(s => s.ScannedAtUtc).ToList();
        var byId = new Dictionary<uint, List<(BuildScanResult Scan, string Name, string File)>>();
        foreach (var scan in ordered)
            foreach (var bin in scan.Binaries)
                foreach (var f in bin.Features)
                {
                    if (f.Id == 0 || !FeatureCatalogLoader.IsValidName(f.Name)) continue;
                    if (!byId.TryGetValue(f.Id, out var list)) byId[f.Id] = list = new();
                    list.Add((scan, f.Name, bin.File));
                }

        var entries = byId.OrderBy(kv => kv.Key).Select(kv =>
        {
            var latest = kv.Value[^1];
            var builds = kv.Value.GroupBy(v => v.Scan.BuildText).Select(g =>
            {
                var files = g.Select(v => FileNameOf(v.File)).Distinct(StringComparer.OrdinalIgnoreCase).Take(5).ToList();
                return new BuildCompatibility
                {
                    MinBuild = g.First().Scan.Build,
                    MaxBuild = g.First().Scan.Build,
                    Note = $"Found by a local symbol scan of build {g.Key} in {string.Join(", ", files)}.",
                    Source = "Microsoft public symbols for " + string.Join(", ", files),
                };
            }).ToList();
            return new FeatureCatalogEntry
            {
                Id = kv.Key,
                Name = latest.Name,
                Aliases = kv.Value.Select(v => v.Name).Where(n => !string.Equals(n, latest.Name, StringComparison.OrdinalIgnoreCase))
                    .Distinct(StringComparer.OrdinalIgnoreCase).Take(8).ToArray(),
                Source = "Local symbol scan (Microsoft public symbol server; method after mach2 by Rafael Rivera, github.com/riverar/mach2, GPL-3.0)",
                VerificationStatus = VerificationStatus.Verified,
                Builds = builds,
                Modules = kv.Value.Select(v => FileNameOf(v.File)).Where(FeatureCatalogLoader.IsValidModuleName)
                    .Distinct(StringComparer.OrdinalIgnoreCase).Take(8).ToArray(),
            };
        }).ToList();

        return new FeatureCatalogDocument
        {
            CatalogVersion = "local-scan",
            Description = "Feature names and IDs read from this PC's Windows binaries and their public symbols. Builds scanned: " +
                          string.Join(", ", ordered.Select(s => s.BuildText).Distinct()) + ". No descriptions or categories.",
            Features = entries,
        };
    }

    private static string CacheKey(PdbIdentity id) => $"{id.PdbFileName.ToLowerInvariant()}/{id.SymbolServerKey}";

    private static string SafeName(string text) => string.Concat(text.Select(c => char.IsAsciiLetterOrDigit(c) || c == '.' ? c : '_'));

    private static IEnumerable<string> EnumerateFiles(IReadOnlyList<ScanRoot> roots, ConcurrentQueue<string> problems)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var root in roots)
        {
            if (!Directory.Exists(root.Path)) continue;
            var options = new EnumerationOptions
            {
                RecurseSubdirectories = root.Recursive,
                IgnoreInaccessible = true,
                AttributesToSkip = FileAttributes.ReparsePoint | FileAttributes.Offline,
                MatchCasing = MatchCasing.CaseInsensitive,
            };
            IEnumerable<string> files;
            try { files = Directory.EnumerateFiles(root.Path, "*", options).ToList(); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { problems.Enqueue($"{root.Path}: {ex.Message}"); continue; }
            foreach (var f in files)
                if (Extensions.Contains(Path.GetExtension(f), StringComparer.OrdinalIgnoreCase) && seen.Add(f))
                    yield return f;
        }
    }

    private static SymbolScanOutcome Cancelled(ConcurrentQueue<string> problems) =>
        new(false, true, "Scan cancelled. Symbols read so far are cached, so the next scan continues from there.", 0, 0, 0, Array.Empty<string>(), problems.Take(20).ToList());

    private sealed record CachedPdb
    {
        [JsonPropertyName("features")] public IReadOnlyList<ScannedFeature> Features { get; init; } = Array.Empty<ScannedFeature>();
        [JsonPropertyName("unnamed")] public int UnnamedCount { get; init; }
        [JsonPropertyName("noSymbols")] public bool NoSymbols { get; init; }
        [JsonPropertyName("unreadable")] public bool Unreadable { get; init; }
    }

    private ConcurrentDictionary<string, CachedPdb> LoadCache()
    {
        try
        {
            if (File.Exists(CachePath))
            {
                using var s = File.OpenRead(CachePath);
                var d = JsonSerializer.Deserialize<Dictionary<string, CachedPdb>>(s, Json);
                if (d != null) return new ConcurrentDictionary<string, CachedPdb>(d, StringComparer.OrdinalIgnoreCase);
            }
        }
        catch (Exception ex) when (ex is JsonException or IOException) { }
        return new ConcurrentDictionary<string, CachedPdb>(StringComparer.OrdinalIgnoreCase);
    }

    private readonly SemaphoreSlim _cacheWrite = new(1);

    private async Task SaveCacheAsync(ConcurrentDictionary<string, CachedPdb> cache)
    {
        await _cacheWrite.WaitAsync().ConfigureAwait(false);
        try { await WriteJsonAtomicAsync(CachePath, new SortedDictionary<string, CachedPdb>(cache, StringComparer.OrdinalIgnoreCase)).ConfigureAwait(false); }
        finally { _cacheWrite.Release(); }
    }

    private static async Task WriteJsonAtomicAsync<T>(string path, T value)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temp = path + $".{Guid.NewGuid():N}.tmp";
        try
        {
            await using (var stream = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                await JsonSerializer.SerializeAsync(stream, value, Json).ConfigureAwait(false);
            File.Move(temp, path, overwrite: true);
        }
        finally { if (File.Exists(temp)) File.Delete(temp); }
    }

    private static void TryDeleteDirectory(string dir)
    {
        try { if (Directory.Exists(dir)) Directory.Delete(dir, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    /// <summary>Deletes all scan results, the cache and the scan catalog.</summary>
    public bool RemoveScans()
    {
        var any = File.Exists(CatalogPath) || Directory.Exists(ScanDirectory);
        if (File.Exists(CatalogPath)) File.Delete(CatalogPath);
        TryDeleteDirectory(ScanDirectory);
        return any;
    }

    internal static string FormatBuild(int build, int? revision) =>
        revision is int r ? $"{build}.{r}" : build.ToString(CultureInfo.InvariantCulture);
}
