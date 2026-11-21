namespace PhotosWindows.Application.Abstractions;

/// <summary>Kinds of native decode that load a codec into this process (or, for the shell, ask Windows for one).</summary>
public enum DecodeKind
{
    /// <summary>Full HEIF/AVIF image decode through WIC (an HEVC/AV1 video decoder instance).</summary>
    HeifFull = 0,

    /// <summary>The thumbnail stored in a HEIF file (its own HEVC decoder instance).</summary>
    HeifEmbedded = 1,

    /// <summary>One video frame through the Media Foundation Source Reader (software decode).</summary>
    VideoSourceReader = 2,

    /// <summary>One video frame through MediaComposition (fallback).</summary>
    VideoComposition = 3,

    /// <summary>The Windows shell thumbnail of a video: decoded out of process, listed to tell it apart.</summary>
    VideoShell = 4,
}

/// <summary>
/// Counts native decodes running now, by kind, with the highest number at once since the last snapshot. Diagnostics
/// only (the heartbeat log): it tells which kinds really overlap when the private memory peaks, so limits can be set
/// from measurements instead of guesses. Lock-free; one small object per decode.
/// </summary>
public static class DecodeActivity
{
    private const int Kinds = 5;
    private static readonly int[] Live = new int[Kinds];
    private static readonly int[] WindowPeak = new int[Kinds];
    private static readonly long[] Total = new long[Kinds];
    private static int _inProcessLive;
    private static int _inProcessWindowPeak;

    /// <summary>Marks one decode of <paramref name="kind"/> as running until disposed (dispose once, when the native call has returned).</summary>
    public static IDisposable Begin(DecodeKind kind)
    {
        var k = (int)kind;
        Raise(ref WindowPeak[k], Interlocked.Increment(ref Live[k]));
        Interlocked.Increment(ref Total[k]);
        if (kind != DecodeKind.VideoShell) Raise(ref _inProcessWindowPeak, Interlocked.Increment(ref _inProcessLive));
        return new Scope(kind);
    }

    private sealed class Scope(DecodeKind kind) : IDisposable
    {
        private int _ended;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _ended, 1) != 0) return;
            Interlocked.Decrement(ref Live[(int)kind]);
            if (kind != DecodeKind.VideoShell) Interlocked.Decrement(ref _inProcessLive);
        }
    }

    /// <summary>Running now by kind.</summary>
    public static int Running(DecodeKind kind) => Volatile.Read(ref Live[(int)kind]);

    /// <summary>
    /// "heifFull 1/2 embedded 0/2 sourceReader 1/1 composition 0/0 shell 0/1 inProcess 2/4": running now / highest at
    /// once since the previous snapshot, then started in total. Resets the window peaks to the current values.
    /// </summary>
    public static string Snapshot()
    {
        Span<int> now = stackalloc int[Kinds];
        Span<int> peak = stackalloc int[Kinds];
        for (var k = 0; k < Kinds; k++)
        {
            now[k] = Volatile.Read(ref Live[k]);
            peak[k] = Interlocked.Exchange(ref WindowPeak[k], now[k]);
            peak[k] = Math.Max(peak[k], now[k]);
        }

        var inNow = Volatile.Read(ref _inProcessLive);
        var inPeak = Math.Max(Interlocked.Exchange(ref _inProcessWindowPeak, inNow), inNow);
        return $"heifFull {now[0]}/{peak[0]} embedded {now[1]}/{peak[1]} sourceReader {now[2]}/{peak[2]} composition {now[3]}/{peak[3]} shell {now[4]}/{peak[4]} inProcess {inNow}/{inPeak} " +
               $"(started {Volatile.Read(ref Total[0])}/{Volatile.Read(ref Total[1])}/{Volatile.Read(ref Total[2])}/{Volatile.Read(ref Total[3])}/{Volatile.Read(ref Total[4])})";
    }

    private static void Raise(ref int peak, int value)
    {
        int seen;
        while (value > (seen = Volatile.Read(ref peak)) && Interlocked.CompareExchange(ref peak, value, seen) != seen)
        {
        }
    }
}
