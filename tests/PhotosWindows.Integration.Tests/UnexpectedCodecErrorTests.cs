using Microsoft.Extensions.DependencyInjection;
using PhotosWindows.Application.Abstractions;
using PhotosWindows.Application.Import;
using PhotosWindows.Application.Library;
using PhotosWindows.Application.Thumbnails;
using PhotosWindows.Domain;
using PhotosWindows.Imaging;
using PhotosWindows.Integration.Tests.Support;

namespace PhotosWindows.Integration.Tests;

/// <summary>
/// Regression (Windows 11, full test library): a HEIC screenshot made the codec throw NotImplementedException
/// (0x80004001). It escaped from the thumbnail service, ended the background loop silently and no thumbnail or hash
/// was made again for the rest of the session (5,363 left pending).
/// </summary>
public class UnexpectedCodecErrorTests
{
    private sealed class ThrowingRenderer(IImageRenderer inner, string poisonedFile) : IImageRenderer
    {
        public Task<RenderedImage> RenderAsync(string sourcePath, FormatFamily family, EditRecipe recipe, int? maxLongSide, EncodeOptions encode, CancellationToken cancellationToken) =>
            Path.GetFileName(sourcePath) == poisonedFile
                ? throw new NotImplementedException("The method or operation is not implemented.")
                : inner.RenderAsync(sourcePath, family, recipe, maxLongSide, encode, cancellationToken);

        public Task<RenderedImage> RenderAsync(RasterImage source, EditRecipe recipe, int? maxLongSide, EncodeOptions encode, CancellationToken cancellationToken) =>
            inner.RenderAsync(source, recipe, maxLongSide, encode, cancellationToken);

        public Task<RasterImage> DecodeOrientedAsync(string sourcePath, FormatFamily family, int maxLongSide, CancellationToken cancellationToken) =>
            inner.DecodeOrientedAsync(sourcePath, family, maxLongSide, cancellationToken);

        public bool CanDecode(FormatFamily family) => inner.CanDecode(family);

        public (int Width, int Height)? ProbeSize(string sourcePath, FormatFamily family) => inner.ProbeSize(sourcePath, family);
    }

    [Fact]
    public async Task An_unexpected_codec_exception_fails_only_that_photo_and_the_pass_goes_on()
    {
        await using var host = new TestHost(s => s.AddSingleton<IImageRenderer>(sp =>
            new ThrowingRenderer(ActivatorUtilities.CreateInstance<SkiaImageRenderer>(sp), "IMG_0003.jpg")));
        await host.CreateLibraryAsync();
        var dir = host.SourceDir("lib");
        for (var i = 0; i < 8; i++) Fixtures.Jpeg(Path.Combine(dir, $"IMG_{i:0000}.jpg"), new Fixtures.PhotoSpec { Width = 400, Height = 300, Seed = i });
        await host.Import.ImportAsync(new ImportRequest([dir], ImportMode.AddInPlace), null, CancellationToken.None);
        var thumbs = host.Get<ThumbnailService>();
        var poisoned = (await host.AllAsync()).Single(a => a.FileName == "IMG_0003.jpg");

        // Direct request: no exception, just "no thumbnail".
        Assert.Null(await thumbs.GetOrCreateAsync(poisoned.Id, ThumbnailSize.Small, CancellationToken.None));

        // Background passes run to the end and every other photo gets its thumbnail and hash. (The direct request above
        // counts as grid activity: background work yields to it for 2 s by design.)
        await Task.Delay(TimeSpan.FromSeconds(2.2));
        for (var pass = 0; pass < 5 && await thumbs.ProcessPendingAsync(40, CancellationToken.None) > 0; pass++) { }
        var assets = host.Get<IAssetRepository>();
        Assert.Empty(await assets.GetIdsWithThumbnailStateAsync(ThumbnailState.Pending, 100, CancellationToken.None));
        Assert.Equal([poisoned.Id], await assets.GetIdsWithThumbnailStateAsync(ThumbnailState.Failed, 100, CancellationToken.None));
        Assert.Empty(await assets.GetPhotosWithoutPerceptualHashAsync(100, CancellationToken.None));
    }
}
