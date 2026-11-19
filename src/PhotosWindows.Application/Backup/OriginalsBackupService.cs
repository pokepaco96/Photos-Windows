using System.Globalization;
using Microsoft.Extensions.Logging;
using PhotosWindows.Application.Abstractions;
using PhotosWindows.Application.Common;
using PhotosWindows.Application.Library;
using PhotosWindows.Domain;

namespace PhotosWindows.Application.Backup;

/// <summary>
/// Copies every physical original registered in the catalog (managed and referenced, RAW pairs, Live Photo clips and
/// sidecars; hidden and recently deleted assets included) into <c>YEAR/MONTH</c> folders under a destination chosen
/// by the user, byte for byte, and verifies each copy with SHA-256.
/// <para>Guarantees: sources are only ever opened for reading; nothing is re-encoded; no existing file or backup is
/// overwritten; a backup is called complete only when every file was copied and verified without a single issue.</para>
/// </summary>
public sealed class OriginalsBackupService
{
    public const string PartialSuffix = ".partial";
    public const string IncompleteSuffix = "-INCOMPLETA";
    private const string FolderPrefix = "PhotosWindows-Originales-Backup-";
    /// <summary>Room kept for the manifest and file-system overhead.</summary>
    private const long SpaceMargin = 64L * 1024 * 1024;

    private readonly ILibraryContext _context;
    private readonly IAssetRepository _assets;
    private readonly IVolumeInspector _volumes;
    private readonly IClock _clock;
    private readonly ILogger<OriginalsBackupService> _logger;

    public OriginalsBackupService(ILibraryContext context, IAssetRepository assets, IVolumeInspector volumes, IClock clock, ILogger<OriginalsBackupService> logger)
    {
        _context = context;
        _assets = assets;
        _volumes = volumes;
        _clock = clock;
        _logger = logger;
    }

    /// <summary>Counts, sizes, unavailable files and destination checks. Reads the catalog and file attributes only.</summary>
    public async Task<OriginalsPreflight> AnalyzeAsync(string? destinationFolder, CancellationToken ct)
    {
        var library = _context.RequireCurrent();
        var plan = await PlanAsync(library, ct).ConfigureAwait(false);
        return await Task.Run(() => Preflight(library, plan, destinationFolder, existingBytes: 0), ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Starts a new backup in a new folder under <paramref name="destinationFolder"/>. With
    /// <paramref name="acceptIncomplete"/> false (the default) it refuses to start when any original is unavailable.
    /// </summary>
    public async Task<OriginalsBackupResult> CreateAsync(string destinationFolder, bool acceptIncomplete, IProgress<OriginalsProgress>? progress, CancellationToken ct)
    {
        var library = _context.RequireCurrent();
        var destination = Path.GetFullPath(destinationFolder);
        var plan = await PlanAsync(library, ct).ConfigureAwait(false);
        var preflight = Preflight(library, plan, destination, existingBytes: 0);
        Guard(preflight, acceptIncomplete);

        Directory.CreateDirectory(destination);
        var created = _clock.UtcNow;
        var stamp = TimeZoneInfo.ConvertTimeFromUtc(created, _clock.LocalZone).ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
        var finalDir = Path.Combine(destination, FolderPrefix + PathSafety.SanitizeFileName(library.Name) + "-" + stamp);
        var workDir = finalDir + PartialSuffix;
        if (Directory.Exists(finalDir) || Directory.Exists(workDir) || Directory.Exists(finalDir + IncompleteSuffix))
        {
            throw new IOException("A backup with the same name already exists in the destination.");
        }

        Directory.CreateDirectory(workDir);
        return await RunAsync(library, plan, preflight, workDir, created, acceptIncomplete, progress, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Continues an interrupted backup. Files already in the folder are trusted only after their SHA-256 matches the
    /// source again. The folder may have moved (e.g. the external drive got another letter): pass where it is now.
    /// </summary>
    public async Task<OriginalsBackupResult> ResumeAsync(string partialFolder, bool acceptIncomplete, IProgress<OriginalsProgress>? progress, CancellationToken ct)
    {
        var library = _context.RequireCurrent();
        var workDir = Path.GetFullPath(partialFolder);
        if (!workDir.EndsWith(PartialSuffix, StringComparison.OrdinalIgnoreCase)) throw new ArgumentException("Not an unfinished originals backup folder.", nameof(partialFolder));
        var manifest = ReadManifest(workDir) ?? throw new InvalidDataException("The folder has no backup manifest.");
        if (manifest.LibraryId != library.Id) throw new InvalidOperationException("This unfinished backup belongs to another library.");

        var plan = await PlanAsync(library, ct).ConfigureAwait(false);
        var existing = Directory.EnumerateFiles(workDir, "*", SearchOption.AllDirectories).Sum(f => new FileInfo(f).Length);
        var preflight = Preflight(library, plan, Path.GetDirectoryName(workDir)!, existing);
        Guard(preflight, acceptIncomplete);
        return await RunAsync(library, plan, preflight, workDir, manifest.CreatedUtc, acceptIncomplete, progress, ct).ConfigureAwait(false);
    }

    /// <summary>Unfinished backups of the open library found directly under <paramref name="destinationFolder"/>.</summary>
    public IReadOnlyList<string> FindUnfinished(string destinationFolder)
    {
        var library = _context.RequireCurrent();
        if (!Directory.Exists(destinationFolder)) return [];
        return Directory.EnumerateDirectories(destinationFolder, FolderPrefix + "*" + PartialSuffix)
            .Where(d => ReadManifest(d)?.LibraryId == library.Id)
            .OrderByDescending(d => d, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <summary>Re-reads every file of a backup and checks size and SHA-256 against its manifest.</summary>
    public async Task<OriginalsVerification> VerifyAsync(string backupFolder, IProgress<OriginalsProgress>? progress, CancellationToken ct)
    {
        var problems = new List<string>();
        OriginalsManifest? manifest;
        try
        {
            manifest = ReadManifest(backupFolder);
        }
        catch (Exception ex) when (ex is InvalidDataException or System.Text.Json.JsonException or IOException)
        {
            return new OriginalsVerification(false, null, 0, ["Manifest unreadable: " + ex.Message]);
        }

        if (manifest is null) return new OriginalsVerification(false, null, 0, [OriginalsManifest.FileName + " not found."]);
        if (!manifest.Complete) problems.Add("The manifest says this backup is not complete.");
        var total = manifest.Files.Sum(f => f.Size);
        long bytes = 0;
        var done = 0;
        var listed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var file in manifest.Files)
        {
            ct.ThrowIfCancellationRequested();
            listed.Add(file.Path);
            progress?.Report(new OriginalsProgress(done, manifest.Files.Count, bytes, total, file.OriginalName));
            if (!TryResolve(backupFolder, file.Path, out var path))
            {
                problems.Add("Unsafe path in manifest: " + file.Path);
                continue;
            }

            if (!File.Exists(path)) problems.Add("Missing: " + file.Path);
            else if (new FileInfo(path).Length != file.Size) problems.Add("Size differs: " + file.Path);
            else if (!string.Equals(await FileHashing.Sha256Async(path, ct).ConfigureAwait(false), file.Sha256, StringComparison.OrdinalIgnoreCase)) problems.Add("Content differs (SHA-256): " + file.Path);
            bytes += file.Size;
            done++;
        }

        foreach (var extra in Directory.EnumerateFiles(backupFolder, "*", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(backupFolder, extra).Replace('\\', '/');
            if (relative != OriginalsManifest.FileName && !listed.Contains(relative)) problems.Add("Not in the manifest: " + relative);
        }

        return new OriginalsVerification(problems.Count == 0, manifest, done, problems);
    }

    /// <summary>
    /// Finished backups (complete, or incomplete by the user's choice) of the open library directly under
    /// <paramref name="destinationFolder"/>, most recently created or updated first. These can be updated incrementally.
    /// </summary>
    public IReadOnlyList<string> FindExisting(string destinationFolder)
    {
        var library = _context.RequireCurrent();
        if (!Directory.Exists(destinationFolder)) return [];
        var found = new List<(string Folder, DateTime When)>();
        foreach (var dir in Directory.EnumerateDirectories(destinationFolder, FolderPrefix + "*"))
        {
            if (dir.EndsWith(PartialSuffix, StringComparison.OrdinalIgnoreCase)) continue;
            try
            {
                if (ReadManifest(dir) is { } m && m.LibraryId == library.Id) found.Add((dir, m.UpdatedUtc ?? m.CreatedUtc));
            }
            catch (Exception ex) when (ex is InvalidDataException or System.Text.Json.JsonException or IOException or UnauthorizedAccessException)
            {
                // Not a readable backup of ours: ignore it, never touch it.
            }
        }

        return found.OrderByDescending(f => f.When).Select(f => f.Folder).ToList();
    }

    /// <summary>What <see cref="UpdateAsync"/> would copy into <paramref name="backupFolder"/>. Reads the catalog, the manifest and file attributes only.</summary>
    public async Task<OriginalsUpdatePreview> AnalyzeUpdateAsync(string backupFolder, CancellationToken ct)
    {
        var library = _context.RequireCurrent();
        var folder = Path.GetFullPath(backupFolder);
        var manifest = RequireUpdatable(library, folder);
        var plan = await PlanAsync(library, ct).ConfigureAwait(false);
        return await Task.Run(() =>
        {
            var (toCopy, _, _) = SplitForUpdate(folder, manifest, plan);
            var preflight = Preflight(library, toCopy, Path.GetDirectoryName(folder)!, existingBytes: 0);
            return new OriginalsUpdatePreview(folder, manifest.CreatedUtc, manifest.UpdatedUtc, manifest.Complete, manifest.Files.Count,
                toCopy.Count - preflight.Unavailable.Count - preflight.TooLarge.Count, preflight.TotalBytes, preflight);
        }, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Incremental update of a finished backup: copies (and verifies) only the files of the library that the backup
    /// does not have yet, next to the ones already there. Files already in the backup are never overwritten, moved or
    /// deleted, even if they are no longer in the library. If it is interrupted, running it again picks up where it
    /// stopped (copies already made are recognised by their SHA-256).
    /// </summary>
    public async Task<OriginalsBackupResult> UpdateAsync(string backupFolder, bool acceptIncomplete, IProgress<OriginalsProgress>? progress, CancellationToken ct)
    {
        var library = _context.RequireCurrent();
        var folder = Path.GetFullPath(backupFolder);
        var manifest = RequireUpdatable(library, folder);
        var plan = await PlanAsync(library, ct).ConfigureAwait(false);
        var (toCopy, kept, carried) = SplitForUpdate(folder, manifest, plan);
        var preflight = Preflight(library, toCopy, Path.GetDirectoryName(folder)!, existingBytes: 0);
        if (preflight.DestinationProblem is not null) throw new OriginalsBackupBlockedException("The destination cannot be inside the library or inside a folder that holds originals.", preflight);
        if (!preflight.EnoughSpace) throw new OriginalsBackupBlockedException("There is not enough free space in the destination.", preflight);
        if (!preflight.CanBeComplete && !acceptIncomplete) throw new OriginalsBackupBlockedException("Some originals are not available, so the backup would not be complete.", preflight);

        var skip = preflight.Unavailable.Concat(preflight.TooLarge).Select(i => i.Path).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var work = toCopy.Where(e => !skip.Contains(e.RelativePath)).ToList();
        var files = new List<OriginalsManifestFile>(kept);
        var added = new List<OriginalsPlanEntry>();
        var issues = new List<OriginalsManifestIssue>(carried.Concat(preflight.Unavailable).Concat(preflight.TooLarge));
        int copied = 0, present = 0, done = 0;
        long bytes = 0;
        string? fatal = null;
        var cancelled = false;
        CleanTemporaries(folder);

        OriginalsManifest Manifest(bool complete, bool updated) => manifest with
        {
            UpdatedUtc = updated ? _clock.UtcNow : manifest.UpdatedUtc,
            AppVersion = BackupService.AppVersion,
            Complete = complete,
            AssetCount = files.Select(f => f.AssetId).Distinct().Count(),
            PhotoCount = manifest.PhotoCount + added.Count(e => e.Role == FileRole.Original && e.MediaType == MediaType.Photo),
            VideoCount = manifest.VideoCount + added.Count(e => e.Role == FileRole.Original && e.MediaType == MediaType.Video),
            FileCount = files.Count,
            TotalBytes = files.Sum(f => f.Size),
            Files = files.ToList(),
            Issues = issues.ToList(),
        };

        try
        {
            foreach (var entry in work)
            {
                ct.ThrowIfCancellationRequested();
                progress?.Report(new OriginalsProgress(done, work.Count, bytes, preflight.TotalBytes, entry.OriginalName));
                done++;
                var (file, issue, wasPresent, stop) = await CopyOneAsync(folder, entry, replaceStale: false, ct).ConfigureAwait(false);
                if (file is not null)
                {
                    files.Add(file);
                    added.Add(entry);
                    bytes += file.Size;
                    if (wasPresent) present++;
                    else copied++;
                }

                if (issue is not null) issues.Add(issue);
                if (stop is not null)
                {
                    fatal = stop;
                    break;
                }
            }
        }
        catch (OperationCanceledException)
        {
            cancelled = true;
        }

        progress?.Report(new OriginalsProgress(done, work.Count, bytes, preflight.TotalBytes, string.Empty));
        if (!Directory.Exists(folder))
        {
            return new OriginalsBackupResult(OriginalsBackupStatus.Incomplete, folder, copied, present, bytes, issues, fatal ?? "The destination is no longer available.");
        }

        // What was copied so far is recorded even when stopped: the backup stays valid, just not up to date.
        var complete = !cancelled && fatal is null && issues.Count == 0;
        TryWriteManifest(folder, Manifest(complete, updated: added.Count > 0 || complete));
        if (cancelled) return new OriginalsBackupResult(OriginalsBackupStatus.Cancelled, folder, copied, present, bytes, issues, null);
        if (fatal is not null) return new OriginalsBackupResult(OriginalsBackupStatus.Incomplete, folder, copied, present, bytes, issues, fatal);

        var result = folder;
        if (complete && folder.EndsWith(IncompleteSuffix, StringComparison.OrdinalIgnoreCase) && !Directory.Exists(folder[..^IncompleteSuffix.Length]))
        {
            result = folder[..^IncompleteSuffix.Length];
            Directory.Move(folder, result);
        }

        _logger.LogInformation("Originals backup updated: {Files} new files, {Bytes} bytes, {Issues} issues", added.Count, bytes, issues.Count);
        var status = complete ? OriginalsBackupStatus.Complete : acceptIncomplete ? OriginalsBackupStatus.PartialAccepted : OriginalsBackupStatus.Incomplete;
        return new OriginalsBackupResult(status, result, copied, present, bytes, issues, null);
    }

    private static OriginalsManifest RequireUpdatable(LibraryInfo library, string folder)
    {
        if (folder.EndsWith(PartialSuffix, StringComparison.OrdinalIgnoreCase)) throw new ArgumentException("This backup is unfinished: resume it instead.", nameof(folder));
        var manifest = ReadManifest(folder) ?? throw new InvalidDataException("The folder has no backup manifest.");
        if (manifest.LibraryId != library.Id) throw new InvalidOperationException("This backup belongs to another library.");
        return manifest;
    }

    /// <summary>
    /// Splits the library plan into the files the backup already holds and the ones still to copy, each with a
    /// destination that does not clash with anything already in the backup. A file counts as held when the manifest
    /// has the same asset, role and name with the catalog's size and content hash, and the copy is still on disk with
    /// that size. Everything the manifest lists is kept, including files the library no longer has.
    /// </summary>
    private static (List<OriginalsPlanEntry> ToCopy, List<OriginalsManifestFile> Kept, List<OriginalsManifestIssue> Carried) SplitForUpdate(
        string folder, OriginalsManifest manifest, IReadOnlyList<OriginalsPlanEntry> plan)
    {
        var byKey = manifest.Files.GroupBy(f => (f.AssetId, f.Role, Name: f.OriginalName.ToUpperInvariant())).ToDictionary(g => g.Key, g => g.ToList());
        var taken = manifest.Files.Select(f => f.Path).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var toCopy = new List<OriginalsPlanEntry>();
        var carried = new List<OriginalsManifestIssue>();
        foreach (var e in plan)
        {
            var held = byKey.TryGetValue((e.AssetUid, e.Role, e.OriginalName.ToUpperInvariant()), out var candidates)
                ? candidates.FirstOrDefault(f => f.Size == e.CatalogSize
                    && (string.IsNullOrEmpty(e.CatalogSha256) || string.Equals(f.Sha256, e.CatalogSha256, StringComparison.OrdinalIgnoreCase) || !f.MatchesCatalogHash)
                    && TryResolve(folder, f.Path, out var p) && new FileInfo(p) is { Exists: true } info && info.Length == f.Size)
                : null;
            if (held is not null)
            {
                if (!held.MatchesCatalogHash) carried.Add(Issue(e, OriginalsIssueKind.ChangedSinceImport) with { Path = held.Path });
                continue;
            }

            // A new file goes to its planned place, or the first free " (n)" next to it. A file already on disk there
            // that the manifest does not list (an interrupted update) is reused only if identical: CopyOneAsync checks.
            var slash = e.RelativePath.LastIndexOf('/') + 1;
            var (dir, name) = (e.RelativePath[..slash], e.RelativePath[slash..]);
            var relative = e.RelativePath;
            for (var n = 2; taken.Contains(relative); n++) relative = dir + Path.GetFileNameWithoutExtension(name) + $" ({n})" + Path.GetExtension(name);
            taken.Add(relative);
            toCopy.Add(e with { RelativePath = relative });
        }

        return (toCopy, manifest.Files.ToList(), carried);
    }

    // ------------------------------------------------------------------------------------------------ internals

    private async Task<IReadOnlyList<OriginalsPlanEntry>> PlanAsync(LibraryInfo library, CancellationToken ct)
    {
        var rows = await _assets.GetAllOriginalFilesAsync(ct).ConfigureAwait(false);
        return OriginalsBackupPlanner.Plan(rows, library.Resolve);
    }

    private OriginalsPreflight Preflight(LibraryInfo library, IReadOnlyList<OriginalsPlanEntry> plan, string? destination, long existingBytes)
    {
        var maxFile = destination is null ? null : _volumes.MaxFileBytes(destination);
        var unavailable = new List<OriginalsManifestIssue>();
        var tooLarge = new List<OriginalsManifestIssue>();
        long total = 0;
        foreach (var e in plan)
        {
            long size;
            try
            {
                var info = new FileInfo(e.SourcePath);
                if (!info.Exists)
                {
                    unavailable.Add(Issue(e, OriginalsIssueKind.SourceMissing));
                    continue;
                }

                size = info.Length;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
            {
                unavailable.Add(Issue(e, OriginalsIssueKind.SourceMissing));
                continue;
            }

            if (maxFile is { } max && size > max)
            {
                tooLarge.Add(Issue(e, OriginalsIssueKind.TooLargeForDestination));
                continue;
            }

            total += size;
        }

        string? problem = null;
        if (destination is not null)
        {
            if (PathSafety.IsUnder(destination, library.RootPath)) problem = "inside-library";
            else if (plan.Any(e => Path.GetDirectoryName(e.SourcePath) is { } dir && PathSafety.IsUnder(destination, dir))) problem = "inside-source";
        }

        var assets = plan.GroupBy(e => e.AssetUid).Select(g => g.First()).ToList();
        return new OriginalsPreflight
        {
            AssetCount = assets.Count,
            PhotoCount = assets.Count(a => a.MediaType == MediaType.Photo),
            VideoCount = assets.Count(a => a.MediaType == MediaType.Video),
            FileCount = plan.Count,
            TotalBytes = total,
            UndatedAssets = assets.Count(a => OriginalsBackupPlanner.FolderFor(a.CaptureLocal, a.DateSource) == OriginalsBackupPlanner.UndatedFolder),
            Unavailable = unavailable,
            TooLarge = tooLarge,
            FreeBytes = destination is null ? null : _volumes.FreeBytes(destination) is { } free ? free + existingBytes : null,
            RequiredBytes = total + SpaceMargin,
            DestinationProblem = problem,
        };
    }

    private static void Guard(OriginalsPreflight preflight, bool acceptIncomplete)
    {
        if (preflight.DestinationProblem is not null) throw new OriginalsBackupBlockedException("The destination cannot be inside the library or inside a folder that holds originals.", preflight);
        if (preflight.FileCount == 0) throw new OriginalsBackupBlockedException("The library has no files to back up.", preflight);
        if (!preflight.EnoughSpace) throw new OriginalsBackupBlockedException("There is not enough free space in the destination.", preflight);
        if (!preflight.CanBeComplete && !acceptIncomplete) throw new OriginalsBackupBlockedException("Some originals are not available, so the backup would not be complete.", preflight);
    }

    private async Task<OriginalsBackupResult> RunAsync(LibraryInfo library, IReadOnlyList<OriginalsPlanEntry> plan, OriginalsPreflight preflight, string workDir,
        DateTime createdUtc, bool acceptIncomplete, IProgress<OriginalsProgress>? progress, CancellationToken ct)
    {
        var skip = preflight.Unavailable.Concat(preflight.TooLarge).ToDictionary(i => i.Path, StringComparer.OrdinalIgnoreCase);
        var files = new List<OriginalsManifestFile>();
        var issues = new List<OriginalsManifestIssue>(skip.Values);
        int copied = 0, present = 0, done = 0;
        long bytes = 0;
        string? fatal = null;
        var cancelled = false;

        OriginalsManifest Manifest(bool complete)
        {
            var copiedPaths = files.Select(f => f.Path).ToHashSet(StringComparer.Ordinal);
            var originals = plan.Where(e => e.Role == FileRole.Original && copiedPaths.Contains(e.RelativePath)).ToList();
            return new OriginalsManifest
            {
            LibraryId = library.Id,
            LibraryName = library.Name,
            CreatedUtc = createdUtc,
            AppVersion = BackupService.AppVersion,
            Complete = complete,
            AssetCount = files.Select(f => f.AssetId).Distinct().Count(),
            PhotoCount = originals.Count(e => e.MediaType == MediaType.Photo),
            VideoCount = originals.Count(e => e.MediaType == MediaType.Video),
            FileCount = files.Count,
            TotalBytes = files.Sum(f => f.Size),
            Files = files.ToList(),
            Issues = issues.ToList(),
            };
        }

        // Marks the folder as an unfinished backup of this library from the first moment.
        WriteManifest(workDir, new OriginalsManifest { LibraryId = library.Id, LibraryName = library.Name, CreatedUtc = createdUtc, AppVersion = BackupService.AppVersion, Issues = issues.ToList() });
        CleanTemporaries(workDir);

        try
        {
            foreach (var entry in plan)
            {
                ct.ThrowIfCancellationRequested();
                progress?.Report(new OriginalsProgress(done, plan.Count, bytes, preflight.TotalBytes, entry.OriginalName));
                done++;
                if (skip.ContainsKey(entry.RelativePath)) continue;

                var (file, issue, wasPresent, stop) = await CopyOneAsync(workDir, entry, replaceStale: true, ct).ConfigureAwait(false);
                if (file is not null)
                {
                    files.Add(file);
                    bytes += file.Size;
                    if (wasPresent) present++;
                    else copied++;
                }

                if (issue is not null) issues.Add(issue);
                if (stop is not null)
                {
                    fatal = stop;
                    break;
                }
            }
        }
        catch (OperationCanceledException)
        {
            cancelled = true;
        }

        progress?.Report(new OriginalsProgress(done, plan.Count, bytes, preflight.TotalBytes, string.Empty));

        // The destination may be gone (disconnected drive): then there is nowhere to write a manifest.
        if (!Directory.Exists(workDir))
        {
            return new OriginalsBackupResult(OriginalsBackupStatus.Incomplete, workDir, copied, present, bytes, issues, fatal ?? "The destination is no longer available.");
        }

        if (cancelled || fatal is not null)
        {
            TryWriteManifest(workDir, Manifest(false));
            return new OriginalsBackupResult(cancelled ? OriginalsBackupStatus.Cancelled : OriginalsBackupStatus.Incomplete, workDir, copied, present, bytes, issues, fatal);
        }

        if (issues.Count == 0)
        {
            WriteManifest(workDir, Manifest(true));
            var finalDir = workDir[..^PartialSuffix.Length];
            Directory.Move(workDir, finalDir);
            _logger.LogInformation("Originals backup complete: {Files} files, {Bytes} bytes", files.Count, bytes);
            return new OriginalsBackupResult(OriginalsBackupStatus.Complete, finalDir, copied, present, bytes, issues, null);
        }

        WriteManifest(workDir, Manifest(false));
        if (!acceptIncomplete)
        {
            // Issues appeared while copying (a file changed, a drive went away): keep it resumable and say so.
            return new OriginalsBackupResult(OriginalsBackupStatus.Incomplete, workDir, copied, present, bytes, issues, null);
        }

        var partialDir = workDir[..^PartialSuffix.Length] + IncompleteSuffix;
        Directory.Move(workDir, partialDir);
        _logger.LogWarning("Originals backup finished incomplete at the user's request: {Files} files, {Issues} issues", files.Count, issues.Count);
        return new OriginalsBackupResult(OriginalsBackupStatus.PartialAccepted, partialDir, copied, present, bytes, issues, null);
    }

    private async Task<(OriginalsManifestFile? File, OriginalsManifestIssue? Issue, bool WasPresent, string? Stop)> CopyOneAsync(string workDir, OriginalsPlanEntry entry, bool replaceStale, CancellationToken ct)
    {
        if (!TryResolve(workDir, entry.RelativePath, out var destination)) return (null, Issue(entry, OriginalsIssueKind.CopyFailed), false, null);
        var temporary = destination + PathSafety.PartialSuffix;
        try
        {
            var before = new FileInfo(entry.SourcePath);
            if (!before.Exists) return (null, Issue(entry, OriginalsIssueKind.SourceMissing), false, null);
            var (size, modified) = (before.Length, before.LastWriteTimeUtc);
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);

            string sourceHash;
            var wasPresent = false;
            if (File.Exists(destination))
            {
                // Resuming: an existing file counts only if it is still identical to the source.
                sourceHash = await FileHashing.Sha256Async(entry.SourcePath, ct).ConfigureAwait(false);
                var existing = new FileInfo(destination).Length == size ? await FileHashing.Sha256Async(destination, ct).ConfigureAwait(false) : null;
                if (existing == sourceHash)
                {
                    wasPresent = true;
                }
                else if (replaceStale)
                {
                    File.Delete(destination); // our own unfinished working folder: a stale or damaged copy is replaced
                }
                else
                {
                    // A finished backup: a different file is already there and is never overwritten.
                    return (null, Issue(entry, OriginalsIssueKind.CopyFailed), false, null);
                }
            }
            else
            {
                sourceHash = string.Empty;
            }

            if (!wasPresent)
            {
                if (File.Exists(temporary)) File.Delete(temporary);
                sourceHash = await FileHashing.CopyWithHashAsync(entry.SourcePath, temporary, ct).ConfigureAwait(false);
                // Read the copy back from the destination: what was written must hash the same as what was read.
                var copyHash = await FileHashing.Sha256Async(temporary, ct).ConfigureAwait(false);
                if (copyHash != sourceHash || new FileInfo(temporary).Length != size)
                {
                    File.Delete(temporary);
                    return (null, Issue(entry, OriginalsIssueKind.CopyFailed), false, null);
                }
            }

            var after = new FileInfo(entry.SourcePath);
            if (!after.Exists || after.Length != size || after.LastWriteTimeUtc != modified)
            {
                if (File.Exists(temporary)) File.Delete(temporary);
                return (null, Issue(entry, OriginalsIssueKind.ChangedDuringCopy), false, null);
            }

            if (!wasPresent) File.Move(temporary, destination, overwrite: false);
            var matches = string.IsNullOrEmpty(entry.CatalogSha256) || string.Equals(entry.CatalogSha256, sourceHash, StringComparison.OrdinalIgnoreCase);
            var file = new OriginalsManifestFile
            {
                AssetId = entry.AssetUid,
                Role = entry.Role,
                OriginalName = entry.OriginalName,
                Path = entry.RelativePath,
                Size = size,
                Sha256 = sourceHash,
                CaptureLocal = OriginalsBackupPlanner.FolderFor(entry.CaptureLocal, entry.DateSource) == OriginalsBackupPlanner.UndatedFolder
                    ? string.Empty
                    : entry.CaptureLocal.ToString("yyyy-MM-ddTHH:mm:ss", CultureInfo.InvariantCulture),
                DateSource = entry.DateSource,
                MatchesCatalogHash = matches,
            };
            return (file, matches ? null : Issue(entry, OriginalsIssueKind.ChangedSinceImport), wasPresent, null);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // File names are not logged: they can be private. The exception type and role are enough to diagnose.
            _logger.LogWarning("Originals backup: a {Role} file could not be copied ({Error})", entry.Role, ex.GetType().Name);
            if (!Directory.Exists(workDir)) return (null, null, false, "The destination is no longer available.");
            if (_volumes.FreeBytes(workDir) is { } free && free < entry.CatalogSize) return (null, null, false, "The destination ran out of space.");
            if (!File.Exists(entry.SourcePath)) return (null, Issue(entry, OriginalsIssueKind.SourceMissing), false, null);
            return (null, Issue(entry, OriginalsIssueKind.CopyFailed), false, null);
        }
    }

    private static OriginalsManifestIssue Issue(OriginalsPlanEntry e, OriginalsIssueKind kind) =>
        new() { AssetId = e.AssetUid, Role = e.Role, OriginalName = e.OriginalName, Path = e.RelativePath, Kind = kind };

    /// <summary>Maps a manifest path to a file inside <paramref name="root"/>; rejects anything that would escape it.</summary>
    private static bool TryResolve(string root, string relative, out string path)
    {
        path = string.Empty;
        if (string.IsNullOrWhiteSpace(relative) || Path.IsPathRooted(relative) || relative.Split('/', '\\').Any(p => p is ".." or "." or "")) return false;
        path = Path.GetFullPath(Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar)));
        return PathSafety.IsUnder(path, root);
    }

    private static OriginalsManifest? ReadManifest(string folder)
    {
        var path = Path.Combine(folder, OriginalsManifest.FileName);
        return File.Exists(path) ? OriginalsManifest.Parse(File.ReadAllText(path)) : null;
    }

    private static void WriteManifest(string folder, OriginalsManifest manifest)
    {
        var path = Path.Combine(folder, OriginalsManifest.FileName);
        var temporary = path + PathSafety.PartialSuffix;
        File.WriteAllText(temporary, manifest.ToJson());
        File.Move(temporary, path, overwrite: true);
    }

    private void TryWriteManifest(string folder, OriginalsManifest manifest)
    {
        try
        {
            WriteManifest(folder, manifest);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning("Originals backup: the manifest could not be updated ({Error})", ex.GetType().Name);
        }
    }

    /// <summary>Half-written files of an interrupted run are never mistaken for originals: they are removed.</summary>
    private static void CleanTemporaries(string workDir)
    {
        foreach (var leftover in Directory.EnumerateFiles(workDir, "*" + PathSafety.PartialSuffix, SearchOption.AllDirectories)) File.Delete(leftover);
    }
}
