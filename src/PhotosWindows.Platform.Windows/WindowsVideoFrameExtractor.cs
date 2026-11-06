using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;
using PhotosWindows.Application.Abstractions;
using Windows.Graphics.Imaging;
using Windows.Storage;
using Windows.Storage.FileProperties;

namespace PhotosWindows.Platform.Windows;

/// <summary>
/// Extracts a still frame from a video using the Windows thumbnail pipeline (Media Foundation decoders).
/// Formats without an installed decoder (e.g. HEVC without the extension) return null and the grid shows a placeholder.
/// </summary>
public sealed class WindowsVideoFrameExtractor : IVideoFrameExtractor
{
    private readonly ILogger<WindowsVideoFrameExtractor> _logger;

    public WindowsVideoFrameExtractor(ILogger<WindowsVideoFrameExtractor> logger) => _logger = logger;

    public async Task<RasterImage?> ExtractPosterFrameAsync(string path, int maxLongSide, CancellationToken cancellationToken)
    {
        try
        {
            var file = await StorageFile.GetFileFromPathAsync(path).AsTask(cancellationToken).ConfigureAwait(false);
            using var thumbnail = await file.GetThumbnailAsync(ThumbnailMode.SingleItem, (uint)Math.Clamp(maxLongSide, 64, 2048), ThumbnailOptions.ResizeThumbnail)
                .AsTask(cancellationToken).ConfigureAwait(false);
            if (thumbnail is null || thumbnail.Type != ThumbnailType.Image)
            {
                return null; // Windows returned a generic file icon: no decoder for this video.
            }

            var decoder = await BitmapDecoder.CreateAsync(thumbnail).AsTask(cancellationToken).ConfigureAwait(false);
            var pixels = await decoder.GetPixelDataAsync(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Straight, new BitmapTransform(),
                ExifOrientationMode.RespectExifOrientation, ColorManagementMode.ColorManageToSRgb).AsTask(cancellationToken).ConfigureAwait(false);
            return new RasterImage((int)decoder.OrientedPixelWidth, (int)decoder.OrientedPixelHeight, pixels.DetachPixelData());
        }
        catch (Exception ex) when (ex is COMException or ArgumentException or UnauthorizedAccessException or FileNotFoundException)
        {
            _logger.LogDebug(ex, "No poster frame for {File}", Path.GetFileName(path));
            return null;
        }
    }
}
