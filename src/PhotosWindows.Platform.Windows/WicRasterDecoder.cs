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
            var file = await StorageFile.GetFileFromPathAsync(path).AsTask(cancellationToken).ConfigureAwait(false);
            using IRandomAccessStream stream = await file.OpenAsync(FileAccessMode.Read).AsTask(cancellationToken).ConfigureAwait(false);
            var decoder = await BitmapDecoder.CreateAsync(stream).AsTask(cancellationToken).ConfigureAwait(false);

            var width = decoder.PixelWidth;
            var height = decoder.PixelHeight;
            if ((long)width * height > DecodeLimits.MaxPixels || width == 0 || height == 0)
            {
                throw new MediaDecodeException("The image dimensions exceed the safety limit.");
            }

            var transform = new BitmapTransform { InterpolationMode = BitmapInterpolationMode.Fant };
            if (minLongSide is { } target && target > 0 && Math.Max(width, height) > target)
            {
                var scale = target / (double)Math.Max(width, height);
                transform.ScaledWidth = (uint)Math.Max(1, Math.Round(width * scale));
                transform.ScaledHeight = (uint)Math.Max(1, Math.Round(height * scale));
            }

            var pixels = await decoder.GetPixelDataAsync(
                BitmapPixelFormat.Bgra8,
                BitmapAlphaMode.Straight,
                transform,
                ExifOrientationMode.IgnoreExifOrientation,
                ColorManagementMode.ColorManageToSRgb).AsTask(cancellationToken).ConfigureAwait(false);
            var bytes = pixels.DetachPixelData();
            var outWidth = transform.ScaledWidth != 0 ? (int)transform.ScaledWidth : (int)width;
            var outHeight = transform.ScaledHeight != 0 ? (int)transform.ScaledHeight : (int)height;
            var orientation = await ReadOrientationAsync(decoder).ConfigureAwait(false);
            return new RasterImage(outWidth, outHeight, bytes, orientation);
        }
        catch (Exception ex) when (ex is COMException or ArgumentException or UnauthorizedAccessException or FileNotFoundException or InvalidCastException)
        {
            _logger.LogDebug(ex, "WIC could not decode {File}", Path.GetFileName(path));
            throw new MediaDecodeException(MissingCodecMessage(family), ex);
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

    private static string MissingCodecMessage(FormatFamily family) => family switch
    {
        FormatFamily.Heif => "This HEIF/HEIC photo cannot be opened. Install \"HEIF Image Extensions\" from Microsoft Store.",
        FormatFamily.Avif => "This AVIF image cannot be opened. Install \"AV1 Video Extension\" from Microsoft Store.",
        FormatFamily.Raw => "This RAW file cannot be opened. Install \"Raw Image Extension\" from Microsoft Store (camera support varies).",
        _ => "Windows could not decode this image. The file may be damaged.",
    };
}
