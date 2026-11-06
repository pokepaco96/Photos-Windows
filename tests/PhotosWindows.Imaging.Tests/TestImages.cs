using SkiaSharp;

namespace PhotosWindows.Imaging.Tests;

/// <summary>Synthetic, license-free test images generated at runtime (no personal photos in the repository).</summary>
internal static class TestImages
{
    /// <summary>Four coloured quadrants: TL red, TR green, BL blue, BR white. Makes orientation errors obvious.</summary>
    public static SKBitmap Quadrants(int w, int h)
    {
        var bmp = new SKBitmap(new SKImageInfo(w, h, SKColorType.Bgra8888, SKAlphaType.Opaque));
        using var c = new SKCanvas(bmp);
        using var p = new SKPaint();
        p.Color = SKColors.Red; c.DrawRect(0, 0, w / 2f, h / 2f, p);
        p.Color = SKColors.Lime; c.DrawRect(w / 2f, 0, w / 2f, h / 2f, p);
        p.Color = SKColors.Blue; c.DrawRect(0, h / 2f, w / 2f, h / 2f, p);
        p.Color = SKColors.White; c.DrawRect(w / 2f, h / 2f, w / 2f, h / 2f, p);
        return bmp;
    }

    public static SKBitmap Gradient(int w, int h, int seed = 0)
    {
        var bmp = new SKBitmap(new SKImageInfo(w, h, SKColorType.Bgra8888, SKAlphaType.Opaque));
        for (var y = 0; y < h; y++)
        {
            for (var x = 0; x < w; x++)
            {
                var v = (byte)((x * 255 / Math.Max(1, w - 1) + seed * 37 + (y * 3)) % 256);
                bmp.SetPixel(x, y, new SKColor(v, (byte)(255 - v), (byte)((y * 255) / Math.Max(1, h - 1))));
            }
        }

        return bmp;
    }

    public static SKBitmap Solid(int w, int h, SKColor color)
    {
        var bmp = new SKBitmap(new SKImageInfo(w, h, SKColorType.Bgra8888, SKAlphaType.Opaque));
        bmp.Erase(color);
        return bmp;
    }

    public static string SaveJpeg(SKBitmap bmp, string path, int quality = 95)
    {
        using var image = SKImage.FromBitmap(bmp);
        using var data = image.Encode(SKEncodedImageFormat.Jpeg, quality);
        File.WriteAllBytes(path, data.ToArray());
        return path;
    }

    public static string SavePng(SKBitmap bmp, string path)
    {
        using var image = SKImage.FromBitmap(bmp);
        using var data = image.Encode(SKEncodedImageFormat.Png, 100);
        File.WriteAllBytes(path, data.ToArray());
        return path;
    }

    public static SKBitmap DecodeBytes(byte[] data) => SKBitmap.Decode(data);

    public static bool Near(SKColor c, SKColor expected, int tolerance = 40) =>
        Math.Abs(c.Red - expected.Red) <= tolerance && Math.Abs(c.Green - expected.Green) <= tolerance && Math.Abs(c.Blue - expected.Blue) <= tolerance;
}

internal sealed class TempDir : IDisposable
{
    public TempDir() => Directory.CreateDirectory(Path);

    public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "pw-img-" + Guid.NewGuid().ToString("N"));

    public string File(string name) => System.IO.Path.Combine(Path, name);

    public void Dispose()
    {
        if (Directory.Exists(Path)) Directory.Delete(Path, recursive: true);
    }
}
