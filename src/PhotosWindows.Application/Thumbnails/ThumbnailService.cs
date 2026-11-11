using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using PhotosWindows.Application.Abstractions;
using PhotosWindows.Application.Library;
using PhotosWindows.Domain;

namespace PhotosWindows.Application.Thumbnails;

/// <summary>Who is waiting for a thumbnail. Interactive work never queues behind background work.</summary>
public enum ThumbnailPriority
{
    /// <summary>The viewer or the visible grid: the user is looking at it now.</summary>
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
    // Two lanes with their own capacity: background passes (whole-library thumbnails and perceptual hashes) can occupy
    // every background slot, or even be stuck in a native decoder, without delaying the photo the user opens.
    private readonly SemaphoreSlim _workers;
    private readonly SemaphoreSlim _interactive;
    private readonly int _parallelism;
    private readonly ConcurrentDictionary<(string Path, ThumbnailPriority Lane), Lazy<Task<string?>>> _inFlight = new();

    /// <summary>
    /// Longest a single thumbnail or hash may hold a worker slot. A native decoder that never returns cannot be
    /// aborted, but its slot is released and the item is marked failed instead of blocking the lane for ever.
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
        _workers = new SemaphoreSlim(_parallelism);
        _interactive = new SemaphoreSlim(Math.Max(2, _parallelism));
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

    /// <summary>Returns the thumbnail path, generating it if needed. Null when the asset cannot be rendered.</summary>
    public Task<string?> GetOrCreateAsync(long assetId, ThumbnailSize size, CancellationToken cancellationToken) =>
        GetOrCreateCoreAsync(assetId, size, ThumbnailPriority.Interactive, cancellationToken);

    public Task<string?> GetOrCreateAsync(long assetId, ThumbnailSize size, ThumbnailPriority priority, CancellationToken cancellationToken) =>
        GetOrCreateCoreAsync(assetId, size, priority, cancellationToken);

    /// <summary>Slots currently taken in each lane (diagnostics).</summary>
    public (int Interactive, int Background) BusyWorkers => (Math.Max(2, _parallelism) - _interactive.CurrentCount, _parallelism - _workers.CurrentCount);

    private async Task<string?> GetOrCreateCoreAsync(long assetId, ThumbnailSize size, ThumbnailPriority priority, CancellationToken cancellationToken)
    {
        var asset = await _assets.GetAsync(assetId, cancellationToken).ConfigureAwait(false);
        if (asset is null) return null;
        var recipe = asset.HasEdits ? (await _edits.GetAsync(assetId, cancellationToken).ConfigureAwait(false)) : null;
        var version = recipe?.Version ?? 0;
        var path = PathFor(asset.Uid, size, version);
        if (File.Exists(path)) return path;

        // Identical requests in the same lane share one generation. The entry is removed as soon as the generation ends
        // (success, failure or timeout), whether or not anyone is still waiting for it.
        var key = (path, priority);
        var lazy = _inFlight.GetOrAdd(key, k => new Lazy<Task<string?>>(() =>
        {
            var task = GenerateAsync(asset, recipe?.Recipe ?? EditRecipe.Identity, size, path, priority);
            _ = task.ContinueWith(_ => _inFlight.TryRemove(k, out var _), CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
            return task;
        }));

        // A caller that stops waiting (navigated away) does not cancel a generation others may share.
        return await lazy.Value.WaitAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task<string?> GenerateAsync(Asset asset, EditRecipe recipe, ThumbnailSize size, string path, ThumbnailPriority priority)
    {
        var lane = priority == ThumbnailPriority.Interactive ? _interactive : _workers;
        await lane.WaitAsync().ConfigureAwait(false);
        try
        {
            return await GenerateInSlotAsync(asset, recipe, size, path).WaitAsync(GenerationTimeout).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            _logger.LogWarning("Thumbnail for asset {Id} took longer than {Timeout} s; marked as failed", asset.Id, GenerationTimeout.TotalSeconds);
            if (size == ThumbnailSize.Small) await _assets.SetThumbnailStateAsync(asset.Id, ThumbnailState.Failed, CancellationToken.None).ConfigureAwait(false);
            return null;
        }
        finally
        {
            lane.Release();
        }
    }

    private async Task<string?> GenerateInSlotAsync(Asset asset, EditRecipe recipe, ThumbnailSize size, string path)
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
                    var frame = await _videoFrames.ExtractPosterFrameAsync(source, (int)size, CancellationToken.None).ConfigureAwait(false);
                    if (frame is not null)
                    {
                        rendered = await _renderer.RenderAsync(frame, recipe, (int)size, EncodeOptions.ThumbnailJpeg, CancellationToken.None).ConfigureAwait(false);
                    }
                }
            }
            else
            {
                rendered = await _renderer.RenderAsync(source, family, recipe, (int)size, EncodeOptions.ThumbnailJpeg, CancellationToken.None).ConfigureAwait(false);
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
        catch (Exception ex) when (ex is MediaDecodeException or IOException or UnauthorizedAccessException or InvalidOperationException or ArgumentException
                                       or System.Runtime.InteropServices.ExternalException or OutOfMemoryException)
        {
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
        // The batch runs on all background workers (the semaphore still bounds decoding, shared with on-demand
        // requests from the grid); previously each item waited for the previous one, using a single core.
        var options = new ParallelOptions { MaxDegreeOfParallelism = _parallelism, CancellationToken = cancellationToken };
        var pending = await _assets.GetIdsWithThumbnailStateAsync(ThumbnailState.Pending, batchSize, cancellationToken).ConfigureAwait(false);
        await Parallel.ForEachAsync(pending, options, async (id, ct) => await GetOrCreateAsync(id, ThumbnailSize.Small, ThumbnailPriority.Background, ct).ConfigureAwait(false)).ConfigureAwait(false);

        var unhashed = await _assets.GetPhotosWithoutPerceptualHashAsync(batchSize, cancellationToken).ConfigureAwait(false);
        await Parallel.ForEachAsync(unhashed, options, async (id, ct) => await ComputePerceptualHashAsync(id, ct).ConfigureAwait(false)).ConfigureAwait(false);
        return pending.Count + unhashed.Count;
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

        await _workers.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var raster = await _renderer.DecodeOrientedAsync(path, family, 64, cancellationToken).WaitAsync(GenerationTimeout, cancellationToken).ConfigureAwait(false);
            await _assets.SetPerceptualHashAsync(assetId, _hasher.Compute(raster), cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is MediaDecodeException or IOException or UnauthorizedAccessException or TimeoutException
                                       or System.Runtime.InteropServices.ExternalException or OutOfMemoryException)
        {
            _logger.LogDebug(ex, "Perceptual hash skipped for asset {Id}", assetId);
            await _assets.SetThumbnailStateAsync(assetId, ThumbnailState.Failed, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _workers.Release();
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
