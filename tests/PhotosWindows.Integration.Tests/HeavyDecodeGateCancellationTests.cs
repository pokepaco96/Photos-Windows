using System.Runtime.CompilerServices;
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
/// Cancellation and priority at the heavy decode gate: a request cancelled before it gets a slot never starts the
/// native decode and leaves the queue at once; a decode that has started keeps its slot until it really ends; every
/// slot is given back exactly once; the ambient priority reaches the point where the slot is requested.
/// </summary>
public class HeavyDecodeGateCancellationTests
{
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(5);

    [Fact]
    public void A_request_cancelled_before_it_asks_takes_nothing_and_is_not_queued()
    {
        var gate = new HeavyDecodeGate(capacity: 2, backgroundCapacity: 1);
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        var request = gate.AcquireAsync(DecodePriority.Viewer, cts.Token);

        Assert.True(request.IsCanceled);
        var stats = gate.Stats;
        Assert.Equal((0, 0, 0L, 0L), (stats.Running, stats.Waiting, stats.Granted, stats.Queued));
    }

    [Fact]
    public async Task A_cancelled_waiter_leaves_the_queue_at_once_even_behind_many_others()
    {
        var gate = new HeavyDecodeGate(capacity: 2, backgroundCapacity: 1);
        var held = await gate.AcquireAsync(DecodePriority.Background, CancellationToken.None);
        var before = Enumerable.Range(0, 50).Select(_ => gate.AcquireAsync(DecodePriority.Background, CancellationToken.None)).ToList();
        using var cts = new CancellationTokenSource();
        var cancelled = gate.AcquireAsync(DecodePriority.Background, cts.Token);
        var after = Enumerable.Range(0, 50).Select(_ => gate.AcquireAsync(DecodePriority.Background, CancellationToken.None)).ToList();
        Assert.Equal(101, gate.Stats.Waiting);

        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => cancelled);
        Assert.Equal(100, gate.Stats.Waiting); // gone without any slot being released
        Assert.Equal(1, gate.Stats.Cancelled);

        // FIFO for the rest: each release grants exactly the next one in arrival order.
        held.Dispose();
        foreach (var next in before.Concat(after))
        {
            var lease = await next.WaitAsync(Wait);
            Assert.Equal(1, gate.Stats.Running);
            lease.Dispose();
        }

        Assert.Equal((0, 0), (gate.Stats.Running, gate.Stats.Waiting));
    }

    [Fact]
    public async Task Cancelling_the_head_of_a_higher_priority_lets_the_lower_ones_that_fit_start()
    {
        var gate = new HeavyDecodeGate(capacity: 3, backgroundCapacity: 1);
        var a = await gate.AcquireAsync(DecodePriority.Viewer, CancellationToken.None);
        var b = await gate.AcquireAsync(DecodePriority.Viewer, CancellationToken.None);
        var c = await gate.AcquireAsync(DecodePriority.Viewer, CancellationToken.None);
        using var cts = new CancellationTokenSource();
        var viewer = gate.AcquireAsync(DecodePriority.Viewer, cts.Token);
        var grid = gate.AcquireAsync(DecodePriority.Interactive, CancellationToken.None);
        a.Dispose(); // the waiting viewer is served first
        var v = await viewer.WaitAsync(Wait);
        Assert.False(grid.IsCompleted);

        v.Dispose();
        var g = await grid.WaitAsync(Wait);
        foreach (var l in new[] { b, c, g }) l.Dispose();
        Assert.Equal((0, 0), (gate.Stats.Running, gate.Stats.Waiting));
    }

    [Fact]
    public async Task Cancel_racing_the_grant_either_cancels_or_grants_never_both_and_no_slot_is_lost()
    {
        var gate = new HeavyDecodeGate(capacity: 2, backgroundCapacity: 1);
        var granted = 0;
        var cancelled = 0;
        for (var i = 0; i < 2000; i++)
        {
            var held = await gate.AcquireAsync(DecodePriority.Interactive, CancellationToken.None);
            using var cts = new CancellationTokenSource();
            var waiting = gate.AcquireAsync(DecodePriority.Interactive, cts.Token);
            var release = Task.Run(held.Dispose);
            var cancel = Task.Run(cts.Cancel);
            await Task.WhenAll(release, cancel);
            try
            {
                var lease = await waiting.WaitAsync(Wait);
                granted++;
                lease.Dispose();
            }
            catch (OperationCanceledException)
            {
                cancelled++;
            }

            var stats = gate.Stats;
            Assert.True(stats.Running == 0 && stats.Waiting == 0, $"iteration {i}: {stats}");
        }

        Assert.Equal(2000, granted + cancelled);
    }

    [Fact]
    public async Task Releasing_concurrently_never_exceeds_capacity_and_ends_empty()
    {
        var gate = new HeavyDecodeGate(capacity: 3, backgroundCapacity: 2);
        var live = 0;
        var peak = 0;
        var work = Enumerable.Range(0, 2000).Select(i => Task.Run(async () =>
        {
            using var cts = new CancellationTokenSource();
            if (i % 5 == 0) cts.CancelAfter(i % 3);
            IDisposable lease;
            try
            {
                lease = await gate.AcquireAsync((DecodePriority)(i % 3), cts.Token);
            }
            catch (OperationCanceledException)
            {
                return;
            }

            var now = Interlocked.Increment(ref live);
            int p;
            while (now > (p = Volatile.Read(ref peak)) && Interlocked.CompareExchange(ref peak, now, p) != p) { }
            await Task.Yield();
            Interlocked.Decrement(ref live);
            lease.Dispose();
            lease.Dispose(); // a second dispose must give nothing back
        })).ToArray();
        await Task.WhenAll(work).WaitAsync(TimeSpan.FromSeconds(60));

        Assert.InRange(peak, 1, 3);
        var stats = gate.Stats;
        Assert.Equal((0, 0, 0), (stats.Running, stats.RunningBackground, stats.Waiting));
    }

    /// <summary>The decoder pattern: wait for a slot with the caller's token, then run native work that cannot be stopped.</summary>
    private static async Task<int> DecodeLikeWicAsync(HeavyDecodeGate gate, Task native, Action started, CancellationToken cancellationToken)
    {
        using var slot = await gate.AcquireAsync(cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        started();
        await native; // like GetPixelDataAsync without AsTask(token): never abandoned
        cancellationToken.ThrowIfCancellationRequested();
        return 1;
    }

    [Fact]
    public async Task A_started_native_decode_keeps_its_slot_until_it_really_ends_even_when_cancelled()
    {
        var gate = new HeavyDecodeGate(capacity: 2, backgroundCapacity: 1);
        var native = new TaskCompletionSource();
        using var cts = new CancellationTokenSource();
        var started = 0;

        var decode = DecodeLikeWicAsync(gate, native.Task, () => started++, cts.Token);
        Assert.Equal(1, started);
        cts.Cancel();
        await Task.Delay(50);
        Assert.False(decode.IsCompleted);
        Assert.Equal(1, gate.Stats.Running); // still held: the codec is still working

        native.SetResult();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => decode);
        Assert.Equal(0, gate.Stats.Running);
    }

    [Fact]
    public async Task A_decode_cancelled_while_waiting_for_its_slot_never_starts()
    {
        var gate = new HeavyDecodeGate(capacity: 2, backgroundCapacity: 1);
        var held = await gate.AcquireAsync(DecodePriority.Interactive, CancellationToken.None);
        using var cts = new CancellationTokenSource();
        var started = 0;

        var decode = DecodeLikeWicAsync(gate, Task.CompletedTask, () => started++, cts.Token);
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => decode);
        held.Dispose();
        await Task.Delay(50);

        Assert.Equal(0, started);
        Assert.Equal((0, 0), (gate.Stats.Running, gate.Stats.Waiting));
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static async Task<List<WeakReference>> GrantManyWithOneTokenAsync(HeavyDecodeGate gate, CancellationToken shared)
    {
        var refs = new List<WeakReference>();
        for (var i = 0; i < 1000; i++)
        {
            var held = await gate.AcquireAsync(DecodePriority.Interactive, CancellationToken.None);
            var waiting = gate.AcquireAsync(DecodePriority.Interactive, shared); // queued, registered on the shared token
            held.Dispose(); // granted: its registration must be removed
            (await waiting).Dispose();
            refs.Add(new WeakReference(waiting));
        }

        return refs;
    }

    [Fact]
    public async Task Thousands_of_waits_on_one_long_lived_token_leave_no_registrations_behind()
    {
        var gate = new HeavyDecodeGate(capacity: 2, backgroundCapacity: 1);
        using var lifetime = new CancellationTokenSource();

        var refs = await GrantManyWithOneTokenAsync(gate, lifetime.Token);
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();

        // A registration left on the token would keep its waiter (and the task) reachable for the token's lifetime.
        Assert.True(refs.Count(r => r.IsAlive) <= 2, $"{refs.Count(r => r.IsAlive)} waiters still reachable from the token");
        lifetime.Cancel();
        Assert.Equal(0, gate.Stats.Cancelled);
        Assert.Equal((0, 0), (gate.Stats.Running, gate.Stats.Waiting));
    }

    [Fact]
    public void Unknown_priorities_are_rejected()
    {
        var gate = new HeavyDecodeGate(capacity: 2, backgroundCapacity: 1);
        Assert.Throws<ArgumentOutOfRangeException>(() => { _ = gate.AcquireAsync((DecodePriority)3, CancellationToken.None); });
        Assert.Throws<ArgumentOutOfRangeException>(() => { _ = gate.AcquireAsync((DecodePriority)(-1), CancellationToken.None); });
        Assert.Throws<ArgumentOutOfRangeException>(() => HeavyDecodeGate.Use((DecodePriority)7));
        Assert.Equal(DecodePriority.Interactive, HeavyDecodeGate.CurrentPriority);
    }

    [Fact]
    public async Task The_ambient_priority_reaches_the_slot_request_through_awaits_and_thread_hops_and_never_leaks()
    {
        // Two slots, one held by the grid: only a viewer request can get the other one at once.
        var gate = new HeavyDecodeGate(capacity: 2, backgroundCapacity: 1);
        var grid = await gate.AcquireAsync(DecodePriority.Interactive, CancellationToken.None);

        async Task<IDisposable> DeepAsync()
        {
            await Task.Yield();
            await Task.Delay(1).ConfigureAwait(false);
            return await Task.Run(async () =>
            {
                await Task.Delay(1).ConfigureAwait(false);
                Assert.Equal(DecodePriority.Viewer, HeavyDecodeGate.CurrentPriority);
                return await gate.AcquireAsync(CancellationToken.None); // no explicit priority: the ambient one
            });
        }

        Task<IDisposable> request;
        using (HeavyDecodeGate.Use(DecodePriority.Viewer)) request = DeepAsync();
        Assert.Equal(DecodePriority.Interactive, HeavyDecodeGate.CurrentPriority); // the caller is back to its own

        var viewerLease = await request.WaitAsync(Wait); // granted although the grid holds capacity - 1: it asked as the viewer
        Assert.Equal(2, gate.Stats.Running);

        // A background flow started from a viewer scope keeps the priority it was given, and an exception restores it.
        try
        {
            using (HeavyDecodeGate.Use(DecodePriority.Background)) throw new InvalidOperationException();
        }
        catch (InvalidOperationException)
        {
        }

        Assert.Equal(DecodePriority.Interactive, HeavyDecodeGate.CurrentPriority);
        viewerLease.Dispose();
        grid.Dispose();
    }

    /// <summary>A renderer that decodes like WicRasterDecoder: waits for a slot of <see cref="Gate"/> with the caller's token, then cannot be stopped.</summary>
    private sealed class GatedRenderer(IImageRenderer inner, HeavyDecodeGate gate) : IImageRenderer
    {
        private int _started;

        public HeavyDecodeGate Gate { get; } = gate;

        public TaskCompletionSource Native { get; set; } = CompletedNative();

        public int Started => Volatile.Read(ref _started);

        private static TaskCompletionSource CompletedNative()
        {
            var t = new TaskCompletionSource();
            t.SetResult();
            return t;
        }

        private async Task DecodeAsync(CancellationToken ct)
        {
            using var slot = await Gate.AcquireAsync(ct);
            ct.ThrowIfCancellationRequested();
            Interlocked.Increment(ref _started);
            await Native.Task;
        }

        public async Task<RenderedImage> RenderAsync(string sourcePath, FormatFamily family, EditRecipe recipe, int? maxLongSide, EncodeOptions encode, CancellationToken cancellationToken)
        {
            await DecodeAsync(cancellationToken);
            return await inner.RenderAsync(sourcePath, family, recipe, maxLongSide, encode, CancellationToken.None);
        }

        public Task<RenderedImage> RenderAsync(RasterImage source, EditRecipe recipe, int? maxLongSide, EncodeOptions encode, CancellationToken cancellationToken) =>
            inner.RenderAsync(source, recipe, maxLongSide, encode, cancellationToken);

        public async Task<RasterImage> DecodeOrientedAsync(string sourcePath, FormatFamily family, int maxLongSide, CancellationToken cancellationToken)
        {
            await DecodeAsync(cancellationToken);
            return await inner.DecodeOrientedAsync(sourcePath, family, maxLongSide, CancellationToken.None);
        }

        public bool CanDecode(FormatFamily family) => inner.CanDecode(family);

        public (int Width, int Height)? ProbeSize(string sourcePath, FormatFamily family) => inner.ProbeSize(sourcePath, family);
    }

    private static async Task<(TestHost Host, GatedRenderer Renderer, long Id)> OnePhotoAsync(HeavyDecodeGate gate)
    {
        GatedRenderer? renderer = null;
        var host = new TestHost(s => s.AddSingleton<IImageRenderer>(sp =>
            renderer = new GatedRenderer(ActivatorUtilities.CreateInstance<SkiaImageRenderer>(sp), gate)));
        await host.CreateLibraryAsync();
        var dir = host.SourceDir("lib");
        Fixtures.Jpeg(Path.Combine(dir, "IMG_1.jpg"), new Fixtures.PhotoSpec { Width = 400, Height = 300, Seed = 1 });
        await host.Import.ImportAsync(new ImportRequest([dir], ImportMode.AddInPlace), null, CancellationToken.None);
        _ = host.Get<ThumbnailService>();
        var id = (await host.AllAsync()).Single().Id;
        return (host, renderer!, id);
    }

    [Fact]
    public async Task When_the_last_tile_waiting_for_a_shared_generation_leaves_before_its_slot_the_decode_never_starts()
    {
        var gate = new HeavyDecodeGate(capacity: 2, backgroundCapacity: 1);
        var (host, renderer, id) = await OnePhotoAsync(gate);
        await using var _ = host;
        var thumbs = host.Get<ThumbnailService>();
        var blocker = await gate.AcquireAsync(DecodePriority.Interactive, CancellationToken.None); // grid capacity in use

        using var tileA = new CancellationTokenSource();
        using var tileB = new CancellationTokenSource();
        var a = thumbs.GetOrCreateAsync(id, ThumbnailSize.Large, tileA.Token);
        var b = thumbs.GetOrCreateAsync(id, ThumbnailSize.Large, tileB.Token);
        await WaitUntilAsync(() => gate.Stats.Waiting == 1); // one generation, one request at the gate

        tileA.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => a);
        Assert.Equal(1, gate.Stats.Waiting); // B still waits: the shared generation goes on

        tileB.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => b);
        await WaitUntilAsync(() => gate.Stats.Waiting == 0); // left the gate queue without a slot being released

        blocker.Dispose();
        await Task.Delay(100);
        Assert.Equal(0, renderer.Started);
        Assert.Equal(0, gate.Stats.Running);
    }

    [Fact]
    public async Task When_one_tile_leaves_and_another_still_waits_the_shared_generation_decodes_once()
    {
        var gate = new HeavyDecodeGate(capacity: 2, backgroundCapacity: 1);
        var (host, renderer, id) = await OnePhotoAsync(gate);
        await using var _ = host;
        var thumbs = host.Get<ThumbnailService>();
        var blocker = await gate.AcquireAsync(DecodePriority.Interactive, CancellationToken.None);

        using var tileA = new CancellationTokenSource();
        var a = thumbs.GetOrCreateAsync(id, ThumbnailSize.Large, tileA.Token);
        var b = thumbs.GetOrCreateAsync(id, ThumbnailSize.Large, CancellationToken.None);
        await WaitUntilAsync(() => gate.Stats.Waiting == 1);
        tileA.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => a);

        blocker.Dispose();
        var path = await b.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.NotNull(path);
        Assert.Equal(1, renderer.Started);
        Assert.Equal((0, 0), (gate.Stats.Running, gate.Stats.Waiting));
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        var until = DateTime.UtcNow + Wait;
        while (!condition())
        {
            if (DateTime.UtcNow > until) throw new TimeoutException("condition not reached");
            await Task.Delay(5);
        }
    }
}
