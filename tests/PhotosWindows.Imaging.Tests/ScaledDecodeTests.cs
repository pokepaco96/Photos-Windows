using SkiaSharp;

namespace PhotosWindows.Imaging.Tests;

/// <summary>Grid thumbnails must use the JPEG decoder's built-in downscaling instead of decoding every pixel.</summary>
public class ScaledDecodeTests
{
    private static SKCodec JpegCodec(int width, int height)
    {
        using var bmp = new SKBitmap(width, height);
        bmp.Erase(SKColors.CornflowerBlue);
        using var image = SKImage.FromBitmap(bmp);
        var data = image.Encode(SKEncodedImageFormat.Jpeg, 85);
        return SKCodec.Create(data)!;
    }

    [Theory]
    [InlineData(4032, 3024, 360, 1008)] // 12 MP phone photo: 1/4 (1008 px) covers 360; 1/8 (504) also does
    [InlineData(2560, 1920, 360, 640)]  // 1/8 = 320 is too small; 2/8 = 640 is the smallest that covers 360
    [InlineData(6000, 4000, 2048, 2250)]
    public void Picks_the_smallest_codec_scale_that_covers_the_target(int w, int h, int target, int maxExpectedLongSide)
    {
        using var codec = JpegCodec(w, h);
        var size = SkiaImageRenderer.ScaledDecodeSize(codec, new SKSizeI(w, h), target);
        var longSide = Math.Max(size.Width, size.Height);
        Assert.InRange(longSide, target, maxExpectedLongSide);
        Assert.True(longSide < Math.Max(w, h), "must not fall back to a full-size decode");
    }

    [Fact]
    public void Small_images_are_decoded_at_full_size()
    {
        using var codec = JpegCodec(300, 200);
        Assert.Equal(new SKSizeI(300, 200), SkiaImageRenderer.ScaledDecodeSize(codec, new SKSizeI(300, 200), 360));
    }
}
