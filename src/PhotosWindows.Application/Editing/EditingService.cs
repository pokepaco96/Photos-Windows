using PhotosWindows.Application.Abstractions;
using PhotosWindows.Domain;

namespace PhotosWindows.Application.Editing;

/// <summary>Loads, previews and saves non-destructive edit recipes. Originals are only read.</summary>
public sealed class EditingService
{
    private readonly ILibraryContext _context;
    private readonly IAssetRepository _assets;
    private readonly IEditRepository _edits;
    private readonly IImageRenderer _renderer;
    private readonly IClock _clock;

    public EditingService(ILibraryContext context, IAssetRepository assets, IEditRepository edits, IImageRenderer renderer, IClock clock)
    {
        _context = context;
        _assets = assets;
        _edits = edits;
        _renderer = renderer;
        _clock = clock;
    }

    public event EventHandler<long>? RecipeSaved;

    public async Task<EditSession> StartSessionAsync(long assetId, CancellationToken ct)
    {
        var stored = await _edits.GetAsync(assetId, ct).ConfigureAwait(false);
        return new EditSession(assetId, stored?.Recipe ?? EditRecipe.Identity);
    }

    public async Task<EditRecipe> GetRecipeAsync(long assetId, CancellationToken ct) =>
        (await _edits.GetAsync(assetId, ct).ConfigureAwait(false))?.Recipe ?? EditRecipe.Identity;

    public Task<IReadOnlyList<StoredRecipe>> GetHistoryAsync(long assetId, CancellationToken ct) => _edits.GetHistoryAsync(assetId, 50, ct);

    /// <summary>Saves the recipe in the catalog (the original file is never touched). Returns the new edit version (0 = unedited).</summary>
    public async Task<int> SaveAsync(long assetId, EditRecipe recipe, CancellationToken ct)
    {
        var version = await _edits.SaveAsync(assetId, recipe, _clock.UtcNow, ct).ConfigureAwait(false);
        RecipeSaved?.Invoke(this, assetId);
        return version;
    }

    public Task<int> RevertToOriginalAsync(long assetId, CancellationToken ct) => SaveAsync(assetId, EditRecipe.Identity, ct);

    /// <summary>
    /// Decodes the original once at preview resolution with orientation applied, so slider changes only re-run the
    /// fast part of the pipeline. Returns null if the file is unavailable or cannot be decoded.
    /// </summary>
    public async Task<RasterImage?> LoadPreviewSourceAsync(long assetId, int maxLongSide, CancellationToken ct)
    {
        var path = await ResolveOriginalAsync(assetId, ct).ConfigureAwait(false);
        if (path is null) return null;
        var family = MediaFormats.FromExtension(path)?.Family ?? FormatFamily.Unknown;
        return await Task.Run(() => _renderer.DecodeForAnalysis(path, family, maxLongSide, ct), ct).ConfigureAwait(false);
    }

    public Task<RenderedImage> RenderPreviewAsync(RasterImage source, EditRecipe recipe, int maxLongSide, CancellationToken ct) =>
        Task.Run(() => _renderer.Render(source, recipe, maxLongSide, EncodeOptions.PreviewJpeg, ct), ct);

    /// <summary>Full-quality render from the original (viewer at large sizes and export).</summary>
    public async Task<RenderedImage?> RenderFromOriginalAsync(long assetId, EditRecipe recipe, int? maxLongSide, EncodeOptions encode, CancellationToken ct)
    {
        var path = await ResolveOriginalAsync(assetId, ct).ConfigureAwait(false);
        if (path is null) return null;
        var family = MediaFormats.FromExtension(path)?.Family ?? FormatFamily.Unknown;
        return await Task.Run(() => _renderer.Render(path, family, recipe, maxLongSide, encode, ct), ct).ConfigureAwait(false);
    }

    public async Task<string?> ResolveOriginalAsync(long assetId, CancellationToken ct)
    {
        var file = await _assets.GetOriginalFileAsync(assetId, ct).ConfigureAwait(false);
        if (file is null) return null;
        var path = _context.RequireCurrent().Resolve(file);
        return File.Exists(path) ? path : null;
    }
}
