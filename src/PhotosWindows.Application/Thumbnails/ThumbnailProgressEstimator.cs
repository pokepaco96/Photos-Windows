namespace PhotosWindows.Application.Thumbnails;

/// <summary>What the shell shows about the background thumbnail pass.</summary>
/// <param name="Visible">False when there is nothing (or too little) to show.</param>
/// <param name="Done">Thumbnails made since this pass started.</param>
/// <param name="Total">Thumbnails in this pass (grows when an import adds more).</param>
/// <param name="Remaining">Estimated time left; null until there is enough data, or while paused.</param>
/// <param name="Paused">The pass is waiting (an import is running).</param>
public readonly record struct ThumbnailProgressState(bool Visible, int Done, int Total, TimeSpan? Remaining, bool Paused)
{
    public double Fraction => Total <= 0 ? 0 : Math.Clamp(Done / (double)Total, 0, 1);
}

/// <summary>
/// Turns periodic samples of the pending-thumbnail count into progress and a time estimate. The rate is measured over
/// the last <see cref="Window"/> of work only: time spent paused (an import) is not counted, so the estimate does not
/// jump after a pause. Pure logic, no clock of its own.
/// </summary>
public sealed class ThumbnailProgressEstimator
{
    /// <summary>A pass smaller than this is not shown: it ends before the user could read it.</summary>
    public const int MinimumToShow = 50;

    /// <summary>Period over which the rate is measured.</summary>
    public static readonly TimeSpan Window = TimeSpan.FromSeconds(90);

    /// <summary>No estimate before this much measured work: early rates are noisy.</summary>
    public static readonly TimeSpan Warmup = TimeSpan.FromSeconds(15);

    private readonly Queue<(TimeSpan Worked, int Done)> _samples = new();
    private int _total;
    private int _lastPending;
    private bool _visible;
    private TimeSpan _worked;
    private DateTime? _last;

    public ThumbnailProgressState Update(DateTime nowUtc, int pending, bool paused)
    {
        pending = Math.Max(0, pending);
        if (pending == 0)
        {
            Reset();
            return default;
        }

        if (_total == 0)
        {
            _total = pending;
        }
        else if (pending > _lastPending)
        {
            _total += pending - _lastPending; // an import added work to this pass
        }

        _lastPending = pending;
        if (pending >= MinimumToShow) _visible = true;

        var delta = _last is { } last && nowUtc > last ? nowUtc - last : TimeSpan.Zero;
        _last = nowUtc;
        var done = _total - pending;
        if (!paused)
        {
            _worked += delta;
            _samples.Enqueue((_worked, done));
            while (_samples.Count > 2 && _worked - _samples.Peek().Worked > Window) _samples.Dequeue();
        }

        TimeSpan? remaining = null;
        if (!paused && _samples.Count >= 2)
        {
            var first = _samples.Peek();
            var span = _worked - first.Worked;
            var made = done - first.Done;
            if (_worked >= Warmup && span > TimeSpan.Zero && made > 0)
            {
                remaining = TimeSpan.FromSeconds(pending / (made / span.TotalSeconds));
            }
        }

        return new ThumbnailProgressState(_visible, done, _total, remaining, paused);
    }

    private void Reset()
    {
        _samples.Clear();
        _total = 0;
        _lastPending = 0;
        _visible = false;
        _worked = TimeSpan.Zero;
        _last = null;
    }
}
