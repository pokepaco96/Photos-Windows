using System.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using PhotosWindows.Application.Abstractions;
using PhotosWindows.Application.Import;
using PhotosWindows.Application.Library;
using PhotosWindows.Application.Thumbnails;
using PhotosWindows.Domain;
using PhotosWindows.Integration.Tests.Support;

namespace PhotosWindows.Integration.Tests;

/// <summary>
/// Reproduces the "black viewer that never finishes loading" pattern: background thumbnail work that never completes
/// (a native poster-frame call that ignores cancellation) must not block the photo the user opens.
/// </summary>
public class ViewerPriorityTests
{
    /// <summary>Poster-frame extraction that never returns and ignores cancellation, like a stuck native call.</summary>
    private sealed class HangingVideoFrames : IVideoFrameExtractor
    {
        public int Started;

        public Task<RasterImage?> ExtractPosterFrameAsync(string path, int maxLongSide, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref Started);
            return new TaskCompletionSource<RasterImage?>().Task;
        }
    }

    private static async Task<(TestHost Host, HangingVideoFrames Frames, long PhotoId)> LibraryWithStuckBackgroundAsync(int videos)
    {
        var frames = new HangingVideoFrames();
        var host = new TestHost(s => s.AddSingleton<IVideoFrameExtractor>(frames));
        var settings = host.Get<ISettingsStore>();
        settings.Save(settings.Load() with { MaxBackgroundWorkers = 2 });
        await host.CreateLibraryAsync();
        var dir = host.SourceDir("lib");
        for (var i = 0; i < videos; i++) Fixtures.QuickTime(Path.Combine(dir, $"v{i}.mov"), TimeSpan.FromMinutes(1), new DateTime(2020, 1, 1).AddDays(i));
        Fixtures.Jpeg(Path.Combine(dir, "BPEZ5111.JPG"), new Fixtures.PhotoSpec { Width = 1600, Height = 1200, Seed = 529 });
        await host.Import.ImportAsync(new ImportRequest([dir], ImportMode.AddInPlace), null, CancellationToken.None);
        var photo = (await host.AllAsync()).Single(a => a.MediaType == MediaType.Photo);
        return (host, frames, photo.Id);
    }

    [Fact]
    public async Task Opening_a_photo_is_not_blocked_by_stuck_background_thumbnails()
    {
        var (host, frames, photoId) = await LibraryWithStuckBackgroundAsync(videos: 6);
        await using var _ = host;
        var thumbs = host.Get<ThumbnailService>();

        using var stopBackground = new CancellationTokenSource();
        var background = Task.Run(() => thumbs.ProcessPendingAsync(40, stopBackground.Token));
        var sw = Stopwatch.StartNew();
        while (frames.Started < 2 && sw.ElapsedMilliseconds < 5000) await Task.Delay(20);
        Assert.True(frames.Started >= 2, "background work should be stuck in poster-frame extraction");

        sw.Restart();
        using var viewer = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var preview = await thumbs.GetOrCreateAsync(photoId, ThumbnailSize.Large, ViewerCancellation(viewer));
        Assert.NotNull(preview);
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(5), $"viewer preview took {sw.Elapsed}");
        stopBackground.Cancel();
    }

    [Fact]
    public async Task A_stuck_background_item_times_out_frees_its_slot_and_is_not_retried()
    {
        var (host, frames, photoId) = await LibraryWithStuckBackgroundAsync(videos: 3);
        await using var _ = host;
        var thumbs = host.Get<ThumbnailService>();
        thumbs.GenerationTimeout = TimeSpan.FromMilliseconds(300);

        // Every pending item ends (photo ready, stuck videos failed) instead of holding the lane for ever.
        var sw = Stopwatch.StartNew();
        while (await thumbs.ProcessPendingAsync(40, CancellationToken.None) > 0 && sw.Elapsed < TimeSpan.FromSeconds(20)) { }
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(20));
        Assert.Equal((0, 0), thumbs.BusyWorkers);
        var all = await host.AllAsync();
        Assert.All(all.Where(a => a.MediaType == MediaType.Video), v => Assert.Equal(ThumbnailState.Failed, v.ThumbnailState));
        Assert.Equal(ThumbnailState.Ready, all.Single(a => a.Id == photoId).ThumbnailState);

        // Failed items are not picked up again by the next pass.
        var started = frames.Started;
        await thumbs.ProcessPendingAsync(40, CancellationToken.None);
        Assert.Equal(started, frames.Started);
    }

    [Fact]
    public async Task Same_preview_requested_twice_is_generated_once_and_in_flight_entries_are_cleared()
    {
        var (host, _, photoId) = await LibraryWithStuckBackgroundAsync(videos: 0);
        await using var __ = host;
        var thumbs = host.Get<ThumbnailService>();
        var a = thumbs.GetOrCreateAsync(photoId, ThumbnailSize.Large, CancellationToken.None);
        var b = thumbs.GetOrCreateAsync(photoId, ThumbnailSize.Large, CancellationToken.None);
        Assert.Equal(await a, await b);
        Assert.Equal((0, 0), thumbs.BusyWorkers);

        // A caller that gives up does not leave a stuck entry: a later request still gets the file.
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => thumbs.GetOrCreateAsync(photoId, ThumbnailSize.Small, cancelled.Token));
        Assert.NotNull(await thumbs.GetOrCreateAsync(photoId, ThumbnailSize.Small, CancellationToken.None));
        Assert.Single(Directory.EnumerateFiles(Path.GetDirectoryName(await a)!, "*", SearchOption.TopDirectoryOnly), f => f == a.Result);
        Assert.Empty(Directory.EnumerateFiles(host.Get<ILibraryContext>().Current!.ThumbnailsPath, "*.tmp", SearchOption.AllDirectories));
    }

    private static CancellationToken ViewerCancellation(CancellationTokenSource cts) => cts.Token;
}
