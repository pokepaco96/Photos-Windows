using Microsoft.Extensions.Logging;
using PhotosWindows.Application.Abstractions;
using PhotosWindows.Application.Common;
using PhotosWindows.Application.Import;
using PhotosWindows.Domain;

namespace PhotosWindows.Application.Maintenance;

public sealed record VerificationReport(
    IReadOnlyList<string> CatalogProblems,
    IReadOnlyList<AssetFile> MissingFiles,
    IReadOnlyList<AssetFile> ReappearedFiles,
    IReadOnlyList<AssetFile> ChangedFiles,
    IReadOnlyList<string> OrphanManagedFiles,
    IReadOnlyList<string> PartialFiles,
    int CheckedFiles,
    TimeSpan Duration)
{
    public bool IsHealthy => CatalogProblems.Count == 0 && MissingFiles.Count == 0 && ChangedFiles.Count == 0 && OrphanManagedFiles.Count == 0 && PartialFiles.Count == 0;
}

public sealed record VerificationProgress(int Done, int Total);

/// <summary>
/// Verification, reconciliation and repair. File-system watchers can miss events (sleep, network drives,
/// offline edits), so this pass is the source of truth and can be run on demand or periodically.
/// </summary>
public sealed class LibraryMaintenanceService
{
    private readonly ILibraryContext _context;
    private readonly ICatalogStore _catalog;
    private readonly IAssetRepository _assets;
    private readonly ImportService _import;
    private readonly IClock _clock;
    private readonly ILogger<LibraryMaintenanceService> _logger;

    public LibraryMaintenanceService(ILibraryContext context, ICatalogStore catalog, IAssetRepository assets, ImportService import, IClock clock, ILogger<LibraryMaintenanceService> logger)
    {
        _context = context;
        _catalog = catalog;
        _assets = assets;
        _import = import;
        _clock = clock;
        _logger = logger;
    }

    /// <summary>
    /// Checks catalog integrity and every file. Availability flags are updated (missing/reappeared) so the UI can show
    /// "file not available"; nothing is deleted. Content hashing is optional because it reads every byte.
    /// </summary>
    public async Task<VerificationReport> VerifyAsync(bool verifyContentHashes, IProgress<VerificationProgress>? progress, CancellationToken ct)
    {
        var started = DateTime.UtcNow;
        var library = _context.RequireCurrent();
        var problems = await _catalog.CheckIntegrityAsync(ct).ConfigureAwait(false);
        var files = await _assets.GetAllFilesAsync(ct).ConfigureAwait(false);
        var missing = new List<AssetFile>();
        var reappeared = new List<AssetFile>();
        var changed = new List<AssetFile>();
        var managedKnown = new HashSet<string>(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);

        for (var i = 0; i < files.Count; i++)
        {
            ct.ThrowIfCancellationRequested();
            if (i % 50 == 0) progress?.Report(new VerificationProgress(i, files.Count));
            var file = files[i];
            var path = library.Resolve(file);
            if (file.StorageMode == StorageMode.Managed) managedKnown.Add(Path.GetFullPath(path));
            var info = new FileInfo(path);
            if (!info.Exists)
            {
                missing.Add(file);
                if (file.IsAvailable) await _assets.UpdateFileAvailabilityAsync(file.Id, false, _clock.UtcNow, ct).ConfigureAwait(false);
                continue;
            }

            if (!file.IsAvailable)
            {
                reappeared.Add(file);
                await _assets.UpdateFileAvailabilityAsync(file.Id, true, _clock.UtcNow, ct).ConfigureAwait(false);
            }

            var sizeChanged = info.Length != file.SizeBytes;
            var hashChanged = verifyContentHashes && !string.Equals(await FileHashing.Sha256Async(path, ct).ConfigureAwait(false), file.Sha256, StringComparison.OrdinalIgnoreCase);
            if (sizeChanged || hashChanged) changed.Add(file);
        }

        progress?.Report(new VerificationProgress(files.Count, files.Count));

        var orphans = new List<string>();
        var partials = new List<string>();
        if (Directory.Exists(library.OriginalsPath))
        {
            foreach (var f in Directory.EnumerateFiles(library.OriginalsPath, "*", new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true }))
            {
                ct.ThrowIfCancellationRequested();
                if (f.EndsWith(PathSafety.PartialSuffix, StringComparison.OrdinalIgnoreCase)) partials.Add(f);
                else if (!managedKnown.Contains(Path.GetFullPath(f)) && MediaFormats.IsCandidate(f)) orphans.Add(f);
            }
        }

        var report = new VerificationReport(problems, missing, reappeared, changed, orphans, partials, files.Count, DateTime.UtcNow - started);
        _logger.LogInformation("Verification: {Checked} files, {Missing} missing, {Changed} changed, {Orphans} orphan copies, {Partials} partial copies, {Problems} catalog problems",
            files.Count, missing.Count, changed.Count, orphans.Count, partials.Count, problems.Count);
        return report;
    }

    /// <summary>Deletes leftover *.pwpartial files (incomplete copies from interrupted imports) inside originals.</summary>
    public int CleanPartialFiles(VerificationReport report)
    {
        ArgumentNullException.ThrowIfNull(report);
        var library = _context.RequireCurrent();
        var removed = 0;
        foreach (var f in report.PartialFiles.Where(f => PathSafety.IsUnder(f, library.OriginalsPath) && f.EndsWith(PathSafety.PartialSuffix, StringComparison.OrdinalIgnoreCase)))
        {
            File.Delete(f);
            removed++;
        }

        return removed;
    }

    /// <summary>Re-catalogues managed copies that exist on disk but not in the catalog (e.g. a crash between copy and commit).</summary>
    public Task<ImportResult> AdoptOrphansAsync(VerificationReport report, IProgress<ImportProgress>? progress, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(report);
        return _import.ImportAsync(new ImportRequest(report.OrphanManagedFiles, ImportMode.AddInPlace, ImportDuplicates: false, Recursive: false), progress, ct);
    }

    /// <summary>
    /// Looks for missing referenced files inside <paramref name="searchFolder"/> (e.g. after the user moved a folder
    /// or the drive letter changed). A candidate must have the same name and size, and the same SHA-256, to be relinked.
    /// </summary>
    public async Task<int> RelinkMissingAsync(IReadOnlyList<AssetFile> missing, string searchFolder, CancellationToken ct)
    {
        var library = _context.RequireCurrent();
        var byName = Directory.EnumerateFiles(searchFolder, "*", new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true, AttributesToSkip = FileAttributes.ReparsePoint | FileAttributes.System })
            .GroupBy(p => Path.GetFileName(p), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.ToList(), StringComparer.OrdinalIgnoreCase);
        var relinked = 0;
        foreach (var file in missing.Where(f => f.StorageMode == StorageMode.Referenced))
        {
            ct.ThrowIfCancellationRequested();
            if (!byName.TryGetValue(Path.GetFileName(file.StoredPath), out var candidates)) continue;
            foreach (var candidate in candidates.Where(c => new FileInfo(c).Length == file.SizeBytes))
            {
                if (!string.Equals(await FileHashing.Sha256Async(candidate, ct).ConfigureAwait(false), file.Sha256, StringComparison.OrdinalIgnoreCase)) continue;
                if (await _assets.FindFileByStoredPathAsync(candidate, ct).ConfigureAwait(false) is not null) continue;
                var info = new FileInfo(candidate);
                await _assets.UpdateFileLocationAsync(file.Id, PathSafety.IsUnder(candidate, library.OriginalsPath) ? Path.GetRelativePath(library.RootPath, candidate) : candidate, info.Length, info.LastWriteTimeUtc, ct).ConfigureAwait(false);
                relinked++;
                break;
            }
        }

        return relinked;
    }

    public Task RebuildSearchIndexAsync(CancellationToken ct) => _assets.RefreshSearchIndexAsync(null, ct);
}
