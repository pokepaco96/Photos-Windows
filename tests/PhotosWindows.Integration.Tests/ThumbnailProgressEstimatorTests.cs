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

    [Theory]
    [InlineData(49, false)]
    [InlineData(50, true)]
    [InlineData(51, true)]
    public void The_bar_shows_from_fifty_pending(int pending, bool visible) =>
        Assert.Equal(visible, new ThumbnailProgressEstimator().Update(T0, pending, paused: false).Visible);

    [Fact]
    public void A_long_gap_without_samples_is_not_counted_as_work()
    {
        var e = new ThumbnailProgressEstimator();
        for (var i = 0; i <= 10; i++) e.Update(T0.AddSeconds(3 * i), 1000 - (10 * i), paused: false);

        // The PC sleeps 20 minutes between two samples; 10 more thumbnails are counted afterwards.
        var s = e.Update(T0.AddSeconds(30 + 1200), 890, paused: false);
        s = e.Update(T0.AddSeconds(30 + 1203), 880, paused: false);
        Assert.NotNull(s.Remaining);
        Assert.InRange(s.Remaining!.Value.TotalSeconds, 230, 300); // 880 at ~3.3/s, not ~880 at 0.1/s
    }

    [Fact]
    public void A_clock_going_back_adds_no_time_and_breaks_nothing()
    {
        var e = new ThumbnailProgressEstimator();
        for (var i = 0; i <= 10; i++) e.Update(T0.AddSeconds(3 * i), 1000 - (10 * i), paused: false);
        var s = e.Update(T0.AddSeconds(-3600), 890, paused: false);
        Assert.Equal(110, s.Done);
        s = e.Update(T0.AddSeconds(33), 880, paused: false);
        Assert.Equal(120, s.Done);
        Assert.InRange(s.Fraction, 0, 1);
    }

    [Fact]
    public void A_pause_of_ten_minutes_with_a_sample_only_at_its_ends_is_not_counted()
    {
        var e = new ThumbnailProgressEstimator();
        for (var i = 0; i <= 10; i++) e.Update(T0.AddSeconds(3 * i), 1000 - (10 * i), paused: false);
        var s = e.Update(T0.AddSeconds(33), 900, paused: true);
        Assert.Null(s.Remaining);
        Assert.True(s.Paused);
        s = e.Update(T0.AddSeconds(633), 900, paused: true);
        Assert.Null(s.Remaining);
        s = e.Update(T0.AddSeconds(636), 890, paused: false);
        Assert.NotNull(s.Remaining);
        Assert.InRange(s.Remaining!.Value.TotalSeconds, 230, 300);
    }

    [Fact]
    public void Work_added_while_thumbnails_are_made_never_pushes_done_past_total_or_backwards()
    {
        var e = new ThumbnailProgressEstimator();
        var s = e.Update(T0, 500, paused: false);
        var lastDone = 0;
        var rnd = new Random(3);
        var pending = 500;
        for (var i = 1; i < 300; i++)
        {
            pending = Math.Max(1, pending - rnd.Next(0, 12) + (rnd.Next(10) == 0 ? rnd.Next(0, 80) : 0));
            s = e.Update(T0.AddSeconds(3 * i), pending, paused: false);
            Assert.InRange(s.Done, lastDone, s.Total);
            Assert.InRange(s.Fraction, 0, 1);
            lastDone = s.Done;
        }
    }

    [Fact]
    public void No_estimate_while_nothing_is_being_made()
    {
        var e = new ThumbnailProgressEstimator();
        ThumbnailProgressState s = default;
        for (var i = 0; i <= 40; i++) s = e.Update(T0.AddSeconds(3 * i), 700, paused: false);
        Assert.True(s.Visible);
        Assert.Null(s.Remaining);
    }

    [Fact]
    public void A_new_pass_after_an_import_starts_from_zero_with_its_own_total()
    {
        var e = new ThumbnailProgressEstimator();
        for (var i = 0; i <= 10; i++) e.Update(T0.AddSeconds(3 * i), 100 - (10 * i), paused: false);
        Assert.False(e.Update(T0.AddSeconds(33), 0, paused: false).Visible);

        var s = e.Update(T0.AddSeconds(36), 2000, paused: true);
        Assert.Equal((0, 2000), (s.Done, s.Total));
        Assert.True(s.Paused);
        Assert.Null(s.Remaining);
    }
}
