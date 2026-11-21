using System.Collections.Concurrent;
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
/// The process-wide limit on heavy (HEIF/AVIF) decodes: never more than its capacity at once, the viewer always has
/// a slot it can get and is served first, background work stays small, and a cancelled waiter never takes a slot.
/// </summary>
public class HeavyDecodeGateTests
{
    private static async Task<IDisposable> GrantedAsync(Task<IDisposable> pending)
    {
        var done = await Task.WhenAny(pending, Task.Delay(TimeSpan.FromSeconds(5)));
        Assert.Same(pending, done);
        return await pending;
    }

    private static async Task StillWaitingAsync(Task pending)
    {
        await Task.Delay(50);
        Assert.False(pending.IsCompleted, "should still be waiting for a slot");
    }

    [Fact]
    public async Task Background_and_grid_leave_a_slot_for_the_viewer_and_background_takes_one_at_most()
    {
        var gate = new HeavyDecodeGate(capacity: 3, backgroundCapacity: 1);

        var bg1 = await GrantedAsync(gate.AcquireAsync(DecodePriority.Background, CancellationToken.None));
        var bg2 = gate.AcquireAsync(DecodePriority.Background, CancellationToken.None);
        await StillWaitingAsync(bg2);

        var grid1 = await GrantedAsync(gate.AcquireAsync(DecodePriority.Interactive, CancellationToken.None));
        var grid2 = gate.AcquireAsync(DecodePriority.Interactive, CancellationToken.None);
        await StillWaitingAsync(grid2); // background + grid already hold capacity - 1

        var viewer = await GrantedAsync(gate.AcquireAsync(DecodePriority.Viewer, CancellationToken.None));
        Assert.Equal(3, gate.Stats.Running);
        var viewer2 = gate.AcquireAsync(DecodePriority.Viewer, CancellationToken.None);
        await StillWaitingAsync(viewer2); // never above capacity, even for the viewer

        // A slot frees: the viewer is served before the grid and the background that queued earlier.
        bg1.Dispose();
        var v2 = await GrantedAsync(viewer2);
        Assert.False(grid2.IsCompleted);
        Assert.False(bg2.IsCompleted);

        // The viewer finishes: the grid goes before the background.
        viewer.Dispose();
        v2.Dispose();
        var g2 = await GrantedAsync(grid2);
        await StillWaitingAsync(bg2);
        grid1.Dispose();
        var b2 = await GrantedAsync(bg2);

        g2.Dispose();
        b2.Dispose();
        Assert.Equal(0, gate.Stats.Running);
        Assert.Equal(3, gate.Stats.Peak);
    }

    [Fact]
    public async Task A_cancelled_waiter_never_takes_a_slot()
    {
        var gate = new HeavyDecodeGate(capacity: 2, backgroundCapacity: 1);
        var held = await GrantedAsync(gate.AcquireAsync(DecodePriority.Background, CancellationToken.None));

        using var cts = new CancellationTokenSource();
        var cancelled = gate.AcquireAsync(DecodePriority.Background, cts.Token);
        var next = gate.AcquireAsync(DecodePriority.Background, CancellationToken.None);
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => cancelled);

        held.Dispose();
        var granted = await GrantedAsync(next);
        Assert.Equal(1, gate.Stats.Running);
        Assert.Equal(0, gate.Stats.Waiting);
        granted.Dispose();
        granted.Dispose(); // releasing twice gives back one slot only
        Assert.Equal(0, gate.Stats.Running);
    }

    [Fact]
    public async Task Never_more_than_capacity_running_under_load()
    {
        var gate = new HeavyDecodeGate(capacity: 3, backgroundCapacity: 1);
        int live = 0, peak = 0, peakOthers = 0, peakBackground = 0, others = 0, background = 0;
        var rnd = new Random(7);
        var work = Enumerable.Range(0, 300).Select(i => (DecodePriority)(i % 3)).OrderBy(_ => rnd.Next()).Select(p => Task.Run(async () =>
        {
            using var cts = new CancellationTokenSource();
            if (p == DecodePriority.Interactive && Random.Shared.Next(4) == 0) cts.CancelAfter(1);
            IDisposable slot;
            try
            {
                slot = await gate.AcquireAsync(p, cts.Token);
            }
            catch (OperationCanceledException)
            {
                return;
            }

            using (slot)
            {
                var l = Interlocked.Increment(ref live);
                var o = p == DecodePriority.Viewer ? Volatile.Read(ref others) : Interlocked.Increment(ref others);
                var b = p == DecodePriority.Background ? Interlocked.Increment(ref background) : Volatile.Read(ref background);
                lock (gate)
                {
                    peak = Math.Max(peak, l);
                    peakOthers = Math.Max(peakOthers, o);
                    peakBackground = Math.Max(peakBackground, b);
                }

                await Task.Delay(2);
                if (p == DecodePriority.Background) Interlocked.Decrement(ref background);
                if (p != DecodePriority.Viewer) Interlocked.Decrement(ref others);
                Interlocked.Decrement(ref live);
            }
        })).ToArray();
        await Task.WhenAll(work).WaitAsync(TimeSpan.FromSeconds(30));

        Assert.InRange(peak, 1, 3);
        Assert.InRange(peakOthers, 1, 2);
        Assert.Equal(1, peakBackground);
        Assert.Equal(0, gate.Stats.Running);
        Assert.Equal(0, gate.Stats.Waiting);
    }

    /// <summary>The real Skia renderer, recording the decode priority each call sees.</summary>
    private sealed class PriorityRecordingRenderer(IImageRenderer inner) : IImageRenderer
    {
        public ConcurrentQueue<DecodePriority> Seen { get; } = new();

        public Task<RenderedImage> RenderAsync(string sourcePath, FormatFamily family, EditRecipe recipe, int? maxLongSide, EncodeOptions encode, CancellationToken cancellationToken)
        {
            Seen.Enqueue(HeavyDecodeGate.CurrentPriority);
            return inner.RenderAsync(sourcePath, family, recipe, maxLongSide, encode, cancellationToken);
        }

        public Task<RenderedImage> RenderAsync(RasterImage source, EditRecipe recipe, int? maxLongSide, EncodeOptions encode, CancellationToken cancellationToken) =>
            inner.RenderAsync(source, recipe, maxLongSide, encode, cancellationToken);

        public Task<RasterImage> DecodeOrientedAsync(string sourcePath, FormatFamily family, int maxLongSide, CancellationToken cancellationToken) =>
            inner.DecodeOrientedAsync(sourcePath, family, maxLongSide, cancellationToken);

        public bool CanDecode(FormatFamily family) => inner.CanDecode(family);

        public (int Width, int Height)? ProbeSize(string sourcePath, FormatFamily family) => inner.ProbeSize(sourcePath, family);
    }

    [Fact]
    public async Task Each_thumbnail_lane_decodes_with_its_own_priority()
    {
        PriorityRecordingRenderer? renderer = null;
        await using var host = new TestHost(s => s.AddSingleton<IImageRenderer>(sp =>
            renderer = new PriorityRecordingRenderer(ActivatorUtilities.CreateInstance<SkiaImageRenderer>(sp))));
        await host.CreateLibraryAsync();
        var dir = host.SourceDir("lib");
        for (var i = 0; i < 3; i++) Fixtures.Jpeg(Path.Combine(dir, $"IMG_{i}.jpg"), new Fixtures.PhotoSpec { Width = 400, Height = 300, Seed = i });
        await host.Import.ImportAsync(new ImportRequest([dir], ImportMode.AddInPlace), null, CancellationToken.None);
        var ids = (await host.AllAsync()).OrderBy(a => a.FileName, StringComparer.Ordinal).Select(a => a.Id).ToList();
        var thumbs = host.Get<ThumbnailService>();

        async Task<DecodePriority> SeenFor(Func<Task> request)
        {
            renderer!.Seen.Clear();
            await request();
            Assert.True(renderer.Seen.TryDequeue(out var seen), "a decode should have run");
            return seen;
        }

        Assert.Equal(DecodePriority.Viewer, await SeenFor(() => thumbs.GetOrCreateAsync(ids[0], ThumbnailSize.Large, ThumbnailPriority.Viewer, CancellationToken.None)));
        Assert.Equal(DecodePriority.Interactive, await SeenFor(() => thumbs.GetOrCreateAsync(ids[1], ThumbnailSize.Large, CancellationToken.None)));
        Assert.Equal(DecodePriority.Background, await SeenFor(() => thumbs.GetOrCreateAsync(ids[2], ThumbnailSize.Large, ThumbnailPriority.Background, CancellationToken.None)));
        Assert.Equal(DecodePriority.Interactive, HeavyDecodeGate.CurrentPriority); // nothing leaks into the caller
    }
}
