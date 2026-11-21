using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using PhotosWindows.Application.Abstractions;
using PhotosWindows.Application.Library;
using PhotosWindows.Domain;

namespace PhotosWindows.Application.Thumbnails;

/// <summary>Who is waiting for a thumbnail. Interactive work never queues behind background work.</summary>
public enum ThumbnailPriority
{
    /// <summary>The photo open in the viewer: its own lane, ahead of everything else.</summary>
    Viewer,

    /// <summary>The visible grid: the user is looking at it now.</summary>
    Interactive,

    /// <summary>Filling the cache for the whole library after an import.</summary>
    Background,
}

public enum ThumbnailSize
{
    /// <summary>Grid tiles (long side 360 px: crisp at 180 logical px on 200% DPI).</summary>
    Small = 360,

    /// <summary>Fast first paint in the viewer before the full-quality render arrives.</summary>
    Large = 2048,
}

/// <summary>
/// Generates and caches thumbnails under &lt;cache&gt;/thumbs/&lt;2 hex&gt;/&lt;uid&gt;_&lt;size&gt;_v&lt;editVersion&gt;.jpg.
/// The cache is disposable: deleting it only costs regeneration time.
/// </summary>
public sealed class ThumbnailService
{
    private readonly ILibraryContext _context;
    private readonly IAssetRepository _assets;
    private readonly IEditRepository _edits;
    private readonly IImageRenderer _renderer;
    private readonly IPerceptualHasher _hasher;
    private readonly IVideoFrameExtractor? _videoFrames;
    private readonly ILogger<ThumbnailService> _logger;
    // Three lanes with their own capacity: the viewer, the visible grid and background passes. A lane slot is held until
    // the underlying decode has really finished (even after a caller timed out), so the number of decodes running at
    // once can never exceed the lanes' capacity, whatever the native codec does.
    private readonly Lane _viewer;
    private readonly Lane _interactive;
    private readonly Lane _background;
    private readonly int _parallelism;
    private readonly Lock _gate = new();
    private readonly Dictionary<(string Path, ThumbnailPriority Lane), Generation> _inFlight = [];
    private readonly ConcurrentDictionary<string, byte> _overdue = new(StringComparer.Ordinal);
    private int _live;
    private int _foreground;
    private long _lastInteractiveTicks;

    /// <summary>
    /// Longest a caller waits for one thumbnail or hash. A native decode that does not finish keeps its slot (it is
    /// still using the CPU) and the same file is not started again while it runs; callers get "no thumbnail".
    /// </summary>
    internal TimeSpan GenerationTimeout { get; set; } = TimeSpan.FromSeconds(30);

    public ThumbnailService(ILibraryContext context, IAssetRepository assets, IEditRepository edits, IImageRenderer renderer, IPerceptualHasher hasher,
        IEnumerable<IVideoFrameExtractor> videoFrames, ISettingsStore settings, ILogger<ThumbnailService> logger)
    {
        _context = context;
        _assets = assets;
        _edits = edits;
        _renderer = renderer;
        _hasher = hasher;
        _videoFrames = videoFrames.FirstOrDefault();
        _logger = logger;
        _parallelism = Math.Clamp(settings.Load().MaxBackgroundWorkers, 1, 8);
        _viewer = new Lane(2, DecodePriority.Viewer);
        _interactive = new Lane(Math.Max(2, _parallelism), DecodePriority.Interactive);
        _background = new Lane(_parallelism, DecodePriority.Background);
    }

    private sealed class Lane(int capacity, DecodePriority priority)
    {
        public int Capacity { get; } = capacity;

        /// <summary>Priority of this lane's decodes at the process-wide <see cref="HeavyDecodeGate"/>.</summary>
        public DecodePriority Priority { get; } = priority;
        public SemaphoreSlim Slots { get; } = new(capacity);
        public int Waiting;
        public int Overdue;
        public int Busy => Capacity - Slots.CurrentCount;

        /// <summary>Every slot is held by a decode that is past the timeout: waiting would mean waiting for ever.</summary>
        public bool Stalled => Volatile.Read(ref Overdue) >= Capacity;
    }

    /// <summary>One generation shared by every caller asking for the same file in the same lane.</summary>
    private sealed class Generation
    {
        public TaskCompletionSource<string?> Result { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public CancellationTokenSource Cancel { get; } = new();
        public int Waiters;
    }

    /// <summary>Snapshot for diagnostics: lane use, decodes really running and those past the timeout.</summary>
    public ThumbnailStats Stats
    {
        get
        {
            lock (_gate)
            {
                return new ThumbnailStats(_viewer.Busy, _viewer.Waiting, _interactive.Busy, _interactive.Waiting, _background.Busy, _background.Waiting,
                    Volatile.Read(ref _live), _overdue.Count, _inFlight.Count, ForegroundBusy);
            }
        }
    }

    /// <summary>
    /// True while the user is waiting for something on screen: the viewer is loading, or grid thumbnails were requested
    /// in the last two seconds. Background passes stop taking new work meanwhile.
    /// </summary>
    public bool ForegroundBusy =>
        Volatile.Read(ref _foreground) > 0 || _viewer.Busy > 0 || _interactive.Busy > 0 || _interactive.Waiting > 0
        || Environment.TickCount64 - Interlocked.Read(ref _lastInteractiveTicks) < 2000;

    /// <summary>Marks a foreground load (the viewer opening a photo) until disposed; background work yields meanwhile.</summary>
    public IDisposable BeginForeground()
    {
        Interlocked.Increment(ref _foreground);
        return new ForegroundScope(this);
    }

    private sealed class ForegroundScope(ThumbnailService owner) : IDisposable
    {
        private int _disposed;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0) Interlocked.Decrement(ref owner._foreground);
        }
    }

    /// <summary>Raised when a thumbnail becomes available (worker thread).</summary>
    public event EventHandler<long>? ThumbnailReady;

    public static string RelativePath(Guid uid, ThumbnailSize size, int editVersion)
    {
        var n = uid.ToString("N");
        return Path.Combine(n[..2], $"{n}_{(int)size}_v{editVersion}.jpg");
    }

    public string PathFor(Guid uid, ThumbnailSize size, int editVersion) =>
        Path.Combine(_context.RequireCurrent().ThumbnailsPath, RelativePath(uid, size, editVersion));

    /// <summary>Returns the cached thumbnail path if it already exists (no generation).</summary>
    public string? TryGetCached(AssetSummary asset, ThumbnailSize size)
    {
        var path = PathFor(asset.Uid, size, asset.EditVersion);
        return File.Exists(path) ? path : null;
    }

    /// <summary>Returns the cached thumbnail of the asset's current version if it exists, without generating anything.</summary>
    public async Task<string?> TryGetCachedAsync(long assetId, ThumbnailSize size, CancellationToken cancellationToken)
    {
        var asset = await _assets.GetAsync(assetId, cancellationToken).ConfigureAwait(false);
        if (asset is null) return null;
        var version = asset.HasEdits ? (await _edits.GetAsync(assetId, cancellationToken).ConfigureAwait(false))?.Version ?? 0 : 0;
        var path = PathFor(asset.Uid, size, version);
        return File.Exists(path) && new FileInfo(path).Length > 0 ? path : null;
    }

    /// <summary>
    /// First paint for the viewer: the thumbnail embedded in the original (HEIC…), encoded as JPEG, without decoding the
    /// full image and outside the worker lanes. Null when the file has none, is edited, or is a format Skia scales
    /// cheaply (those use the small thumbnail instead). Never runs on the caller's UI thread.
    /// </summary>
    public Task<byte[]?> TryEmbeddedPreviewAsync(long assetId, CancellationToken cancellationToken) => Task.Run(async () =>
    {
        var asset = await _assets.GetAsync(assetId, cancellationToken).ConfigureAwait(false);
        if (asset is null || asset.HasEdits || asset.MediaType != MediaType.Photo) return null;
        var file = await _assets.GetOriginalFileAsync(assetId, cancellationToken).ConfigureAwait(false);
        if (file is null || _context.Current is not { } library) return null;
        var path = library.Resolve(file);
        var family = MediaFormats.FromExtension(path)?.Family ?? FormatFamily.Unknown;
        var sw = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            var rendered = await _renderer.TryRenderEmbeddedThumbnailAsync(path, family, (int)ThumbnailSize.Small, EncodeOptions.ThumbnailJpeg, cancellationToken).ConfigureAwait(false);
            return rendered?.Encoded;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogDebug(ex, "No embedded preview for asset {Id}", assetId);
            return null;
        }
        finally
        {
            Record($"embedded-preview/{asset.Format}", sw.ElapsedMilliseconds);
        }
    }, CancellationToken.None);

    /// <summary>Returns the thumbnail path, generating it if needed. Null when the asset cannot be rendered.</summary>
    public Task<string?> GetOrCreateAsync(long assetId, ThumbnailSize size, CancellationToken cancellationToken) =>
        GetOrCreateCoreAsync(assetId, size, ThumbnailPriority.Interactive, cancellationToken);

    public Task<string?> GetOrCreateAsync(long assetId, ThumbnailSize size, ThumbnailPriority priority, CancellationToken cancellationToken) =>
        GetOrCreateCoreAsync(assetId, size, priority, cancellationToken);

    /// <summary>Slots currently taken in the interactive and background lanes (diagnostics and tests).</summary>
    public (int Interactive, int Background) BusyWorkers => (_interactive.Busy, _background.Busy);

    private Lane LaneFor(ThumbnailPriority priority) => priority switch
    {
        ThumbnailPriority.Viewer => _viewer,
        ThumbnailPriority.Interactive => _interactive,
        _ => _background,
    };

    /// <summary>
    /// Never runs on the caller's UI thread. The catalog returns completed tasks, so without this hop everything up to
    /// the first truly asynchronous call (catalog queries, File.Exists and the StorageFile call of the WIC and video
    /// decoders) ran on the UI thread inside the grid's ContainerContentChanging. That StorageFile call is a
    /// cross-apartment COM call that pumps messages; the re-entrancy made WinUI fail fast (0xC000027B) while a large
    /// import filled the grid with videos (crash dumps of 2026-11-10 and 2026-11-14 on Windows 11).
    /// </summary>
    private Task<string?> GetOrCreateCoreAsync(long assetId, ThumbnailSize size, ThumbnailPriority priority, CancellationToken cancellationToken)
    {
        if (priority == ThumbnailPriority.Interactive) Interlocked.Exchange(ref _lastInteractiveTicks, Environment.TickCount64);
        return SynchronizationContext.Current is null
            ? GetOrCreateOffUiAsync(assetId, size, priority, cancellationToken)
            : Task.Run(() => GetOrCreateOffUiAsync(assetId, size, priority, cancellationToken), CancellationToken.None);
    }

    private async Task<string?> GetOrCreateOffUiAsync(long assetId, ThumbnailSize size, ThumbnailPriority priority, CancellationToken cancellationToken)
    {
        var asset = await _assets.GetAsync(assetId, cancellationToken).ConfigureAwait(false);
        if (asset is null) return null;
        var recipe = asset.HasEdits ? (await _edits.GetAsync(assetId, cancellationToken).ConfigureAwait(false)) : null;
        var version = recipe?.Version ?? 0;
        var path = PathFor(asset.Uid, size, version);
        if (File.Exists(path)) return path;

        // A decode of this file is still running past the timeout: do not start a second one.
        if (_overdue.ContainsKey(path)) return null;
        cancellationToken.ThrowIfCancellationRequested();

        var key = (path, priority);
        Generation generation;
        var start = false;
        lock (_gate)
        {
            if (!_inFlight.TryGetValue(key, out generation!))
            {
                generation = new Generation();
                _inFlight[key] = generation;
                start = true;
            }

            generation.Waiters++;
        }

        if (start) _ = RunGenerationAsync(generation, key, asset, recipe?.Recipe ?? EditRecipe.Identity, size, path, LaneFor(priority));

        try
        {
            return await generation.Result.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            Leave(generation, key);
        }
    }

    /// <summary>
    /// A caller stops waiting. When nobody waits any more, the generation is cancelled: if it has not started it never
    /// decodes; if it has, the decoder is asked to stop (it keeps its slot until it really does).
    /// </summary>
    private void Leave(Generation generation, (string, ThumbnailPriority) key)
    {
        lock (_gate)
        {
            if (--generation.Waiters > 0 || generation.Result.Task.IsCompleted) return;
            if (_inFlight.TryGetValue(key, out var current) && ReferenceEquals(current, generation)) _inFlight.Remove(key);
        }

        generation.Cancel.Cancel();
    }

    private async Task RunGenerationAsync(Generation generation, (string Path, ThumbnailPriority) key, Asset asset, EditRecipe recipe, ThumbnailSize size, string path, Lane lane)
    {
        var token = generation.Cancel.Token;
        try
        {
            var result = await RunInLaneAsync(lane, ct => GenerateInSlotAsync(asset, recipe, size, path, lane == _background, ct), token, () =>
            {
                _logger.LogWarning("Thumbnail for asset {Id} still running after {Timeout} s; it keeps its worker slot and is not started again", asset.Id, GenerationTimeout.TotalSeconds);
                if (size == ThumbnailSize.Small) _ = _assets.SetThumbnailStateAsync(asset.Id, ThumbnailState.Failed, CancellationToken.None);
            }, path).ConfigureAwait(false);
            generation.Result.TrySetResult(result);
        }
        catch (OperationCanceledException)
        {
            generation.Result.TrySetCanceled();
        }
        catch (LaneStalledException)
        {
            // Every slot of this lane is held by stuck decodes: answer "no thumbnail now" instead of waiting for ever.
            generation.Result.TrySetResult(null);
        }
        catch (Exception ex)
        {
            generation.Result.TrySetException(ex);
        }
        finally
        {
            lock (_gate)
            {
                if (_inFlight.TryGetValue(key, out var current) && ReferenceEquals(current, generation)) _inFlight.Remove(key);
            }

            generation.Cancel.Dispose();
        }
    }

    /// <summary>
    /// Runs <paramref name="work"/> in a slot of <paramref name="lane"/>. The slot is released only when the work has
    /// finished, not when the caller stops waiting: a timed-out native decode is still counted (and still occupies its
    /// slot) until it returns. While it runs, <paramref name="overdueKey"/> marks the file so it is not started again.
    /// </summary>
    private async Task<T?> RunInLaneAsync<T>(Lane lane, Func<CancellationToken, Task<T?>> work, CancellationToken token, Action onOverdue, string overdueKey)
    {
        if (lane.Stalled) throw new LaneStalledException();
        Interlocked.Increment(ref lane.Waiting);
        try
        {
            // Re-check periodically: the lane may become stalled while this request waits.
            while (!await lane.Slots.WaitAsync(TimeSpan.FromMilliseconds(250), token).ConfigureAwait(false))
            {
                if (lane.Stalled) throw new LaneStalledException();
            }
        }
        finally
        {
            Interlocked.Decrement(ref lane.Waiting);
        }

        Task<T?> running;
        Interlocked.Increment(ref _live);
        try
        {
            // The lane's priority flows into the decoders (HeavyDecodeGate) through the async context.
            using (HeavyDecodeGate.Use(lane.Priority)) running = work(token);
        }
        catch
        {
            Interlocked.Decrement(ref _live);
            lane.Slots.Release();
            throw;
        }

        var overdue = 0;
        _ = running.ContinueWith(_ =>
        {
            Interlocked.Decrement(ref _live);
            if (Interlocked.Exchange(ref overdue, 0) == 1)
            {
                Interlocked.Decrement(ref lane.Overdue);
                _overdue.TryRemove(overdueKey, out var _);
            }

            lane.Slots.Release();
        }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);

        try
        {
            return await running.WaitAsync(GenerationTimeout, CancellationToken.None).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            if (!running.IsCompleted && Interlocked.CompareExchange(ref overdue, 1, 0) == 0)
            {
                Interlocked.Increment(ref lane.Overdue);
                _overdue.TryAdd(overdueKey, 0);
                onOverdue();
                // The decode may have finished between the check and the mark: undo so the slot is not counted twice.
                if (running.IsCompleted && Interlocked.Exchange(ref overdue, 0) == 1)
                {
                    Interlocked.Decrement(ref lane.Overdue);
                    _overdue.TryRemove(overdueKey, out var _);
                }
            }

            return default;
        }
    }

    /// <summary>Diagnostics: work done per kind ("thumb-360/HEIF", "hash/JPEG"…): count and total milliseconds.</summary>
    private readonly ConcurrentDictionary<string, (long Count, long Ms)> _workStats = new(StringComparer.Ordinal);

    /// <summary>Counts and average/total time per kind of work since the last call (resets).</summary>
    public string TakeWorkStats()
    {
        var parts = new List<string>();
        foreach (var key in _workStats.Keys.Order(StringComparer.Ordinal))
        {
            if (_workStats.TryRemove(key, out var v)) parts.Add($"{key}: {v.Count}x avg {v.Ms / Math.Max(1, v.Count)} ms");
        }

        return parts.Count == 0 ? "no work" : string.Join(", ", parts);
    }

/// <summary>
    /// Background thumbnail of a HEIC/AVIF/RAW: its embedded thumbnail when usable (~30 ms); otherwise a full decode,
    /// one at a time. Windows' HEVC decoder spreads one 12–24 MP decode over every core, so four background full
    /// decodes at once kept a 12-core PC at 80–100 % CPU while the app looked idle (Windows 11, iPhone library with
    /// ~9 % of HEICs lacking a usable embedded thumbnail). The visible grid and the viewer are not limited.
    /// </summary>
    private async Task<RenderedImage?> RenderHeavyInBackgroundAsync(string source, FormatFamily family, ThumbnailSize size, CancellationToken ct)
    {
        var embedded = await _renderer.TryRenderEmbeddedThumbnailAsync(source, family, (int)size, EncodeOptions.ThumbnailJpeg, ct).ConfigureAwait(false);
        if (embedded is not null) return embedded;
        await BackgroundFullDecodes.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            return await _renderer.RenderAsync(source, family, EditRecipe.Identity, (int)size, EncodeOptions.ThumbnailJpeg, ct).ConfigureAwait(false);
        }
        finally
        {
            BackgroundFullDecodes.Release();
        }
    }

    private static readonly SemaphoreSlim BackgroundFullDecodes = new(1, 1);

    private void Record(string kind, long ms) => _workStats.AddOrUpdate(kind, (1, ms), (_, v) => (v.Count + 1, v.Ms + ms));

    private async Task<string?> GenerateInSlotAsync(Asset asset, EditRecipe recipe, ThumbnailSize size, string path, bool background, CancellationToken ct)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            return await GenerateInSlotCoreAsync(asset, recipe, size, path, background, ct).ConfigureAwait(false);
        }
        finally
        {
            Record($"thumb-{(int)size}/{asset.Format}", sw.ElapsedMilliseconds);
        }
    }

    private async Task<string?> GenerateInSlotCoreAsync(Asset asset, EditRecipe recipe, ThumbnailSize size, string path, bool background, CancellationToken ct)
    {
        try
        {
            var library = _context.RequireCurrent();
            var file = await _assets.GetOriginalFileAsync(asset.Id, CancellationToken.None).ConfigureAwait(false);
            if (file is null) return null;
            var source = library.Resolve(file);
            if (!File.Exists(source))
            {
                if (file.IsAvailable) await _assets.UpdateFileAvailabilityAsync(file.Id, false, DateTime.UtcNow, CancellationToken.None).ConfigureAwait(false);
                return null;
            }

            var family = MediaFormats.FromExtension(source)?.Family ?? FormatFamily.Unknown;
            RenderedImage? rendered = null;
            if (asset.MediaType == MediaType.Video)
            {
                if (_videoFrames is not null)
                {
                    var frame = await _videoFrames.ExtractPosterFrameAsync(source, (int)size, ct).ConfigureAwait(false);
                    if (frame is not null)
                    {
                        rendered = await _renderer.RenderAsync(frame, recipe, (int)size, EncodeOptions.ThumbnailJpeg, ct).ConfigureAwait(false);
                    }
                }
            }
            else if (background && recipe.IsIdentity && family is FormatFamily.Heif or FormatFamily.Avif or FormatFamily.Raw)
            {
                rendered = await RenderHeavyInBackgroundAsync(source, family, size, ct).ConfigureAwait(false);
            }
            else
            {
                rendered = await _renderer.RenderAsync(source, family, recipe, (int)size, EncodeOptions.ThumbnailJpeg, ct).ConfigureAwait(false);
            }

            if (rendered is null)
            {
                if (size == ThumbnailSize.Small) await _assets.SetThumbnailStateAsync(asset.Id, ThumbnailState.Failed, CancellationToken.None).ConfigureAwait(false);
                return null;
            }

            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            // Unique temporary name: the interactive and background lanes may write the same thumbnail at once.
            var temp = $"{path}.{Guid.NewGuid():N}.tmp";
            await File.WriteAllBytesAsync(temp, rendered.Encoded).ConfigureAwait(false);
            File.Move(temp, path, overwrite: true);
            DeleteStaleVersions(path, asset.Uid, size);
            if (size == ThumbnailSize.Small)
            {
                await _assets.SetThumbnailStateAsync(asset.Id, ThumbnailState.Ready, CancellationToken.None).ConfigureAwait(false);
            }

            ThumbnailReady?.Invoke(this, asset.Id);
            return path;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Any failure of one file (codecs throw NotImplementedException, COMException, …) only fails that file;
            // it must never escape into the background loop (it stopped all thumbnail work for the session).
            _logger.LogInformation(ex, "Thumbnail could not be created for asset {Id}", asset.Id);
            if (size == ThumbnailSize.Small) await _assets.SetThumbnailStateAsync(asset.Id, ThumbnailState.Failed, CancellationToken.None).ConfigureAwait(false);
            return null;
        }
    }

    /// <summary>
    /// Background pass: generates small thumbnails for pending assets and perceptual hashes for photos.
    /// Returns the number of assets processed. Safe to call repeatedly.
    /// </summary>
    public async Task<int> ProcessPendingAsync(int batchSize, CancellationToken cancellationToken)
    {
        if (_context.Current is null) return 0;
        // The batch runs on the background lane. Items are skipped (left pending for the next pass) while the user is
        // waiting for something on screen, so the viewer and the visible grid get the CPU.
        var options = new ParallelOptions { MaxDegreeOfParallelism = _parallelism, CancellationToken = cancellationToken };
        if (_background.Stalled)
        {
            _logger.LogWarning("Background thumbnails paused: all {Count} background slots are held by decodes that did not return", _background.Capacity);
            return 0;
        }

        var processed = 0;
        var pending = await _assets.GetIdsWithThumbnailStateAsync(ThumbnailState.Pending, batchSize, cancellationToken).ConfigureAwait(false);
        await Parallel.ForEachAsync(pending, options, async (id, ct) =>
        {
            if (ForegroundBusy) return;
            if (_background.Stalled) return;
            await GetOrCreateAsync(id, ThumbnailSize.Small, ThumbnailPriority.Background, ct).ConfigureAwait(false);
            Interlocked.Increment(ref processed);
        }).ConfigureAwait(false);

        var unhashed = await _assets.GetPhotosWithoutPerceptualHashAsync(batchSize, cancellationToken).ConfigureAwait(false);
        await Parallel.ForEachAsync(unhashed, options, async (id, ct) =>
        {
            if (ForegroundBusy || _background.Stalled) return;
            await ComputePerceptualHashAsync(id, ct).ConfigureAwait(false);
            Interlocked.Increment(ref processed);
        }).ConfigureAwait(false);
        return processed;
    }

    private async Task ComputePerceptualHashAsync(long assetId, CancellationToken cancellationToken)
    {
        var library = _context.RequireCurrent();
        var file = await _assets.GetOriginalFileAsync(assetId, cancellationToken).ConfigureAwait(false);
        if (file is null) return;
        var path = library.Resolve(file);
        var family = MediaFormats.FromExtension(path)?.Family ?? FormatFamily.Unknown;

        // The hash only needs a 64 px image: decode the unedited grid thumbnail (already oriented, 360 px) instead of
        // decoding the full original a second time. Falls back to the original when there is no such thumbnail.
        var asset = await _assets.GetAsync(assetId, cancellationToken).ConfigureAwait(false);
        if (asset is { HasEdits: false } && File.Exists(PathFor(asset.Uid, ThumbnailSize.Small, 0)))
        {
            path = PathFor(asset.Uid, ThumbnailSize.Small, 0);
            family = FormatFamily.Jpeg;
        }
        if (!File.Exists(path) || !_renderer.CanDecode(family))
        {
            // Not decodable here: mark it so the background pass does not retry it forever.
            await _assets.SetThumbnailStateAsync(assetId, ThumbnailState.Failed, cancellationToken).ConfigureAwait(false);
            return;
        }

        if (_overdue.ContainsKey(path)) return;
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var kind = family == FormatFamily.Jpeg && path.EndsWith(".jpg", StringComparison.Ordinal) && path.Contains("thumbs", StringComparison.Ordinal) ? "hash/from-thumb" : $"hash/original-{family}";
        try
        {
            // Same lane rules as thumbnails: the slot stays taken until the decode has really returned.
            var raster = await RunInLaneAsync(_background, ct => _renderer.DecodeOrientedAsync(path, family, 64, ct)!, cancellationToken,
                () => _logger.LogWarning("Perceptual hash for asset {Id} still running after {Timeout} s", assetId, GenerationTimeout.TotalSeconds), path).ConfigureAwait(false);
            if (raster is null)
            {
                await _assets.SetThumbnailStateAsync(assetId, ThumbnailState.Failed, CancellationToken.None).ConfigureAwait(false);
                return;
            }

            await _assets.SetPerceptualHashAsync(assetId, _hasher.Compute(raster), cancellationToken).ConfigureAwait(false);
        }
        catch (LaneStalledException)
        {
            // Left as is: retried when the lane recovers.
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogDebug(ex, "Perceptual hash skipped for asset {Id}", assetId);
            await _assets.SetThumbnailStateAsync(assetId, ThumbnailState.Failed, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            Record(kind, sw.ElapsedMilliseconds);
        }
    }

    /// <summary>Deletes cached thumbnails of the given assets (after permanent deletion).</summary>
    public void DeleteFor(IEnumerable<Guid> uids)
    {
        if (_context.Current is null) return;
        foreach (var uid in uids)
        {
            var n = uid.ToString("N");
            var dir = Path.Combine(_context.Current.ThumbnailsPath, n[..2]);
            if (!Directory.Exists(dir)) continue;
            foreach (var f in Directory.EnumerateFiles(dir, n + "_*"))
            {
                TryDelete(f);
            }
        }
    }

    private void DeleteStaleVersions(string current, Guid uid, ThumbnailSize size)
    {
        var dir = Path.GetDirectoryName(current)!;
        foreach (var f in Directory.EnumerateFiles(dir, $"{uid:N}_{(int)size}_v*.jpg"))
        {
            if (!string.Equals(f, current, StringComparison.Ordinal)) TryDelete(f);
        }
    }

    private void TryDelete(string file)
    {
        try
        {
            File.Delete(file);
        }
        catch (IOException ex)
        {
            _logger.LogDebug(ex, "Stale thumbnail kept (in use)");
        }
        catch (UnauthorizedAccessException ex)
        {
            _logger.LogDebug(ex, "Stale thumbnail kept (access denied)");
        }
    }
}

/// <summary>Thumbnail service state for diagnostics. Counts only; no paths.</summary>
public sealed record ThumbnailStats(
    int ViewerBusy, int ViewerWaiting,
    int InteractiveBusy, int InteractiveWaiting,
    int BackgroundBusy, int BackgroundWaiting,
    int DecodesRunning, int DecodesPastTimeout, int Generations, bool ForegroundBusy)
{
    public override string ToString() =>
        $"viewer {ViewerBusy}+{ViewerWaiting}w, grid {InteractiveBusy}+{InteractiveWaiting}w, background {BackgroundBusy}+{BackgroundWaiting}w, " +
        $"decoding {DecodesRunning} ({DecodesPastTimeout} past timeout), generations {Generations}, foreground {ForegroundBusy}, {HeavyDecodeGate.Shared.Stats}, embedded {HeavyDecodeGate.Embedded.Stats}";
}

/// <summary>All slots of a lane are held by decodes that did not return in time.</summary>
internal sealed class LaneStalledException : Exception
{
    public LaneStalledException()
        : base("All worker slots of this lane are held by decodes that did not return.")
    {
    }
}
