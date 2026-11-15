using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;
using PhotosWindows.Application.Abstractions;
using Windows.Graphics.Imaging;
using Windows.Media.Editing;
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
        StorageFile file;
        try
        {
            file = await StorageFile.GetFileFromPathAsync(path).AsTask(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is COMException or ArgumentException or UnauthorizedAccessException or FileNotFoundException)
        {
            _logger.LogDebug(ex, "Cannot open {File} for a poster frame", Path.GetFileName(path));
            return null;
        }

        var shell = await FromShellAsync(file, maxLongSide, cancellationToken).ConfigureAwait(false);
        if (shell is not null) return shell;

        // One Media Foundation extraction at a time in the process. Measured on Windows 11 with iPhone videos (the shell
        // only returns an icon for them there): 300–360 MiB private per extraction and up to 1.5 GB for the first one;
        // with the grid and background lanes running eight at once the app peaked at ~6 GB.
        await MediaFoundationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await FromMediaFoundationAsync(file, maxLongSide, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            MediaFoundationGate.Release();
        }
    }

    private static readonly SemaphoreSlim MediaFoundationGate = new(1, 1);

    /// <summary>Shell thumbnail (fast, cached by Windows).</summary>
    private async Task<RasterImage?> FromShellAsync(StorageFile file, int maxLongSide, CancellationToken cancellationToken)
    {
        try
        {
            // Never abandoned (see WicRasterDecoder): the caller's worker slot must stay taken while Windows works.
            cancellationToken.ThrowIfCancellationRequested();
            using var thumbnail = await file.GetThumbnailAsync(ThumbnailMode.SingleItem, (uint)Math.Clamp(maxLongSide, 64, 2048), ThumbnailOptions.ResizeThumbnail)
                .AsTask().ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            if (thumbnail is null || thumbnail.Type != ThumbnailType.Image)
            {
                return null; // A generic file icon: no shell thumbnail handler (e.g. Windows Server) or no decoder.
            }

            return await DecodeAsync(thumbnail, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is COMException or ArgumentException or UnauthorizedAccessException or FileNotFoundException)
        {
            _logger.LogDebug(ex, "No shell thumbnail for {File}", file.Name);
            return null;
        }
    }

    /// <summary>
    /// Decodes a frame with Media Foundation. Needed where the shell has no video thumbnail handler although the
    /// video plays (UI run #14 on Windows Server 2025: clip.mp4 played but its tile stayed a placeholder).
    /// </summary>
    private async Task<RasterImage?> FromMediaFoundationAsync(StorageFile file, int maxLongSide, CancellationToken cancellationToken)
    {
        try
        {
            var clip = await MediaClip.CreateFromFileAsync(file).AsTask(cancellationToken).ConfigureAwait(false);
            var video = clip.GetVideoEncodingProperties();
            if (video is null || video.Width == 0 || video.Height == 0) return null;
            var scale = Math.Min(1.0, Math.Clamp(maxLongSide, 64, 2048) / (double)Math.Max(video.Width, video.Height));
            var width = Math.Max(1, (int)Math.Round(video.Width * scale));
            var height = Math.Max(1, (int)Math.Round(video.Height * scale));
            var composition = new MediaComposition();
            composition.Clips.Add(clip);
            // A little way in: first frames are often black (fade-in).
            var at = TimeSpan.FromTicks(Math.Min(clip.OriginalDuration.Ticks / 10, TimeSpan.FromSeconds(1).Ticks));
            cancellationToken.ThrowIfCancellationRequested();
            using var stream = await composition.GetThumbnailAsync(at, width, height, VideoFramePrecision.NearestFrame)
                .AsTask().ConfigureAwait(false); // never abandoned (see WicRasterDecoder)
            cancellationToken.ThrowIfCancellationRequested();
            return stream is null ? null : await DecodeAsync(stream, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is COMException or ArgumentException or UnauthorizedAccessException or FileNotFoundException or InvalidOperationException)
        {
            _logger.LogInformation(ex, "No poster frame for {File}: no decoder for this video", file.Name);
            return null;
        }
    }

    private static async Task<RasterImage> DecodeAsync(global::Windows.Storage.Streams.IRandomAccessStream stream, CancellationToken cancellationToken)
    {
        var decoder = await BitmapDecoder.CreateAsync(stream).AsTask(cancellationToken).ConfigureAwait(false);
        var pixels = await decoder.GetPixelDataAsync(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Straight, new BitmapTransform(),
            ExifOrientationMode.RespectExifOrientation, ColorManagementMode.ColorManageToSRgb).AsTask().ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        return new RasterImage((int)decoder.OrientedPixelWidth, (int)decoder.OrientedPixelHeight, pixels.DetachPixelData());
    }
}
