using System.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using PhotosWindows.Application.Abstractions;
using PhotosWindows.Application.Import;
using PhotosWindows.Application.Library;
using PhotosWindows.Application.Thumbnails;
using PhotosWindows.Domain;
using PhotosWindows.Imaging;
using PhotosWindows.Integration.Tests.Support;

namespace PhotosWindows.Integration.Tests;

/// <summary>
/// Reproduces the reported pattern (photo 4589 of 8685 stays black on "Loading preview…" with the CPU busy): scrolling
/// the grid requests a thumbnail for every tile it shows and abandons the request when the tile is recycled. The
/// abandoned requests must not keep decoding, and the photo the user opens must not wait behind them.
/// </summary>
public class GridScrollStarvationTests
{
    /// <summary>The real Skia renderer, with a fixed extra cost per decode like an expensive codec (HEIC through WIC).</summary>
    internal sealed class CountingSlowRenderer(IImageRenderer inner, TimeSpan cost) : IImageRenderer
    {
        private int _started, _live, _peak;

        public int Started => Volatile.Read(ref _started);

        public int PeakConcurrent => Volatile.Read(ref _peak);

        public TimeSpan Cost { get; set; } = cost;

        public async Task<RenderedImage> RenderAsync(string sourcePath, FormatFamily family, EditRecipe recipe, int? maxLongSide, EncodeOptions encode, CancellationToken cancellationToken)
        {
            Enter();
            try
            {
                await Task.Delay(Cost, CancellationToken.None); // like a native decode: does not stop when asked
                return await inner.RenderAsync(sourcePath, family, recipe, maxLongSide, encode, cancellationToken);
            }
            finally
            {
                Interlocked.Decrement(ref _live);
            }
        }

        public Task<RenderedImage> RenderAsync(RasterImage source, EditRecipe recipe, int? maxLongSide, EncodeOptions encode, CancellationToken cancellationToken) =>
            inner.RenderAsync(source, recipe, maxLongSide, encode, cancellationToken);

        public async Task<RasterImage> DecodeOrientedAsync(string sourcePath, FormatFamily family, int maxLongSide, CancellationToken cancellationToken)
        {
            Enter();
            try
            {
                await Task.Delay(Cost, CancellationToken.None);
                return await inner.DecodeOrientedAsync(sourcePath, family, maxLongSide, cancellationToken);
            }
            finally
            {
                Interlocked.Decrement(ref _live);
            }
        }

        public bool CanDecode(FormatFamily family) => inner.CanDecode(family);

        public (int Width, int Height)? ProbeSize(string sourcePath, FormatFamily family) => inner.ProbeSize(sourcePath, family);

        private void Enter()
        {
            Interlocked.Increment(ref _started);
            var live = Interlocked.Increment(ref _live);
            int peak;
            while (live > (peak = Volatile.Read(ref _peak)) && Interlocked.CompareExchange(ref _peak, live, peak) != peak) { }
        }
    }

    internal static async Task<(TestHost Host, CountingSlowRenderer Renderer, IReadOnlyList<long> Ids)> LibraryAsync(int photos, TimeSpan cost)
    {
        CountingSlowRenderer? renderer = null;
        var host = new TestHost(s => s.AddSingleton<IImageRenderer>(sp =>
            renderer = new CountingSlowRenderer(ActivatorUtilities.CreateInstance<SkiaImageRenderer>(sp), TimeSpan.Zero)));
        var settings = host.Get<ISettingsStore>();
        settings.Save(settings.Load() with { MaxBackgroundWorkers = 2 });
        await host.CreateLibraryAsync();
        var dir = host.SourceDir("lib");
        for (var i = 0; i < photos; i++) Fixtures.Jpeg(Path.Combine(dir, $"IMG_{i:0000}.jpg"), new Fixtures.PhotoSpec { Width = 400, Height = 300, Seed = i });
        await host.Import.ImportAsync(new ImportRequest([dir], ImportMode.AddInPlace), null, CancellationToken.None);
        _ = host.Get<ThumbnailService>();
        renderer!.Cost = cost;
        var ids = (await host.AllAsync()).OrderBy(a => a.FileName, StringComparer.Ordinal).Select(a => a.Id).ToList();
        return (host, renderer, ids);
    }

    [Fact]
    public async Task Abandoned_grid_requests_do_not_decode_and_do_not_delay_the_viewer()
    {
        var (host, renderer, ids) = await LibraryAsync(200, TimeSpan.FromMilliseconds(100));
        await using var _ = host;
        var thumbs = host.Get<ThumbnailService>();

        // Fast scroll: every tile asks for its thumbnail and is recycled a moment later.
        var grid = new List<Task>();
        foreach (var id in ids.Take(199))
        {
            using var tile = new CancellationTokenSource();
            var request = thumbs.GetOrCreateAsync(id, ThumbnailSize.Small, tile.Token);
            tile.Cancel();
            grid.Add(request.ContinueWith(_ => { }, TaskScheduler.Default));
        }

        // The user opens the last photo.
        var sw = Stopwatch.StartNew();
        using var viewer = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        var preview = await thumbs.GetOrCreateAsync(ids[^1], ThumbnailSize.Large, ThumbnailPriority.Viewer, viewer.Token);
        var elapsed = sw.Elapsed;
        await Task.WhenAll(grid);
        await Task.Delay(300);

        Assert.NotNull(preview);
        Assert.True(elapsed < TimeSpan.FromSeconds(1), $"viewer preview took {elapsed.TotalMilliseconds:0} ms behind abandoned grid work");
        // Before the fix all 199 abandoned thumbnails were decoded anyway (CPU busy long after scrolling stopped).
        Assert.True(renderer.Started <= 6, $"{renderer.Started} decodes started for 199 abandoned requests + 1 preview");
    }
}
