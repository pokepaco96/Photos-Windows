using Microsoft.Extensions.Logging;
using PhotosWindows.Application.Abstractions;
using PhotosWindows.Application.Common;
using PhotosWindows.Domain;

namespace PhotosWindows.Application.Export;

public enum ExportKind
{
    /// <summary>Byte-for-byte copy of the original (plus RAW/motion companions when requested).</summary>
    Original = 0,

    /// <summary>Rendered with the current edits.</summary>
    Edited = 1,
}

public sealed record ExportOptions
{
    public ExportKind Kind { get; init; } = ExportKind.Edited;
    public OutputFormat Format { get; init; } = OutputFormat.Jpeg;
    public int Quality { get; init; } = 92;
    /// <summary>Null keeps the full resolution.</summary>
    public int? MaxLongSide { get; init; }
    /// <summary>Write capture date and camera data (JPEG only).</summary>
    public bool IncludeMetadata { get; init; } = true;
    /// <summary>Write GPS coordinates (JPEG only). Off by default for privacy.</summary>
    public bool IncludeLocation { get; init; }
    /// <summary>Write title, description, keywords and rating as XMP (JPEG only).</summary>
    public bool IncludeDescriptive { get; init; } = true;
    public bool IncludeCompanions { get; init; } = true;
}

public sealed record ExportProgress(int Total, int Done, string? Current);

public sealed record ExportItemResult(long AssetId, IReadOnlyList<string> OutputPaths, string? Error);

/// <summary>Exports originals or edited renders. Never overwrites an existing file and never modifies originals.</summary>
public sealed class ExportService
{
    private readonly ILibraryContext _context;
    private readonly IAssetRepository _assets;
    private readonly IKeywordRepository _keywords;
    private readonly IEditRepository _edits;
    private readonly IImageRenderer _renderer;
    private readonly ILogger<ExportService> _logger;

    public ExportService(ILibraryContext context, IAssetRepository assets, IKeywordRepository keywords, IEditRepository edits, IImageRenderer renderer, ILogger<ExportService> logger)
    {
        _context = context;
        _assets = assets;
        _keywords = keywords;
        _edits = edits;
        _renderer = renderer;
        _logger = logger;
    }

    public async Task<IReadOnlyList<ExportItemResult>> ExportAsync(IReadOnlyList<long> assetIds, string destinationFolder, ExportOptions options, IProgress<ExportProgress>? progress, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(options);
        var library = _context.RequireCurrent();
        var destination = Path.GetFullPath(destinationFolder);
        if (PathSafety.IsUnder(destination, library.RootPath))
        {
            throw new ArgumentException("Choose a folder outside the library to export to.", nameof(destinationFolder));
        }

        Directory.CreateDirectory(destination);
        var results = new List<ExportItemResult>();
        for (var i = 0; i < assetIds.Count; i++)
        {
            ct.ThrowIfCancellationRequested();
            var id = assetIds[i];
            progress?.Report(new ExportProgress(assetIds.Count, i, null));
            try
            {
                var asset = await _assets.GetAsync(id, ct).ConfigureAwait(false) ?? throw new InvalidOperationException("The item no longer exists.");
                progress?.Report(new ExportProgress(assetIds.Count, i, asset.FileName));
                var paths = options.Kind == ExportKind.Original || asset.MediaType == MediaType.Video
                    ? await ExportOriginalAsync(library, asset, destination, options.IncludeCompanions, ct).ConfigureAwait(false)
                    : [await ExportEditedAsync(library, asset, destination, options, ct).ConfigureAwait(false)];
                results.Add(new ExportItemResult(id, paths, null));
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or MediaDecodeException or InvalidOperationException or ArgumentException)
            {
                _logger.LogWarning(ex, "Export failed for asset {Id}", id);
                results.Add(new ExportItemResult(id, [], ex.Message));
            }
        }

        progress?.Report(new ExportProgress(assetIds.Count, assetIds.Count, null));
        return results;
    }

    private async Task<IReadOnlyList<string>> ExportOriginalAsync(LibraryInfo library, Asset asset, string destination, bool companions, CancellationToken ct)
    {
        var files = await _assets.GetFilesAsync(asset.Id, ct).ConfigureAwait(false);
        var selected = files.Where(f => f.Role == FileRole.Original || (companions && f.Role != FileRole.Original)).ToList();
        var outputs = new List<string>();
        foreach (var file in selected)
        {
            var source = library.Resolve(file);
            if (!File.Exists(source))
            {
                if (file.Role == FileRole.Original) throw new IOException("The original file is not available (moved, deleted or on a disconnected drive).");
                continue;
            }

            var target = PathSafety.UniquePath(destination, Path.GetFileName(source));
            var partial = target + PathSafety.PartialSuffix;
            await FileHashing.CopyWithHashAsync(source, partial, ct).ConfigureAwait(false);
            File.Move(partial, target, overwrite: false);
            outputs.Add(target);
        }

        return outputs;
    }

    private async Task<string> ExportEditedAsync(LibraryInfo library, Asset asset, string destination, ExportOptions options, CancellationToken ct)
    {
        var file = await _assets.GetOriginalFileAsync(asset.Id, ct).ConfigureAwait(false) ?? throw new InvalidOperationException("The item has no original file.");
        var source = library.Resolve(file);
        if (!File.Exists(source)) throw new IOException("The original file is not available (moved, deleted or on a disconnected drive).");
        var recipe = (await _edits.GetAsync(asset.Id, ct).ConfigureAwait(false))?.Recipe ?? EditRecipe.Identity;
        var family = MediaFormats.FromExtension(source)?.Family ?? FormatFamily.Unknown;
        var encode = new EncodeOptions(options.Format, options.Quality);
        var rendered = await Task.Run(() => _renderer.Render(source, family, recipe, options.MaxLongSide, encode, ct), ct).ConfigureAwait(false);

        var bytes = rendered.Encoded;
        if (options.Format == OutputFormat.Jpeg)
        {
            if (options.IncludeMetadata || options.IncludeDescriptive)
            {
                var keywords = options.IncludeDescriptive ? await _keywords.GetForAssetAsync(asset.Id, ct).ConfigureAwait(false) : [];
                var metadata = new ExportMetadata(
                    options.IncludeMetadata ? asset.CaptureDate : null,
                    options.IncludeMetadata ? asset.Camera : CameraInfo.Empty,
                    options.IncludeLocation ? asset.Location : null,
                    rendered.Width,
                    rendered.Height,
                    options.IncludeDescriptive ? asset.Title : null,
                    options.IncludeDescriptive ? asset.Description : null,
                    keywords,
                    options.IncludeDescriptive ? asset.Rating : 0);
                bytes = JpegMetadataWriter.Inject(bytes, metadata, options.IncludeLocation, options.IncludeDescriptive);
            }
            else
            {
                bytes = JpegMetadataWriter.StripMetadata(bytes);
            }
        }

        var name = Path.GetFileNameWithoutExtension(asset.FileName) + encode.FileExtension;
        var target = PathSafety.UniquePath(destination, name);
        var partial = target + PathSafety.PartialSuffix;
        await File.WriteAllBytesAsync(partial, bytes, ct).ConfigureAwait(false);
        File.Move(partial, target, overwrite: false);
        return target;
    }
}
