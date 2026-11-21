using PhotosWindows.Application.Thumbnails;

namespace PhotosWindows.Integration.Tests;

public class ThumbnailProgressEstimatorTests
{
    private static readonly DateTime T0 = new(2026, 11, 21, 18, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void Steady_work_gives_progress_and_a_time_left_that_matches_the_rate()
    {
        var e = new ThumbnailProgressEstimator();
        var s = e.Update(T0, 1000, paused: false);
        Assert.True(s.Visible);
        Assert.Equal(0, s.Done);
        Assert.Null(s.Remaining);

        // 10 thumbnails every 3 s.
        for (var i = 1; i <= 10; i++) s = e.Update(T0.AddSeconds(3 * i), 1000 - 10 * i, paused: false);
        Assert.Equal(100, s.Done);
        Assert.Equal(1000, s.Total);
        Assert.Equal(0.1, s.Fraction, 3);
        Assert.NotNull(s.Remaining);
        Assert.InRange(s.Remaining!.Value.TotalSeconds, 260, 280); // 900 left at 3.33/s = 270 s
    }

    [Fact]
    public void No_estimate_while_paused_and_the_pause_does_not_slow_the_estimate()
    {
        var e = new ThumbnailProgressEstimator();
        for (var i = 0; i <= 10; i++) e.Update(T0.AddSeconds(3 * i), 1000 - 10 * i, paused: false);

        // An import runs for 5 minutes and adds 200 photos; nothing is made meanwhile.
        var s = e.Update(T0.AddSeconds(60), 1100, paused: true);
        Assert.True(s.Paused);
        Assert.Null(s.Remaining);
        Assert.Equal(1200, s.Total);
        s = e.Update(T0.AddSeconds(330), 1100, paused: true);
        Assert.Null(s.Remaining);

        s = e.Update(T0.AddSeconds(333), 1090, paused: false);
        Assert.False(s.Paused);
        Assert.NotNull(s.Remaining);
        Assert.InRange(s.Remaining!.Value.TotalSeconds, 300, 360); // ~1090 at ~3.3/s, not stretched by the 5 min pause
    }

    [Fact]
    public void Small_passes_stay_hidden_and_the_bar_hides_and_resets_when_done()
    {
        var e = new ThumbnailProgressEstimator();
        Assert.False(e.Update(T0, 12, paused: false).Visible);
        Assert.False(e.Update(T0.AddSeconds(3), 0, paused: false).Visible);

        Assert.True(e.Update(T0.AddSeconds(6), 500, paused: false).Visible);
        Assert.True(e.Update(T0.AddSeconds(9), 10, paused: false).Visible); // stays visible to the end of the pass
        Assert.False(e.Update(T0.AddSeconds(12), 0, paused: false).Visible);

        var next = e.Update(T0.AddSeconds(15), 80, paused: false);
        Assert.Equal(80, next.Total);
        Assert.Equal(0, next.Done);
    }
}
