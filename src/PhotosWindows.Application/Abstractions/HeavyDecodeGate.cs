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

    private static readonly AsyncLocal<DecodePriority?> Ambient = new();
    private readonly Lock _lock = new();
    private readonly Queue<Waiter>[] _queues = [new(), new(), new()];
    private int _busy;
    private int _busyOthers;
    private int _busyBackground;
    private int _peak;
    private long _granted;
    private long _queued;

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
        cancellationToken.ThrowIfCancellationRequested();
        Waiter waiter;
        lock (_lock)
        {
            if (NoneQueuedAtOrAbove(priority) && CanStart(priority)) return Task.FromResult<IDisposable>(Take(priority));
            waiter = new Waiter(priority);
            _queues[(int)priority].Enqueue(waiter);
            _queued++;
        }

        if (cancellationToken.CanBeCanceled)
        {
            waiter.Registration = cancellationToken.Register(static state =>
            {
                var (gate, w, token) = ((HeavyDecodeGate, Waiter, CancellationToken))state!;
                lock (gate._lock)
                {
                    if (w.Done) return;
                    w.Done = true; // skipped when its turn comes
                }

                w.Result.TrySetCanceled(token);
            }, (this, waiter, cancellationToken));
        }

        return waiter.Result.Task;
    }

    /// <summary>Counts for diagnostics.</summary>
    public HeavyDecodeStats Stats
    {
        get
        {
            lock (_lock)
            {
                var waiting = 0;
                foreach (var q in _queues)
                {
                    foreach (var w in q)
                    {
                        if (!w.Done) waiting++;
                    }
                }

                return new HeavyDecodeStats(_busy, _busyBackground, waiting, _peak, _granted, _queued);
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
        List<(Waiter, Lease)>? grants = null;
        lock (_lock)
        {
            _busy--;
            if (priority != DecodePriority.Viewer) _busyOthers--;
            if (priority == DecodePriority.Background) _busyBackground--;

            // Serve the queues in priority order. Background limits are a subset of the interactive ones, so a lower
            // priority can never start while a higher one that is waiting cannot.
            for (var p = 0; p < _queues.Length; p++)
            {
                var queue = _queues[p];
                while (queue.Count > 0)
                {
                    var head = queue.Peek();
                    if (head.Done)
                    {
                        queue.Dequeue();
                        continue;
                    }

                    if (!CanStart(head.Priority)) break;
                    queue.Dequeue();
                    head.Done = true;
                    (grants ??= []).Add((head, Take(head.Priority)));
                }
            }
        }

        if (grants is null) return;
        foreach (var (waiter, lease) in grants)
        {
            waiter.Registration.Dispose();
            waiter.Result.TrySetResult(lease);
        }
    }

    private sealed class Waiter(DecodePriority priority)
    {
        public DecodePriority Priority { get; } = priority;
        public TaskCompletionSource<IDisposable> Result { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
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
    public override string ToString() => $"heavy decodes {Running} ({RunningBackground} bg)+{Waiting}w peak {Peak}";
}
