using System.Diagnostics;

namespace PhotosWindows.Application.Import;

/// <summary>Import phases. Several run at once on different files; timings are summed across workers.</summary>
public enum ImportPhase
{
    Preparing,
    Scanning,
    Planning,
    Signature,
    LookupExisting,
    Metadata,
    CopyAndHash,
    Hash,
    DuplicateLookup,
    Dimensions,
    CatalogInsert,
    ItemUpdate,
    Finishing,
}

/// <summary>
/// Local, in-memory timing of each import phase (worker time, summed over parallel workers) and bytes read.
/// Never contains paths or file names; used by the import benchmark and written to the local log at the end of a job.
/// </summary>
public sealed class ImportDiagnostics
{
    private readonly long[] _ticks = new long[Enum.GetValues<ImportPhase>().Length];
    private readonly int[] _counts = new int[Enum.GetValues<ImportPhase>().Length];
    private long _bytes;

    public long BytesHashed => Interlocked.Read(ref _bytes);

    public void AddBytes(long bytes) => Interlocked.Add(ref _bytes, bytes);

    public TimeSpan Time(ImportPhase phase) => TimeSpan.FromTicks((long)(Interlocked.Read(ref _ticks[(int)phase]) * (TimeSpan.TicksPerSecond / (double)Stopwatch.Frequency)));

    public int Count(ImportPhase phase) => Volatile.Read(ref _counts[(int)phase]);

    public Scope Measure(ImportPhase phase) => new(this, phase, Stopwatch.GetTimestamp());

    private void Add(ImportPhase phase, long elapsed)
    {
        Interlocked.Add(ref _ticks[(int)phase], elapsed);
        Interlocked.Increment(ref _counts[(int)phase]);
    }

    public IReadOnlyList<(ImportPhase Phase, TimeSpan Time, int Count)> Snapshot() =>
        Enum.GetValues<ImportPhase>().Where(p => Count(p) > 0).Select(p => (p, Time(p), Count(p))).ToList();

    public override string ToString() => string.Join(", ", Snapshot().Select(s => $"{s.Phase}={s.Time.TotalMilliseconds:0}ms/{s.Count}"));

    public readonly struct Scope(ImportDiagnostics owner, ImportPhase phase, long start) : IDisposable
    {
        public void Dispose() => owner.Add(phase, Stopwatch.GetTimestamp() - start);
    }
}
