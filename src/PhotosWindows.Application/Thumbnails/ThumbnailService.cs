using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using PhotosWindows.Application.Abstractions;
using PhotosWindows.Application.Library;
using PhotosWindows.Domain;

namespace PhotosWindows.Application.Thumbnails;

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
    private readonly SemaphoreSlim _workers;
    private readonly ConcurrentDictionary<string, Lazy<Task<string?>>> _inFlight = new(StringComparer.Ordinal);

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
        _workers = new SemaphoreSlim(Math.Clamp(settings.Load().MaxBackgroundWorkers, 1, 8));
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

    /// <summary>Returns the thumbnail path, generating it if needed. Null when the asset cannot be rendered.</summary>
    public Task<string?> GetOrCreateAsync(long assetId, ThumbnailSize size, CancellationToken cancellationToken) =>
        GetOrCreateCoreAsync(assetId, size, cancellationToken);

    private async Task<string?> GetOrCreateCoreAsync(long assetId, ThumbnailSize size, CancellationToken cancellationToken)
    {
        var asset = await _assets.GetAsync(assetId, cancellationToken).ConfigureAwait(false);
        if (asset is null) return null;
        var recipe = asset.HasEdits ? (await _edits.GetAsync(assetId, cancellationToken).ConfigureAwait(false)) : null;
        var version = recipe?.Version ?? 0;
        var path = PathFor(asset.Uid, size, version);
        if (File.Exists(path)) return path;

        var lazy = _inFlight.GetOrAdd(path, _ => new Lazy<Task<string?>>(() => GenerateAsync(asset, recipe?.Recipe ?? EditRecipe.Identity, size, path)));
        try
        {
            return await lazy.Value.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            if (lazy.IsValueCreated && lazy.Value.IsCompleted) _inFlight.TryRemove(path, out _);
        }
    }

    private async Task<string?> GenerateAsync(Asset asset, EditRecipe recipe, ThumbnailSize size, string path)
    {
        // Generation is not tied to the caller's token: other callers may share the result.
        await _workers.WaitAsync().ConfigureAwait(false);
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
                        rendered = await Task.Run(() => _renderer.Render(frame, recipe, (int)size, EncodeOptions.ThumbnailJpeg, CancellationToken.None)).ConfigureAwait(false);
                    }
                }
            }
            else
            {
                rendered = await Task.Run(() => _renderer.Render(source, family, recipe, (int)size, EncodeOptions.ThumbnailJpeg, CancellationToken.None)).ConfigureAwait(false);
            }

            if (rendered is null)
            {
                if (size == ThumbnailSize.Small) await _assets.SetThumbnailStateAsync(asset.Id, ThumbnailState.Failed, CancellationToken.None).ConfigureAwait(false);
                return null;
            }

            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var temp = path + ".tmp";
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
        catch (Exception ex) when (ex is MediaDecodeException or IOException or UnauthorizedAccessException or InvalidOperationException or ArgumentException)
        {
            _logger.LogInformation(ex, "Thumbnail could not be created for asset {Id}", asset.Id);
            if (size == ThumbnailSize.Small) await _assets.SetThumbnailStateAsync(asset.Id, ThumbnailState.Failed, CancellationToken.None).ConfigureAwait(false);
            return null;
        }
        finally
        {
            _workers.Release();
        }
    }

    /// <summary>
    /// Background pass: generates small thumbnails for pending assets and perceptual hashes for photos.
    /// Returns the number of assets processed. Safe to call repeatedly.
    /// </summary>
    public async Task<int> ProcessPendingAsync(int batchSize, CancellationToken cancellationToken)
    {
        if (_context.Current is null) return 0;
        var processed = 0;
        var pending = await _assets.GetIdsWithThumbnailStateAsync(ThumbnailState.Pending, batchSize, cancellationToken).ConfigureAwait(false);
        foreach (var id in pending)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await GetOrCreateAsync(id, ThumbnailSize.Small, cancellationToken).ConfigureAwait(false);
            processed++;
        }

        var unhashed = await _assets.GetPhotosWithoutPerceptualHashAsync(batchSize, cancellationToken).ConfigureAwait(false);
        foreach (var id in unhashed)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await ComputePerceptualHashAsync(id, cancellationToken).ConfigureAwait(false);
            processed++;
        }

        return processed;
    }

    private async Task ComputePerceptualHashAsync(long assetId, CancellationToken cancellationToken)
    {
        var library = _context.RequireCurrent();
        var file = await _assets.GetOriginalFileAsync(assetId, cancellationToken).ConfigureAwait(false);
        if (file is null) return;
        var path = library.Resolve(file);
        var family = MediaFormats.FromExtension(path)?.Family ?? FormatFamily.Unknown;
        if (!File.Exists(path) || !_renderer.CanDecode(family))
        {
            // Not decodable here: mark it so the background pass does not retry it forever.
            await _assets.SetThumbnailStateAsync(assetId, ThumbnailState.Failed, cancellationToken).ConfigureAwait(false);
            return;
        }

        await _workers.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var raster = await Task.Run(() => _renderer.DecodeForAnalysis(path, family, 64, cancellationToken), cancellationToken).ConfigureAwait(false);
            await _assets.SetPerceptualHashAsync(assetId, _hasher.Compute(raster), cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is MediaDecodeException or IOException or UnauthorizedAccessException)
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
