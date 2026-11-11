using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using PhotosWindows.Application.Abstractions;
using PhotosWindows.Domain;
using SkiaSharp;

namespace PhotosWindows.Imaging;

/// <summary>
/// Rendering pipeline built on SkiaSharp (MIT). Formats Skia decodes natively are handled here
/// (JPEG, PNG, GIF first frame, BMP, WebP); other families are delegated to registered
/// <see cref="IRasterDecoder"/>s (e.g. Windows Imaging Component for HEIF/AVIF/TIFF/RAW).
/// </summary>
public sealed class SkiaImageRenderer : IImageRenderer
{
    private static readonly FormatFamily[] SkiaFamilies = [FormatFamily.Jpeg, FormatFamily.Png, FormatFamily.Gif, FormatFamily.Bmp, FormatFamily.WebP];
    private readonly IReadOnlyList<IRasterDecoder> _decoders;
    private readonly ILogger<SkiaImageRenderer> _logger;

    public SkiaImageRenderer(IEnumerable<IRasterDecoder> decoders, ILogger<SkiaImageRenderer>? logger = null)
    {
        _decoders = decoders.OrderBy(d => d.Priority).ToList();
        _logger = logger ?? NullLogger<SkiaImageRenderer>.Instance;
    }

    public bool CanDecode(FormatFamily family) => SkiaFamilies.Contains(family) || _decoders.Any(d => d.CanDecode(family));

    public async Task<RenderedImage> RenderAsync(string sourcePath, FormatFamily family, EditRecipe recipe, int? maxLongSide, EncodeOptions encode, CancellationToken cancellationToken)
    {
        var needed = RequiredDecodeSize(recipe, maxLongSide);
        using var decoded = await DecodeAsync(sourcePath, family, needed, cancellationToken).ConfigureAwait(false);
        return await Task.Run(() => RenderDecoded(decoded.Bitmap, decoded.Orientation, recipe, maxLongSide, encode, cancellationToken), cancellationToken).ConfigureAwait(false);
    }

    public Task<RenderedImage> RenderAsync(RasterImage source, EditRecipe recipe, int? maxLongSide, EncodeOptions encode, CancellationToken cancellationToken) =>
        Task.Run(() =>
        {
            using var bitmap = ToBitmap(source);
            return RenderDecoded(bitmap, source.Orientation, recipe, maxLongSide, encode, cancellationToken);
        }, cancellationToken);

    public async Task<RasterImage> DecodeOrientedAsync(string sourcePath, FormatFamily family, int maxLongSide, CancellationToken cancellationToken)
    {
        using var decoded = await DecodeAsync(sourcePath, family, maxLongSide, cancellationToken).ConfigureAwait(false);
        return await Task.Run(() =>
        {
            using var output = Transform(decoded.Bitmap, decoded.Orientation, EditRecipe.Identity, maxLongSide);
            return ToRaster(output);
        }, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Reads only the header to obtain stored dimensions; null when the format is not handled by Skia.</summary>
    public (int Width, int Height)? ProbeSize(string sourcePath, FormatFamily family)
    {
        if (!SkiaFamilies.Contains(family)) return null;
        using var stream = File.OpenRead(sourcePath);
        using var codec = SKCodec.Create(stream);
        return codec is null ? null : (codec.Info.Width, codec.Info.Height);
    }

    private RenderedImage RenderDecoded(SKBitmap source, int orientation, EditRecipe recipe, int? maxLongSide, EncodeOptions encode, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var normalized = recipe.Normalize();
        using var output = Transform(source, orientation, normalized, maxLongSide);
        cancellationToken.ThrowIfCancellationRequested();

        if (!normalized.IsIdentity)
        {
            var span = GetPixelSpan(output);
            ToneProcessor.Apply(span, output.Width, output.Height, normalized);
        }

        cancellationToken.ThrowIfCancellationRequested();
        return Encode(output, encode);
    }

    /// <summary>
    /// Applies EXIF orientation, quarter turns, flips, straighten (with cover zoom) and crop in a single
    /// resampling pass, scaling so the longest output side is at most <paramref name="maxLongSide"/>.
    /// </summary>
    internal static SKBitmap Transform(SKBitmap source, int exifOrientation, EditRecipe recipe, int? maxLongSide)
    {
        var (exif, uw, uh) = Orientation.FromExif(exifOrientation, source.Width, source.Height);
        var (user, ow, oh) = Orientation.FromRecipe(recipe.QuarterTurns, recipe.FlipHorizontal, recipe.FlipVertical, uw, uh);
        var toOriented = user.After(exif);

        // Straighten around the centre and zoom so no empty corners appear.
        var cover = Orientation.StraightenCoverScale(recipe.StraightenDegrees, ow, oh);
        var straighten = Affine.Translate(ow / 2.0, oh / 2.0)
            .After(Affine.Scale(cover))
            .After(Affine.RotateDegrees(recipe.StraightenDegrees, 0, 0))
            .After(Affine.Translate(-ow / 2.0, -oh / 2.0));

        var crop = recipe.Crop.Clamp();
        var cropX = crop.X * ow;
        var cropY = crop.Y * oh;
        var cropW = Math.Max(1, crop.Width * ow);
        var cropH = Math.Max(1, crop.Height * oh);

        var scale = maxLongSide is { } max && max > 0 ? Math.Min(1.0, max / Math.Max(cropW, cropH)) : 1.0;
        var outW = Math.Max(1, (int)Math.Round(cropW * scale));
        var outH = Math.Max(1, (int)Math.Round(cropH * scale));

        var total = Affine.Scale(scale)
            .After(Affine.Translate(-cropX, -cropY))
            .After(straighten)
            .After(toOriented);

        var isOpaque = source.AlphaType == SKAlphaType.Opaque;
        var info = new SKImageInfo(outW, outH, SKColorType.Bgra8888, isOpaque ? SKAlphaType.Opaque : SKAlphaType.Premul);
        var output = new SKBitmap(info);
        using var canvas = new SKCanvas(output);
        canvas.Clear(isOpaque ? SKColors.Black : SKColors.Transparent);
        canvas.SetMatrix(new SKMatrix((float)total.M11, (float)total.M12, (float)total.Dx, (float)total.M21, (float)total.M22, (float)total.Dy, 0, 0, 1));

        var effectiveScale = scale * cover;
        var sampling = effectiveScale < 0.5
            ? new SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.Linear)
            : new SKSamplingOptions(SKCubicResampler.Mitchell);
        using var image = SKImage.FromBitmap(source);
        using var paint = new SKPaint { IsAntialias = true };
        canvas.DrawImage(image, new SKRect(0, 0, source.Width, source.Height), new SKRect(0, 0, source.Width, source.Height), sampling, paint);
        canvas.Flush();
        return output;
    }

    /// <summary>
    /// Smallest long side the decoder must deliver so the final output keeps full quality,
    /// taking the crop into account (a tight crop needs more source pixels).
    /// </summary>
    internal static int? RequiredDecodeSize(EditRecipe recipe, int? maxLongSide)
    {
        if (maxLongSide is not { } max) return null;
        var crop = recipe.Crop.Clamp();
        var fraction = Math.Min(crop.Width, crop.Height);
        var cover = Orientation.StraightenCoverScale(recipe.StraightenDegrees, 1, 1);
        return (int)Math.Ceiling(max / Math.Max(0.01, fraction) * cover);
    }

    private sealed class DecodedImage(SKBitmap bitmap, int orientation) : IDisposable
    {
        public SKBitmap Bitmap { get; } = bitmap;
        public int Orientation { get; } = orientation;
        public void Dispose() => Bitmap.Dispose();
    }

    private async Task<DecodedImage> DecodeAsync(string path, FormatFamily family, int? minLongSide, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var length = new FileInfo(path).Length;
        if (length > DecodeLimits.MaxStillFileBytes)
        {
            throw new MediaDecodeException("The file is too large to decode safely.");
        }

        if (SkiaFamilies.Contains(family))
        {
            return await Task.Run(() => DecodeWithSkia(path, minLongSide), cancellationToken).ConfigureAwait(false);
        }

        foreach (var decoder in _decoders.Where(d => d.CanDecode(family)))
        {
            try
            {
                var raster = await decoder.DecodeAsync(path, family, minLongSide, cancellationToken).ConfigureAwait(false);
                return new DecodedImage(ToBitmap(raster), raster.Orientation);
            }
            catch (MediaDecodeException ex)
            {
                _logger.LogDebug(ex, "Decoder {Decoder} could not decode {File}", decoder.Name, Path.GetFileName(path));
            }
        }

        throw new MediaDecodeException($"No installed decoder can open this {family} file.");
    }

    /// <summary>
    /// Smallest size the codec can decode to directly (JPEG: 1/8, 2/8 … 8/8) whose long side still covers
    /// <paramref name="target"/>. Asking for the exact ratio rounds down to a step that may be too small; the old code
    /// then fell back to a full-size decode, so a 360 px grid thumbnail of a 12 MP photo decoded all 12 MP.
    /// </summary>
    internal static SKSizeI ScaledDecodeSize(SKCodec codec, SKSizeI full, int target)
    {
        var longSide = Math.Max(full.Width, full.Height);
        if (target >= longSide) return full;
        for (var eighths = 1; eighths < 8; eighths++)
        {
            var factor = eighths / 8f;
            if (factor * longSide < target) continue;
            var size = codec.GetScaledDimensions(factor);
            if (Math.Max(size.Width, size.Height) >= target) return size;
        }

        return full;
    }

    private static DecodedImage DecodeWithSkia(string path, int? minLongSide)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        using var codec = SKCodec.Create(stream, out var result) ?? throw new MediaDecodeException($"The image could not be read ({result}).");
        var info = codec.Info;
        if ((long)info.Width * info.Height > DecodeLimits.MaxPixels || info.Width <= 0 || info.Height <= 0)
        {
            throw new MediaDecodeException("The image dimensions exceed the safety limit.");
        }

        var size = info.Size;
        if (minLongSide is { } target && target > 0)
        {
            size = ScaledDecodeSize(codec, info.Size, target);
        }

        var alpha = info.AlphaType == SKAlphaType.Opaque ? SKAlphaType.Opaque : SKAlphaType.Premul;
        var decodeInfo = new SKImageInfo(size.Width, size.Height, SKColorType.Bgra8888, alpha);
        var bitmap = new SKBitmap(decodeInfo);
        var status = codec.GetPixels(decodeInfo, bitmap.GetPixels());
        if (status is not (SKCodecResult.Success or SKCodecResult.IncompleteInput) && size != info.Size)
        {
            // Retry at full size if scaled decoding is not supported for this codec.
            bitmap.Dispose();
            decodeInfo = new SKImageInfo(info.Width, info.Height, SKColorType.Bgra8888, alpha);
            bitmap = new SKBitmap(decodeInfo);
            status = codec.GetPixels(decodeInfo, bitmap.GetPixels());
        }

        if (status is not (SKCodecResult.Success or SKCodecResult.IncompleteInput))
        {
            bitmap.Dispose();
            throw new MediaDecodeException($"The image could not be decoded ({status}).");
        }

        // Truncated files still render the part that was read; that is preferable to failing.
        return new DecodedImage(bitmap, OriginToExif(codec.EncodedOrigin));
    }

    private static int OriginToExif(SKEncodedOrigin origin) => origin switch
    {
        SKEncodedOrigin.TopRight => 2,
        SKEncodedOrigin.BottomRight => 3,
        SKEncodedOrigin.BottomLeft => 4,
        SKEncodedOrigin.LeftTop => 5,
        SKEncodedOrigin.RightTop => 6,
        SKEncodedOrigin.RightBottom => 7,
        SKEncodedOrigin.LeftBottom => 8,
        _ => 1,
    };

    internal static SKBitmap ToBitmap(RasterImage raster)
    {
        if ((long)raster.Width * raster.Height > DecodeLimits.MaxPixels)
        {
            throw new MediaDecodeException("The image dimensions exceed the safety limit.");
        }

        var hasAlpha = false;
        var pixels = raster.Pixels;
        for (var i = 3; i < pixels.Length; i += 4)
        {
            if (pixels[i] != 255) { hasAlpha = true; break; }
        }

        var bitmap = new SKBitmap(new SKImageInfo(raster.Width, raster.Height, SKColorType.Bgra8888, hasAlpha ? SKAlphaType.Premul : SKAlphaType.Opaque));
        var dest = GetPixelSpan(bitmap);
        if (!hasAlpha)
        {
            pixels.CopyTo(dest);
        }
        else
        {
            for (var i = 0; i < pixels.Length; i += 4)
            {
                var a = pixels[i + 3];
                dest[i] = (byte)(pixels[i] * a / 255);
                dest[i + 1] = (byte)(pixels[i + 1] * a / 255);
                dest[i + 2] = (byte)(pixels[i + 2] * a / 255);
                dest[i + 3] = a;
            }
        }

        return bitmap;
    }

    internal static RasterImage ToRaster(SKBitmap bitmap)
    {
        using var straight = new SKBitmap(new SKImageInfo(bitmap.Width, bitmap.Height, SKColorType.Bgra8888, SKAlphaType.Unpremul));
        if (!bitmap.CopyTo(straight, SKColorType.Bgra8888))
        {
            using var canvas = new SKCanvas(straight);
            canvas.DrawBitmap(bitmap, 0, 0, SKSamplingOptions.Default, null);
        }

        return new RasterImage(bitmap.Width, bitmap.Height, GetPixelSpan(straight).ToArray());
    }

    internal static unsafe Span<byte> GetPixelSpan(SKBitmap bitmap) =>
        new((void*)bitmap.GetPixels(), bitmap.RowBytes * bitmap.Height);

    private static RenderedImage Encode(SKBitmap bitmap, EncodeOptions options)
    {
        var format = options.Format switch
        {
            OutputFormat.Png => SKEncodedImageFormat.Png,
            OutputFormat.WebP => SKEncodedImageFormat.Webp,
            _ => SKEncodedImageFormat.Jpeg,
        };

        SKBitmap toEncode = bitmap;
        SKBitmap? flattened = null;
        if (format == SKEncodedImageFormat.Jpeg && bitmap.AlphaType != SKAlphaType.Opaque)
        {
            // JPEG has no alpha: composite on white instead of letting transparent areas turn black.
            flattened = new SKBitmap(new SKImageInfo(bitmap.Width, bitmap.Height, SKColorType.Bgra8888, SKAlphaType.Opaque));
            using var canvas = new SKCanvas(flattened);
            canvas.Clear(SKColors.White);
            canvas.DrawBitmap(bitmap, 0, 0, SKSamplingOptions.Default, null);
            toEncode = flattened;
        }

        try
        {
            using var image = SKImage.FromBitmap(toEncode);
            using var data = image.Encode(format, Math.Clamp(options.Quality, 1, 100))
                ?? throw new MediaDecodeException($"Encoding to {options.Format} failed.");
            return new RenderedImage(data.ToArray(), bitmap.Width, bitmap.Height, options.Format);
        }
        finally
        {
            flattened?.Dispose();
        }
    }
}
