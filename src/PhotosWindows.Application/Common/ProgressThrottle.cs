using System.Diagnostics;

namespace PhotosWindows.Application.Common;

/// <summary>Thread-safe rate limiter for progress reports: at most one report per interval.</summary>
public sealed class ProgressThrottle(TimeSpan interval)
{
    private readonly long _intervalTicks = (long)(interval.TotalSeconds * Stopwatch.Frequency);
    private long _next;

    public bool ShouldReport()
    {
        var now = Stopwatch.GetTimestamp();
        var next = Interlocked.Read(ref _next);
        return now >= next && Interlocked.CompareExchange(ref _next, now + _intervalTicks, next) == next;
    }
}
