using PhotosWindows.Application.Abstractions;
using PhotosWindows.Domain;
using SkiaSharp;

namespace PhotosWindows.Imaging.Tests;

public class RendererTests
{
    private readonly SkiaImageRenderer _renderer = new([]);

    private Task<RenderedImage> RenderAsync(string path, FormatFamily family, EditRecipe recipe, int? max, EncodeOptions encode) =>
        _renderer.RenderAsync(path, family, recipe, max, encode, CancellationToken.None);

    private async Task<SKBitmap> RenderToBitmapAsync(string path, EditRecipe recipe, int? max = null, FormatFamily family = FormatFamily.Png)
    {
        var r = await RenderAsync(path, family, recipe, max, new EncodeOptions(OutputFormat.Png));
        return TestImages.DecodeBytes(r.Encoded);
    }

    [Fact]
    public async Task Identity_render_preserves_size_and_pixels()
    {
        using var dir = new TempDir();
        using var src = TestImages.Quadrants(80, 60);
        var path = TestImages.SavePng(src, dir.File("q.png"));
        using var outBmp = await RenderToBitmapAsync(path, EditRecipe.Identity);
        Assert.Equal((80, 60), (outBmp.Width, outBmp.Height));
        Assert.True(TestImages.Near(outBmp.GetPixel(5, 5), SKColors.Red, 2));
        Assert.True(TestImages.Near(outBmp.GetPixel(75, 55), SKColors.White, 2));
    }

    [Fact]
    public async Task Quarter_turn_rotates_clockwise()
    {
        using var dir = new TempDir();
        using var src = TestImages.Quadrants(80, 60);
        var path = TestImages.SavePng(src, dir.File("q.png"));
        using var outBmp = await RenderToBitmapAsync(path, new EditRecipe { QuarterTurns = 1 });
        Assert.Equal((60, 80), (outBmp.Width, outBmp.Height));
        // After a clockwise turn, the former bottom-left (blue) is at the top-left; red at top-right.
        Assert.True(TestImages.Near(outBmp.GetPixel(5, 5), SKColors.Blue));
        Assert.True(TestImages.Near(outBmp.GetPixel(55, 5), SKColors.Red));
        Assert.True(TestImages.Near(outBmp.GetPixel(55, 75), SKColors.Lime));
    }

    [Fact]
    public async Task Horizontal_flip_mirrors()
    {
        using var dir = new TempDir();
        using var src = TestImages.Quadrants(80, 60);
        var path = TestImages.SavePng(src, dir.File("q.png"));
        using var outBmp = await RenderToBitmapAsync(path, new EditRecipe { FlipHorizontal = true });
        Assert.True(TestImages.Near(outBmp.GetPixel(5, 5), SKColors.Lime));
        Assert.True(TestImages.Near(outBmp.GetPixel(75, 5), SKColors.Red));
    }

    [Fact]
    public async Task Crop_selects_the_requested_region_and_size()
    {
        using var dir = new TempDir();
        using var src = TestImages.Quadrants(80, 60);
        var path = TestImages.SavePng(src, dir.File("q.png"));
        using var outBmp = await RenderToBitmapAsync(path, new EditRecipe { Crop = new NormalizedRect(0.5, 0.5, 0.5, 0.5) });
        Assert.Equal((40, 30), (outBmp.Width, outBmp.Height));
        Assert.True(TestImages.Near(outBmp.GetPixel(20, 15), SKColors.White, 2));
    }

    [Fact]
    public async Task Max_long_side_downscales_keeping_aspect()
    {
        using var dir = new TempDir();
        using var src = TestImages.Gradient(400, 200);
        var path = TestImages.SaveJpeg(src, dir.File("g.jpg"));
        var r = await RenderAsync(path, FormatFamily.Jpeg, EditRecipe.Identity, 100, EncodeOptions.ThumbnailJpeg);
        Assert.Equal((100, 50), (r.Width, r.Height));
        Assert.Equal(0xFF, r.Encoded[0]);
        Assert.Equal(0xD8, r.Encoded[1]);
    }

    [Fact]
    public async Task Straighten_fills_the_whole_frame()
    {
        using var dir = new TempDir();
        using var src = TestImages.Solid(200, 100, new SKColor(200, 120, 40));
        var path = TestImages.SavePng(src, dir.File("s.png"));
        using var outBmp = await RenderToBitmapAsync(path, new EditRecipe { StraightenDegrees = 12 });
        Assert.Equal((200, 100), (outBmp.Width, outBmp.Height));
        // Corners must not show empty (black) background thanks to the cover zoom.
        foreach (var (x, y) in new[] { (1, 1), (198, 1), (1, 98), (198, 98) })
        {
            Assert.True(TestImages.Near(outBmp.GetPixel(x, y), new SKColor(200, 120, 40), 30), $"corner {x},{y} = {outBmp.GetPixel(x, y)}");
        }
    }

    [Fact]
    public async Task Exposure_and_black_and_white_change_pixels_in_the_expected_direction()
    {
        using var dir = new TempDir();
        using var src = TestImages.Solid(32, 32, new SKColor(100, 140, 60));
        var path = TestImages.SavePng(src, dir.File("s.png"));
        using var brighter = await RenderToBitmapAsync(path, new EditRecipe { Exposure = 1 });
        using var darker = await RenderToBitmapAsync(path, new EditRecipe { Exposure = -1 });
        using var mono = await RenderToBitmapAsync(path, new EditRecipe { BlackAndWhite = 1 });
        var b = brighter.GetPixel(16, 16);
        var d = darker.GetPixel(16, 16);
        var m = mono.GetPixel(16, 16);
        Assert.True(b.Green > 140 && d.Green < 140);
        Assert.True(Math.Abs(m.Red - m.Green) <= 1 && Math.Abs(m.Green - m.Blue) <= 1);
    }

    [Fact]
    public async Task Saturation_minus_one_produces_grey()
    {
        using var dir = new TempDir();
        using var src = TestImages.Solid(16, 16, new SKColor(220, 40, 40));
        var path = TestImages.SavePng(src, dir.File("s.png"));
        using var outBmp = await RenderToBitmapAsync(path, new EditRecipe { Saturation = -1 });
        var p = outBmp.GetPixel(8, 8);
        Assert.True(Math.Abs(p.Red - p.Blue) <= 2);
    }

    [Fact]
    public async Task Vignette_darkens_corners_more_than_centre()
    {
        using var dir = new TempDir();
        using var src = TestImages.Solid(100, 100, new SKColor(200, 200, 200));
        var path = TestImages.SavePng(src, dir.File("s.png"));
        using var outBmp = await RenderToBitmapAsync(path, new EditRecipe { Vignette = 1 });
        Assert.True(outBmp.GetPixel(1, 1).Red < outBmp.GetPixel(50, 50).Red - 50);
    }

    [Fact]
    public async Task Rendering_is_deterministic()
    {
        using var dir = new TempDir();
        using var src = TestImages.Gradient(64, 48);
        var path = TestImages.SavePng(src, dir.File("g.png"));
        var recipe = new EditRecipe { Exposure = 0.3, Contrast = 0.2, Shadows = 0.4, Highlights = -0.3, Vibrance = 0.5, Temperature = 0.2, Sharpness = 0.5 };
        var a = (await RenderAsync(path, FormatFamily.Png, recipe, null, new EncodeOptions(OutputFormat.Png))).Encoded;
        var b = (await RenderAsync(path, FormatFamily.Png, recipe, null, new EncodeOptions(OutputFormat.Png))).Encoded;
        Assert.Equal(a, b);
    }

    [Fact]
    public async Task Corrupt_file_throws_a_decode_exception()
    {
        using var dir = new TempDir();
        var path = dir.File("bad.jpg");
        File.WriteAllBytes(path, [0xFF, 0xD8, 0xFF, 0xE0, 1, 2, 3, 4, 5]);
        await Assert.ThrowsAsync<MediaDecodeException>(() => RenderAsync(path, FormatFamily.Jpeg, EditRecipe.Identity, 100, EncodeOptions.ThumbnailJpeg));
    }

    [Fact]
    public async Task Truncated_jpeg_still_renders_the_available_part()
    {
        using var dir = new TempDir();
        using var src = TestImages.Gradient(200, 200);
        var full = File.ReadAllBytes(TestImages.SaveJpeg(src, dir.File("full.jpg")));
        var path = dir.File("cut.jpg");
        File.WriteAllBytes(path, full.Take(full.Length * 2 / 3).ToArray());
        var r = await RenderAsync(path, FormatFamily.Jpeg, EditRecipe.Identity, 100, EncodeOptions.ThumbnailJpeg);
        Assert.Equal(100, r.Width);
    }

    [Fact]
    public async Task Unsupported_family_without_decoder_is_reported()
    {
        Assert.False(_renderer.CanDecode(FormatFamily.Heif));
        Assert.True(_renderer.CanDecode(FormatFamily.Jpeg));
        using var dir = new TempDir();
        var path = dir.File("photo.heic");
        File.WriteAllBytes(path, [0, 0, 0, 0x18, (byte)'f', (byte)'t', (byte)'y', (byte)'p', (byte)'h', (byte)'e', (byte)'i', (byte)'c']);
        var ex = await Assert.ThrowsAsync<MediaDecodeException>(() => RenderAsync(path, FormatFamily.Heif, EditRecipe.Identity, 10, EncodeOptions.ThumbnailJpeg));
        Assert.Contains("decoder", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Plugged_raster_decoder_is_used_for_other_families()
    {
        using var dir = new TempDir();
        var path = dir.File("fake.heic");
        File.WriteAllBytes(path, [1, 2, 3]);
        var renderer = new SkiaImageRenderer([new FakeDecoder()]);
        Assert.True(renderer.CanDecode(FormatFamily.Heif));
        var r = await renderer.RenderAsync(path, FormatFamily.Heif, EditRecipe.Identity, null, new EncodeOptions(OutputFormat.Png), CancellationToken.None);
        // Orientation 6 from the decoder is applied: 20x10 raw becomes 10x20.
        Assert.Equal((10, 20), (r.Width, r.Height));
    }

    [Fact]
    public async Task Transparent_png_exported_as_jpeg_is_flattened_on_white()
    {
        using var dir = new TempDir();
        using var bmp = new SKBitmap(new SKImageInfo(10, 10, SKColorType.Bgra8888, SKAlphaType.Premul));
        bmp.Erase(SKColors.Transparent);
        var path = TestImages.SavePng(bmp, dir.File("t.png"));
        var r = await RenderAsync(path, FormatFamily.Png, EditRecipe.Identity, null, new EncodeOptions(OutputFormat.Jpeg, 95));
        using var decoded = TestImages.DecodeBytes(r.Encoded);
        Assert.True(TestImages.Near(decoded.GetPixel(5, 5), SKColors.White, 5));
    }

    private sealed class FakeDecoder : IRasterDecoder
    {
        public string Name => "fake";
        public int Priority => 0;
        public bool CanDecode(FormatFamily family) => family == FormatFamily.Heif;
        public Task<RasterImage> DecodeAsync(string path, FormatFamily family, int? minLongSide, CancellationToken cancellationToken)
        {
            var px = new byte[20 * 10 * 4];
            for (var i = 0; i < px.Length; i += 4) { px[i] = 50; px[i + 1] = 100; px[i + 2] = 150; px[i + 3] = 255; }
            return Task.FromResult(new RasterImage(20, 10, px, orientation: 6));
        }
    }
}
