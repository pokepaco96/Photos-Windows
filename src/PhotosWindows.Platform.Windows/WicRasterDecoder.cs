using System.Runtime.InteropServices;
using System.Runtime.InteropServices.WindowsRuntime;
using Microsoft.Extensions.Logging;
using PhotosWindows.Application.Abstractions;
using PhotosWindows.Domain;
using Windows.Graphics.Imaging;
using Windows.Storage;
using Windows.Storage.Streams;

namespace PhotosWindows.Platform.Windows;

/// <summary>
/// Decodes images through Windows Imaging Component. Supports whatever codecs are installed: TIFF and BMP are built in;
/// HEIF/HEIC, AVIF and camera RAW require the free "HEIF Image Extensions", "AV1 Video Extension" and
/// "Raw Image Extension" from Microsoft Store. When a codec is missing, a <see cref="MediaDecodeException"/>
/// with an actionable message is thrown.
/// </summary>
public sealed class WicRasterDecoder : IRasterDecoder
{
    private const string OrientationProperty = "System.Photo.Orientation";
    private static readonly FormatFamily[] Families = [FormatFamily.Heif, FormatFamily.Avif, FormatFamily.Tiff, FormatFamily.Raw, FormatFamily.Bmp];
    private readonly ILogger<WicRasterDecoder> _logger;

    public WicRasterDecoder(ILogger<WicRasterDecoder> logger) => _logger = logger;

    public string Name => "Windows Imaging Component";

    public int Priority => 10;

    public bool CanDecode(FormatFamily family) => Families.Contains(family);

    public async Task<RasterImage> DecodeAsync(string path, FormatFamily family, int? minLongSide, CancellationToken cancellationToken)
    {
        try
        {
            // StorageFile rejects paths with mixed separators ("The specified path is invalid"): normalize first.
            var file = await StorageFile.GetFileFromPathAsync(Path.GetFullPath(path)).AsTask(cancellationToken).ConfigureAwait(false);
            using IRandomAccessStream stream = await file.OpenAsync(FileAccessMode.Read).AsTask(cancellationToken).ConfigureAwait(false);
            var decoder = await BitmapDecoder.CreateAsync(stream).AsTask(cancellationToken).ConfigureAwait(false);

            var width = decoder.PixelWidth;
            var height = decoder.PixelHeight;
            if ((long)width * height > DecodeLimits.MaxPixels || width == 0 || height == 0)
            {
                throw new MediaDecodeException("The image dimensions exceed the safety limit.");
            }

            // Small targets (grid tiles, hashes): an embedded HEIF thumbnail avoids decoding the whole HEVC image.
            // Measured on Windows 11 with iPhone HEICs: 20–35 ms instead of 450–1000 ms, and no HEVC decoder instance.
            if (family == FormatFamily.Heif && minLongSide is { } small && small > 0 && small <= EmbeddedThumbnailMaxTarget)
            {
                var embedded = await TryEmbeddedThumbnailAsync(decoder, small, cancellationToken).ConfigureAwait(false);
                if (embedded is not null) return embedded;
            }

            var transform = new BitmapTransform { InterpolationMode = BitmapInterpolationMode.Fant };
            if (minLongSide is { } target && target > 0 && Math.Max(width, height) > target)
            {
                var scale = target / (double)Math.Max(width, height);
                transform.ScaledWidth = (uint)Math.Max(1, Math.Round(width * scale));
                transform.ScaledHeight = (uint)Math.Max(1, Math.Round(height * scale));
            }

            // The decode is never abandoned: with AsTask(token) a cancel completed the task at once while WIC kept
            // decoding, so the caller's worker slot was released and new decodes piled up on top of the old ones
            // (20 GB private memory while browsing quickly through HEICs on Windows 11). The work runs to the end and
            // the cancellation is honoured afterwards; the slot stays taken exactly as long as WIC is working.
            cancellationToken.ThrowIfCancellationRequested();
            // A full HEIC decode costs 1–2 GB of transient memory inside the codec (24 MP iPhone photos, measured on
            // Windows 11), whatever the output size. Viewer, grid and background lanes together allowed ten at once
            // (5.8 GB private while browsing). Process-wide cap; embedded thumbnails do not take part.
            var heavy = family is FormatFamily.Heif or FormatFamily.Avif;
            if (heavy) await HeavyDecodes.WaitAsync(cancellationToken).ConfigureAwait(false);
            PixelDataProvider pixels;
            try
            {
                pixels = await decoder.GetPixelDataAsync(
                    BitmapPixelFormat.Bgra8,
                    BitmapAlphaMode.Straight,
                    transform,
                    ExifOrientationMode.IgnoreExifOrientation,
                    ColorManagementMode.ColorManageToSRgb).AsTask().ConfigureAwait(false);
            }
            finally
            {
                if (heavy) HeavyDecodes.Release();
            }

            cancellationToken.ThrowIfCancellationRequested();
            var bytes = pixels.DetachPixelData();
            var outWidth = transform.ScaledWidth != 0 ? (int)transform.ScaledWidth : (int)width;
            var outHeight = transform.ScaledHeight != 0 ? (int)transform.ScaledHeight : (int)height;
            var orientation = await ReadOrientationAsync(decoder).ConfigureAwait(false);
            return new RasterImage(outWidth, outHeight, bytes, orientation);
        }
        catch (Exception ex) when (ex is COMException or ArgumentException or UnauthorizedAccessException or FileNotFoundException or InvalidCastException)
        {
            _logger.LogDebug(ex, "WIC could not decode {File}", Path.GetFileName(path));
            // MF_E_TOPO_CODEC_NOT_FOUND: the HEIF container is readable but no HEVC decoder is installed (measured on a
            // Windows runner with HEIF Image Extension 1.2.65.0 and no HEVC Video Extensions).
            if (family == FormatFamily.Heif && ex.HResult == unchecked((int)0xC00D5212)) throw new MediaDecodeException(HevcMissingMessage, ex);
            throw new MediaDecodeException(MissingCodecMessage(family), ex);
        }
    }

    /// <summary>Full HEIF/AVIF decodes allowed at once in the process (see DecodeAsync).</summary>
    internal const int MaxHeavyDecodes = 2;

    private static readonly SemaphoreSlim HeavyDecodes = new(MaxHeavyDecodes, MaxHeavyDecodes);

    /// <summary>Largest requested long side for which an embedded thumbnail may be used (grid tiles are 360 px).</summary>
    internal const int EmbeddedThumbnailMaxTarget = 512;

    /// <summary>
    /// An embedded thumbnail is used when its long side is at least this fraction of the target. iPhone HEICs carry
    /// 320–416 px thumbnails, and 252 px ones for some photos (≈5 % of the test library); grid tiles show ~180 logical px.
    /// </summary>
    internal const double EmbeddedThumbnailMinCoverage = 0.66;

    /// <summary>
    /// Returns the image's embedded thumbnail when it is big enough for <paramref name="target"/> and has the same
    /// proportions as the image (so it is not a crop); null otherwise (the caller decodes the full image).
    /// </summary>
    private async Task<RasterImage?> TryEmbeddedThumbnailAsync(BitmapDecoder decoder, int target, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return await TryEmbeddedThumbnailCoreAsync(decoder, target).ConfigureAwait(false);
    }

    private async Task<RasterImage?> TryEmbeddedThumbnailCoreAsync(BitmapDecoder decoder, int target)
    {
        try
        {
            using var stream = await decoder.GetThumbnailAsync().AsTask().ConfigureAwait(false);
            var thumb = await BitmapDecoder.CreateAsync(stream).AsTask().ConfigureAwait(false);
            double tw = thumb.PixelWidth, th = thumb.PixelHeight;
            if (tw <= 0 || th <= 0 || Math.Max(tw, th) < target * EmbeddedThumbnailMinCoverage) return null;
            if (Math.Abs(tw / th - decoder.PixelWidth / (double)decoder.PixelHeight) > 0.02 * (tw / th)) return null;
            var pixels = await thumb.GetPixelDataAsync(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Straight, new BitmapTransform(),
                ExifOrientationMode.IgnoreExifOrientation, ColorManagementMode.DoNotColorManage).AsTask().ConfigureAwait(false);
            var orientation = await ReadOrientationAsync(decoder).ConfigureAwait(false);
            return new RasterImage((int)tw, (int)th, pixels.DetachPixelData(), orientation);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // No usable embedded thumbnail: decode the image. Seen on Windows 11: ArgumentException (none stored),
            // NotImplementedException 0x80004001 (screenshots), COMException 0x88982F44 / 0xC00D3E8C.
            _logger.LogDebug(ex, "No embedded thumbnail");
            return null;
        }
    }

    private static async Task<int> ReadOrientationAsync(BitmapDecoder decoder)
    {
        try
        {
            var props = await decoder.BitmapProperties.GetPropertiesAsync([OrientationProperty]);
            if (props.TryGetValue(OrientationProperty, out var value) && value.Value is ushort o && o is >= 1 and <= 8)
            {
                return o;
            }
        }
        catch (COMException)
        {
            // Some codecs do not expose metadata queries; treat as upright.
        }
        catch (NotSupportedException)
        {
            // Same as above.
        }

        return 1;
    }

    internal const string HevcMissingMessage = "This HEIC photo uses HEVC compression and Windows has no HEVC decoder. Install \"HEVC Video Extensions\" (or \"HEVC Video Extensions from Device Manufacturer\") from Microsoft Store, together with \"HEIF Image Extensions\".";

    private static string MissingCodecMessage(FormatFamily family) => family switch
    {
        FormatFamily.Heif => "This HEIF/HEIC photo cannot be opened. Install \"HEIF Image Extensions\" from Microsoft Store.",
        FormatFamily.Avif => "This AVIF image cannot be opened. Install \"AV1 Video Extension\" from Microsoft Store.",
        FormatFamily.Raw => "This RAW file cannot be opened. Install \"Raw Image Extension\" from Microsoft Store (camera support varies).",
        _ => "Windows could not decode this image. The file may be damaged.",
    };
}
