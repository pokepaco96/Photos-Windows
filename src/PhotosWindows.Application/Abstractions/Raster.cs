using PhotosWindows.Domain;

namespace PhotosWindows.Application.Abstractions;

/// <summary>
/// Decoded pixels in BGRA 8-bit, straight (non-premultiplied) alpha, row-major, stride = Width * 4.
/// EXIF orientation has NOT been applied: <see cref="Orientation"/> carries it.
/// </summary>
public sealed class RasterImage
{
    public RasterImage(int width, int height, byte[] pixels, int orientation = 1)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(width);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(height);
        ArgumentNullException.ThrowIfNull(pixels);
        if (pixels.LongLength != (long)width * height * 4)
        {
            throw new ArgumentException("Pixel buffer size does not match dimensions.", nameof(pixels));
        }

        Width = width;
        Height = height;
        Pixels = pixels;
        Orientation = orientation is >= 1 and <= 8 ? orientation : 1;
    }

    public int Width { get; }
    public int Height { get; }
    public byte[] Pixels { get; }
    public int Orientation { get; }
}

/// <summary>
/// Pluggable still-image decoder. Several decoders can be registered; the first that supports a family wins.
/// The cross-platform implementation lives in PhotosWindows.Imaging; the Windows platform layer adds WIC
/// (HEIF, AVIF, TIFF, camera RAW through installed codecs).
/// </summary>
public interface IRasterDecoder
{
    string Name { get; }

    /// <summary>Lower runs first.</summary>
    int Priority { get; }

    bool CanDecode(FormatFamily family);

    /// <summary>
    /// Decodes the image, optionally downscaled so that its longest side is at least <paramref name="minLongSide"/>
    /// (decoders may use that hint to decode faster). Throws <see cref="MediaDecodeException"/> on failure.
    /// </summary>
    Task<RasterImage> DecodeAsync(string path, FormatFamily family, int? minLongSide, CancellationToken cancellationToken);
}

/// <summary>Extracts a representative still frame from a video file.</summary>
public interface IVideoFrameExtractor
{
    Task<RasterImage?> ExtractPosterFrameAsync(string path, int maxLongSide, CancellationToken cancellationToken);
}

public sealed class MediaDecodeException : Exception
{
    public MediaDecodeException(string message) : base(message) { }
    public MediaDecodeException(string message, Exception inner) : base(message, inner) { }
    public MediaDecodeException() { }
}

/// <summary>Limits applied before decoding to defend against decompression bombs.</summary>
public static class DecodeLimits
{
    /// <summary>Maximum pixels accepted for a still image (~200 MP).</summary>
    public const long MaxPixels = 200L * 1000 * 1000;

    /// <summary>Maximum file size read for a still image (2 GiB).</summary>
    public const long MaxStillFileBytes = 2L * 1024 * 1024 * 1024;
}
