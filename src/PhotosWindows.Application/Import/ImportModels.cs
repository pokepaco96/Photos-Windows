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
