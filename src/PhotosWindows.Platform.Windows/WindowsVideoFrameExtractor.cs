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

        // One in-process Media Foundation extraction at a time, and it counts against the process-wide limit on heavy
        // decodes (the HEVC decoder takes ~300 MB, ~1.2 GB for 4K). Measured on Windows 11 with iPhone videos: the
        // shell returns only an icon for ~40 % of them (HDR HEVC, some re-encoded H.264).
        await MediaFoundationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            using var slot = await HeavyDecodeGate.Shared.AcquireAsync(cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            var reader = await FromSourceReaderAsync(path, cancellationToken).ConfigureAwait(false);
            if (reader is not null) return reader;
            return await FromMediaFoundationAsync(file, maxLongSide, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            MediaFoundationGate.Release();
        }
    }

    private static readonly SemaphoreSlim MediaFoundationGate = new(1, 1);

    /// <summary>
    /// Software decode of one frame (see <see cref="MediaFoundationFrameReader"/>); null when Media Foundation cannot
    /// read the file. Runs off the caller's thread and is never abandoned: the slot stays taken while it works.
    /// </summary>
    private async Task<RasterImage?> FromSourceReaderAsync(string path, CancellationToken cancellationToken)
    {
        try
        {
            RasterImage frame;
            using (DecodeActivity.Begin(DecodeKind.VideoSourceReader))
            {
                frame = await Task.Run(() => MediaFoundationFrameReader.Read(Path.GetFullPath(path)), CancellationToken.None).ConfigureAwait(false);
            }

            cancellationToken.ThrowIfCancellationRequested();
            return frame;
        }
        catch (Exception ex) when (ex is COMException or InvalidDataException or ArgumentException or InvalidCastException)
        {
            _logger.LogInformation(ex, "Source Reader could not read a frame of {File}", Path.GetFileName(path));
            return null;
        }
    }

    /// <summary>Shell thumbnail (fast, cached by Windows).</summary>
    private async Task<RasterImage?> FromShellAsync(StorageFile file, int maxLongSide, CancellationToken cancellationToken)
    {
        try
        {
            // Never abandoned (see WicRasterDecoder): the caller's worker slot must stay taken while Windows works.
            cancellationToken.ThrowIfCancellationRequested();
            using var activity = DecodeActivity.Begin(DecodeKind.VideoShell);
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
            using var activity = DecodeActivity.Begin(DecodeKind.VideoComposition);
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
            if (stream is null) return null;
            var frame = await DecodeAsync(stream, cancellationToken).ConfigureAwait(false);
            // MediaComposition returns a blank frame (every pixel exactly 0) when it cannot decode, e.g. for HDR HEVC: a
            // placeholder is better than a black tile. This path runs only after the Source Reader failed.
            return IsBlankFrame(frame) ? null : frame;
        }
        catch (Exception ex) when (ex is COMException or ArgumentException or UnauthorizedAccessException or FileNotFoundException or InvalidOperationException)
        {
            _logger.LogInformation(ex, "No poster frame for {File}: no decoder for this video", file.Name);
            return null;
        }
    }

    /// <summary>
    /// True when every pixel is at most 2 in each channel: what MediaComposition returns when its decoder failed (measured
    /// on Windows 11: exactly 0 everywhere for HLG and PQ HEVC). Every pixel is checked, so a dark scene, a night shot or an
    /// underexposed clip (sensor noise, a few lit pixels) is kept; only a frame with no signal at all is dropped. The old
    /// rule (every 7th pixel at most 8) could drop a legitimately dark frame.
    /// </summary>
    internal static bool IsBlankFrame(RasterImage image)
    {
        var p = image.Pixels;
        for (var i = 0; i + 2 < p.Length; i += 4)
        {
            if (p[i] > 2 || p[i + 1] > 2 || p[i + 2] > 2) return false;
        }

        return true;
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
