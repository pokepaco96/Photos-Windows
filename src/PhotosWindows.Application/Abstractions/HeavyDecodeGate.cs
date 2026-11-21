namespace PhotosWindows.Application.Abstractions;

/// <summary>Who is waiting for a heavy native decode. Lower values are served first.</summary>
public enum DecodePriority
{
    /// <summary>The photo open in the viewer.</summary>
    Viewer = 0,

    /// <summary>The visible grid, the editor, exports: the user is waiting for it.</summary>
    Interactive = 1,

    /// <summary>Background passes (thumbnails, hashes, face analysis).</summary>
    Background = 2,
}

/// <summary>
/// Process-wide limit on full HEIF/AVIF decodes running at once, whoever asks for them.
/// Each live Windows HEVC decode takes ~300–450 MB of private memory and 45–80 native threads (measured on Windows 11
/// with HEIF 1.2.22 / HEVC 2.4.13, 24 MP iPhone HEICs). The viewer, grid and background lanes used to be limited only
/// on their own, so up to ~10 such decodes ran together: 4.1 GB private and 925 threads with a 60 GB library (r19,
/// 2026-11-21). This gate keeps the total at <see cref="Capacity"/>:
/// <list type="bullet">
/// <item>the viewer may use every slot and is always served first;</item>
/// <item>everything else together leaves one slot free for the viewer;</item>
/// <item>background work uses at most <see cref="BackgroundCapacity"/> slots.</item>
/// </list>
/// A slot must be held until the native decode has really returned (never released on cancel while the codec still
/// works). The caller's priority is ambient (<see cref="Use"/>) so the decoders do not need to know who asked.
/// </summary>
public sealed class HeavyDecodeGate
{
    public static HeavyDecodeGate Shared { get; } = new(capacity: 3, backgroundCapacity: 1);

    /// <summary>
    /// Embedded HEIF thumbnails (the 320–416 px tile stored in the file). Each is decoded by its own HEVC decoder
    /// instance (~250 MB until Windows tears it down, independent of releasing our references). Measured on Windows 11
    /// with 100 iPhone HEICs: 1/2/4 at once took 83/66/58 ms per thumbnail but 0.4/0.5/0.7 GB median private memory
    /// (1.0/1.1/1.3 GB peak), so more than two in the background buys little.
    /// </summary>
    public static HeavyDecodeGate Embedded { get; } = new(capacity: 3, backgroundCapacity: 2);

    private static readonly AsyncLocal<DecodePriority?> Ambient = new();
    private readonly Lock _lock = new();

    // One FIFO list per priority. A cancelled waiter is unlinked at once, so it never holds a place in the queue, never
    // counts as waiting and never keeps its TaskCompletionSource alive until the next release.
    private readonly LinkedList<Waiter>[] _queues = [new(), new(), new()];
    private int _busy;
    private int _busyOthers;
    private int _busyBackground;
    private int _peak;
    private long _granted;
    private long _queued;
    private long _cancelled;
    private int _peakWaiting;

    public HeavyDecodeGate(int capacity, int backgroundCapacity)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(capacity, 2);
        ArgumentOutOfRangeException.ThrowIfLessThan(backgroundCapacity, 1);
        Capacity = capacity;
        BackgroundCapacity = Math.Min(backgroundCapacity, capacity - 1);
    }

    public int Capacity { get; }

    public int BackgroundCapacity { get; }

    /// <summary>Priority of the current asynchronous flow; <see cref="DecodePriority.Interactive"/> when nobody set one.</summary>
    public static DecodePriority CurrentPriority => Ambient.Value ?? DecodePriority.Interactive;

    /// <summary>Sets the priority of decodes started from this flow until disposed.</summary>
    public static IDisposable Use(DecodePriority priority)
    {
        Validate(priority);
        var previous = Ambient.Value;
        Ambient.Value = priority;
        return new Restore(previous);
    }

    private sealed class Restore(DecodePriority? previous) : IDisposable
    {
        public void Dispose() => Ambient.Value = previous;
    }

    /// <summary>Waits for a slot at the current flow's priority.</summary>
    public Task<IDisposable> AcquireAsync(CancellationToken cancellationToken) => AcquireAsync(CurrentPriority, cancellationToken);

    /// <summary>Waits for a slot. Dispose the result only when the native work has finished.</summary>
    public Task<IDisposable> AcquireAsync(DecodePriority priority, CancellationToken cancellationToken)
    {
        Validate(priority);
        if (cancellationToken.IsCancellationRequested) return Task.FromCanceled<IDisposable>(cancellationToken);
        Waiter waiter;
        lock (_lock)
        {
            if (NoneQueuedAtOrAbove(priority) && CanStart(priority)) return Task.FromResult<IDisposable>(Take(priority));
            waiter = new Waiter(priority);
            waiter.Node = _queues[(int)priority].AddLast(waiter);
            _queued++;
            _peakWaiting = Math.Max(_peakWaiting, _queues[0].Count + _queues[1].Count + _queues[2].Count);
        }

        if (!cancellationToken.CanBeCanceled) return waiter.Result.Task;

        // Registered outside the lock (a token that is already cancelled runs the callback right here). The waiter may
        // be granted or cancelled before the registration is stored: whoever finishes the waiter disposes it.
        var registration = cancellationToken.Register(static state =>
        {
            var (gate, w, token) = ((HeavyDecodeGate, Waiter, CancellationToken))state!;
            gate.Cancel(w, token);
        }, (this, waiter, cancellationToken));

        bool finished;
        lock (_lock)
        {
            finished = waiter.Done;
            if (!finished) waiter.Registration = registration;
        }

        if (finished) registration.Dispose();
        return waiter.Result.Task;
    }

    private void Cancel(Waiter waiter, CancellationToken token)
    {
        List<(Waiter, Lease)>? grants;
        lock (_lock)
        {
            if (waiter.Done) return; // already granted: the caller owns the lease and must dispose it
            waiter.Done = true;
            _queues[(int)waiter.Priority].Remove(waiter.Node!);
            waiter.Node = null;
            waiter.Registration = default; // the callback is running: nothing left to dispose
            _cancelled++;

            // The cancelled waiter may have been the one that held back lower priorities (NoneQueuedAtOrAbove).
            grants = Dispatch();
        }

        waiter.Result.TrySetCanceled(token);
        Complete(grants);
    }

    private static void Validate(DecodePriority priority)
    {
        if (priority is < DecodePriority.Viewer or > DecodePriority.Background)
        {
            throw new ArgumentOutOfRangeException(nameof(priority), priority, "Unknown decode priority.");
        }
    }

    /// <summary>Counts for diagnostics.</summary>
    public HeavyDecodeStats Stats
    {
        get
        {
            lock (_lock)
            {
                return new HeavyDecodeStats(_busy, _busyBackground, _queues[0].Count + _queues[1].Count + _queues[2].Count, _peak, _granted, _queued)
                {
                    Cancelled = _cancelled,
                    PeakWaiting = _peakWaiting,
                };
            }
        }
    }

    private bool NoneQueuedAtOrAbove(DecodePriority priority)
    {
        for (var p = 0; p <= (int)priority; p++)
        {
            if (_queues[p].Count > 0) return false;
        }

        return true;
    }

    private bool CanStart(DecodePriority priority) => priority switch
    {
        DecodePriority.Viewer => _busy < Capacity,
        DecodePriority.Interactive => _busy < Capacity && _busyOthers < Capacity - 1,
        _ => _busy < Capacity && _busyOthers < Capacity - 1 && _busyBackground < BackgroundCapacity,
    };

    private Lease Take(DecodePriority priority)
    {
        _busy++;
        if (priority != DecodePriority.Viewer) _busyOthers++;
        if (priority == DecodePriority.Background) _busyBackground++;
        _peak = Math.Max(_peak, _busy);
        _granted++;
        return new Lease(this, priority);
    }

    private void Release(DecodePriority priority)
    {
        List<(Waiter, Lease)>? grants;
        lock (_lock)
        {
            _busy--;
            if (priority != DecodePriority.Viewer) _busyOthers--;
            if (priority == DecodePriority.Background) _busyBackground--;
            if (_busy < 0 || _busyOthers < 0 || _busyBackground < 0) throw new InvalidOperationException("Heavy decode gate released more slots than it granted.");
            grants = Dispatch();
        }

        Complete(grants);
    }

    /// <summary>
    /// Grants waiting requests in priority order, FIFO within a priority. Called under the lock. A lower priority never
    /// starts while a higher one waits: its limits are a subset of the higher one's, so if the higher cannot start the
    /// lower cannot either, and when the higher can it is served first.
    /// </summary>
    private List<(Waiter, Lease)>? Dispatch()
    {
        List<(Waiter, Lease)>? grants = null;
        for (var p = 0; p < _queues.Length; p++)
        {
            var queue = _queues[p];
            while (queue.First is { } node && CanStart(node.Value.Priority))
            {
                queue.RemoveFirst();
                var waiter = node.Value;
                waiter.Node = null;
                waiter.Done = true;
                (grants ??= []).Add((waiter, Take(waiter.Priority)));
            }

            if (queue.Count > 0) break;
        }

        return grants;
    }

    /// <summary>Completes granted waiters outside the lock. A waiter whose result cannot be set (never happens: only Cancel and Dispatch finish waiters, under the lock) gives its slot back.</summary>
    private void Complete(List<(Waiter, Lease)>? grants)
    {
        if (grants is null) return;
        foreach (var (waiter, lease) in grants)
        {
            CancellationTokenRegistration registration;
            lock (_lock)
            {
                registration = waiter.Registration;
                waiter.Registration = default;
            }

            registration.Dispose();
            if (!waiter.Result.TrySetResult(lease)) lease.Dispose();
        }
    }

    private sealed class Waiter(DecodePriority priority)
    {
        public DecodePriority Priority { get; } = priority;
        public TaskCompletionSource<IDisposable> Result { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public LinkedListNode<Waiter>? Node;
        public CancellationTokenRegistration Registration;
        public bool Done;
    }

    private sealed class Lease(HeavyDecodeGate owner, DecodePriority priority) : IDisposable
    {
        private int _released;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _released, 1) == 0) owner.Release(priority);
        }
    }
}

/// <summary>Heavy decode gate counts: running now (of which background), waiting, highest running at once, totals.</summary>
public sealed record HeavyDecodeStats(int Running, int RunningBackground, int Waiting, int Peak, long Granted, long Queued)
{
    /// <summary>Waiters whose caller cancelled before they got a slot (they never started a decode).</summary>
    public long Cancelled { get; init; }

    /// <summary>Most requests waiting at once.</summary>
    public int PeakWaiting { get; init; }

    public override string ToString() => $"heavy decodes {Running} ({RunningBackground} bg)+{Waiting}w peak {Peak} granted {Granted} cancelled {Cancelled}";
}
