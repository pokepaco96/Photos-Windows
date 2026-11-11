using PhotosWindows.Domain;

namespace PhotosWindows.Application.Import;

public sealed record ImportRequest(IReadOnlyList<string> Sources, ImportMode Mode, bool ImportDuplicates = false, bool Recursive = true);

public sealed record ImportProgress(
    long JobId,
    int Total,
    int Processed,
    int Imported,
    int Duplicates,
    int Skipped,
    int Failed,
    string? CurrentFile,
    TimeSpan Elapsed,
    TimeSpan? EstimatedRemaining)
{
    /// <summary>Dominant phase: <see cref="ImportPhase.Scanning"/> until the job exists, then the per-file pipeline.</summary>
    public ImportPhase Phase { get; init; } = ImportPhase.CopyAndHash;

    /// <summary>While scanning: files looked at so far (compatible or not). The total is unknown until the scan ends.</summary>
    public int Examined { get; init; }

    /// <summary>While scanning: name (not full path) of the folder being read.</summary>
    public string? CurrentFolder { get; init; }

    /// <summary>Bytes of originals read and hashed so far in this run.</summary>
    public long BytesProcessed { get; init; }

    public double Fraction => Total == 0 ? 1 : (double)Processed / Total;
}

public sealed record ImportResult(
    long JobId,
    ImportJobStatus Status,
    int Imported,
    int Duplicates,
    int Skipped,
    int Failed,
    IReadOnlyList<long> NewAssetIds);
