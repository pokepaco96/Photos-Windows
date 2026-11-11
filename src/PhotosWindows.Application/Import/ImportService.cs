using System.Collections.Concurrent;
using System.Diagnostics;
using Microsoft.Extensions.Logging;
using PhotosWindows.Application.Abstractions;
using PhotosWindows.Application.Common;
using PhotosWindows.Application.Library;
using PhotosWindows.Domain;

namespace PhotosWindows.Application.Import;

/// <summary>
/// Imports files into the open library. Guarantees:
/// originals are only ever read (never moved, modified or deleted);
/// managed copies are written to a temporary *.pwpartial file and renamed only after a complete, flushed copy;
/// every file has a persisted import item, so an interrupted job can be resumed;
/// a failure on one file never stops the job.
/// </summary>
public sealed class ImportService
{
    /// <summary>Paired motion clips longer than this are treated as normal videos.</summary>
    public static readonly TimeSpan MaxLiveClipDuration = TimeSpan.FromSeconds(4.5);

    private readonly ILibraryContext _context;
    private readonly IAssetRepository _assets;
    private readonly IImportJobRepository _jobs;
    private readonly IMetadataReader _metadata;
    private readonly IImageRenderer _renderer;
    private readonly IClock _clock;
    private readonly ISettingsStore _settings;
    private readonly ILogger<ImportService> _logger;
    private readonly SemaphoreSlim _single = new(1, 1);

    public ImportService(ILibraryContext context, IAssetRepository assets, IImportJobRepository jobs, IMetadataReader metadata, IImageRenderer renderer, IClock clock, ISettingsStore settings, ILogger<ImportService> logger)
    {
        _context = context;
        _assets = assets;
        _jobs = jobs;
        _metadata = metadata;
        _renderer = renderer;
        _clock = clock;
        _settings = settings;
        _logger = logger;
    }

    /// <summary>Raised (on a worker thread) after each asset is committed to the catalog.</summary>
    public event EventHandler<long>? AssetImported;

    public bool IsRunning => _single.CurrentCount == 0;

    /// <summary>Phase timings of the last (or running) job. Local only; contains no paths.</summary>
    public ImportDiagnostics Diagnostics { get; private set; } = new();

    /// <summary>Lists importable candidate files. Hidden/system folders, links and the library itself are skipped.</summary>
    public IReadOnlyList<string> Scan(IReadOnlyList<string> sources, bool recursive, CancellationToken cancellationToken) =>
        Scan(sources, recursive, null, cancellationToken);

    /// <summary>
    /// Lists importable candidate files, reporting (throttled) how many files were examined and found so far,
    /// so a long scan of a large or slow drive is visibly working.
    /// </summary>
    public IReadOnlyList<string> Scan(IReadOnlyList<string> sources, bool recursive, Action<int, int, string?>? onProgress, CancellationToken cancellationToken)
    {
        var examined = 0;
        var throttle = new ProgressThrottle(TimeSpan.FromMilliseconds(250));
        var library = _context.RequireCurrent();
        var options = new EnumerationOptions
        {
            RecurseSubdirectories = recursive,
            IgnoreInaccessible = true,
            AttributesToSkip = FileAttributes.Hidden | FileAttributes.System | FileAttributes.ReparsePoint,
            ReturnSpecialDirectories = false,
        };

        var result = new List<string>();
        foreach (var source in sources)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var full = Path.GetFullPath(source);
            if (File.Exists(full))
            {
                if (MediaFormats.IsCandidate(full)) result.Add(full);
                continue;
            }

            if (!Directory.Exists(full)) continue;
            if (PathSafety.IsUnder(full, library.CachePath) || PathSafety.IsUnder(full, library.BackupsPath))
            {
                continue;
            }

            foreach (var file in Directory.EnumerateFiles(full, "*", options))
            {
                cancellationToken.ThrowIfCancellationRequested();
                examined++;
                if (onProgress is not null && throttle.ShouldReport())
                {
                    onProgress(examined, result.Count, Path.GetFileName(Path.GetDirectoryName(file)));
                }

                if (!MediaFormats.IsCandidate(file)) continue;
                if (PathSafety.IsUnder(file, library.CachePath) || PathSafety.IsUnder(file, library.BackupsPath)) continue;
                if (file.EndsWith(PathSafety.PartialSuffix, StringComparison.OrdinalIgnoreCase)) continue;
                result.Add(file);
            }
        }

        onProgress?.Invoke(examined, result.Count, null);
        return result.Distinct(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal).ToList();
    }

    public async Task<ImportResult> ImportAsync(ImportRequest request, IProgress<ImportProgress>? progress, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        Diagnostics = new ImportDiagnostics();
        IReadOnlyList<string> files;
        using (Diagnostics.Measure(ImportPhase.Scanning))
        {
            var started = Stopwatch.StartNew();
            progress?.Report(new ImportProgress(0, 0, 0, 0, 0, 0, 0, null, TimeSpan.Zero, null) { Phase = ImportPhase.Scanning });
            files = await Task.Run(() => Scan(request.Sources, request.Recursive, (examined, found, folder) =>
                progress?.Report(new ImportProgress(0, found, 0, 0, 0, 0, 0, null, started.Elapsed, null)
                {
                    Phase = ImportPhase.Scanning,
                    Examined = examined,
                    CurrentFolder = folder,
                }), cancellationToken), cancellationToken).ConfigureAwait(false);
        }

        long jobId;
        using (Diagnostics.Measure(ImportPhase.Preparing))
        {
            progress?.Report(new ImportProgress(0, files.Count, 0, 0, 0, 0, 0, null, TimeSpan.Zero, null) { Phase = ImportPhase.Preparing });
            jobId = await _jobs.CreateJobAsync(request.Mode, request.ImportDuplicates, files, _clock.UtcNow, cancellationToken).ConfigureAwait(false);
        }

        return await RunJobAsync(jobId, progress, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Continues an interrupted or cancelled job with its pending items.</summary>
    public Task<ImportResult> ResumeAsync(long jobId, IProgress<ImportProgress>? progress, CancellationToken cancellationToken)
    {
        Diagnostics = new ImportDiagnostics();
        return RunJobAsync(jobId, progress, cancellationToken);
    }

    private async Task<ImportResult> RunJobAsync(long jobId, IProgress<ImportProgress>? progress, CancellationToken cancellationToken)
    {
        if (!await _single.WaitAsync(0, cancellationToken).ConfigureAwait(false))
        {
            throw new InvalidOperationException("Another import is already running.");
        }

        try
        {
            var library = _context.RequireCurrent();
            var job = await _jobs.GetJobAsync(jobId, cancellationToken).ConfigureAwait(false) ?? throw new InvalidOperationException("Import job not found.");
            await _jobs.SetJobStatusAsync(jobId, ImportJobStatus.Running, null, cancellationToken).ConfigureAwait(false);

            var all = await _jobs.GetItemsAsync(jobId, null, cancellationToken).ConfigureAwait(false);
            var pending = all.Where(i => i.Status == ImportItemStatus.Pending).ToList();
            var itemByPath = pending.ToDictionary(i => i.SourcePath, StringComparer.Ordinal);
            var diag = Diagnostics;
            IReadOnlyList<ImportUnit> units;
            using (diag.Measure(ImportPhase.Planning))
            {
                units = ImportPlanner.Plan(pending.Select(i => i.SourcePath));
            }

            var counters = new Counters(all);
            var stopwatch = Stopwatch.StartNew();
            var newIds = new ConcurrentBag<long>();
            var claimedHashes = new ConcurrentDictionary<string, byte>(StringComparer.Ordinal);
            var settings = _settings.Load();

            // Files that ended up in no unit (e.g. a sidecar without its image).
            var planned = new HashSet<string>(units.SelectMany(u => u.AllFiles()), StringComparer.Ordinal);
            var orphans = pending.Where(p => !planned.Contains(p.SourcePath))
                .Select(o => new ImportItemUpdate(o.Id, ImportItemStatus.Skipped, null, "Sidecar without a matching photo or video.", null)).ToList();
            await _jobs.UpdateItemsAsync(orphans, cancellationToken).ConfigureAwait(false);
            foreach (var _ in orphans) counters.Record(ImportItemStatus.Skipped);

            // Item states are written in batches (one transaction per 64 items, or per unit when the job ends): with
            // many small files one commit per item was a large share of the import time. If the app stops before a
            // batch is written, those items are pending again; resuming finds the asset already catalogued (same
            // stored path or same SHA-256) and records it as already in the library or a duplicate, never twice.
            var itemBatch = new ItemBatch(_jobs, 64);

            var throttle = new ProgressThrottle(TimeSpan.FromMilliseconds(200));
            void Report(string? current, bool force = false)
            {
                if (progress is null) return;
                // Coalesced: at most ~5 reports a second reach the UI, whatever the file rate.
                if (!force && !throttle.ShouldReport()) return;
                var processed = counters.Processed;
                var elapsed = stopwatch.Elapsed;
                var doneThisRun = processed - counters.ProcessedAtStart;
                TimeSpan? eta = doneThisRun > 3 ? TimeSpan.FromTicks(elapsed.Ticks / doneThisRun * Math.Max(0, counters.Total - processed)) : null;
                progress.Report(new ImportProgress(jobId, counters.Total, processed, counters.Imported, counters.Duplicates, counters.Skipped, counters.Failed, current, elapsed, eta)
                {
                    Phase = processed >= counters.Total ? ImportPhase.Finishing : ImportPhase.CopyAndHash,
                    BytesProcessed = diag.BytesHashed,
                });
            }

            Report(null, force: true);
            var status = ImportJobStatus.Completed;
            try
            {
                var parallel = new ParallelOptions { MaxDegreeOfParallelism = Math.Clamp(settings.MaxBackgroundWorkers, 1, 8), CancellationToken = cancellationToken };
                await Parallel.ForEachAsync(units, parallel, async (unit, ct) =>
                {
                    Report(Path.GetFileName(unit.Primary));
                    var outcome = await ImportUnitAsync(library, job, unit, claimedHashes, ct).ConfigureAwait(false);
                    foreach (var (path, itemStatus, assetId, message, target) in outcome)
                    {
                        if (!itemByPath.TryGetValue(path, out var item)) continue;
                        itemBatch.Add(new ImportItemUpdate(item.Id, itemStatus, assetId, message, target));
                        counters.Record(itemStatus);
                    }

                    // Persist even if the job is being cancelled, so the item is not redone on resume.
                    using (diag.Measure(ImportPhase.ItemUpdate))
                    {
                        await itemBatch.FlushIfFullAsync().ConfigureAwait(false);
                    }

                    foreach (var imported in outcome.Where(o => o.Status == ImportItemStatus.Imported && o.AssetId is not null))
                    {
                        newIds.Add(imported.AssetId!.Value);
                        AssetImported?.Invoke(this, imported.AssetId.Value);
                    }

                    Report(null);
                }).ConfigureAwait(false);

                if (counters.Failed > 0) status = ImportJobStatus.CompletedWithErrors;
            }
            catch (OperationCanceledException)
            {
                status = ImportJobStatus.Cancelled;
            }
            finally
            {
                // Also on an unexpected error: what was already done must not be redone on resume.
                await itemBatch.FlushAsync().ConfigureAwait(false);
            }

            using (diag.Measure(ImportPhase.ItemUpdate))
            {
                await itemBatch.FlushAsync().ConfigureAwait(false);
            }

            await _jobs.RefreshCountersAsync(jobId, CancellationToken.None).ConfigureAwait(false);
            await _jobs.SetJobStatusAsync(jobId, status, _clock.UtcNow, CancellationToken.None).ConfigureAwait(false);
            Report(null, force: true);
            _logger.LogInformation("Import {JobId} phases: {Phases}", jobId, diag);
            _logger.LogInformation("Import {JobId} finished: {Status}, {Imported} imported, {Dup} duplicates, {Skipped} skipped, {Failed} failed in {Elapsed}",
                jobId, status, counters.Imported, counters.Duplicates, counters.Skipped, counters.Failed, stopwatch.Elapsed);
            return new ImportResult(jobId, status, counters.Imported, counters.Duplicates, counters.Skipped, counters.Failed, newIds.ToList());
        }
        finally
        {
            _single.Release();
        }
    }

    private sealed record ItemOutcome(string Path, ImportItemStatus Status, long? AssetId, string? Message, string? Target);

    private async Task<List<ItemOutcome>> ImportUnitAsync(LibraryInfo library, ImportJob job, ImportUnit unit, ConcurrentDictionary<string, byte> claimedHashes, CancellationToken ct)
    {
        var outcomes = new List<ItemOutcome>();
        var created = new List<string>(); // managed copies to roll back if the catalog insert fails
        try
        {
            var primary = await PrepareFileAsync(library, job, unit.Primary, FileRole.Original, claimedHashes, created, ct).ConfigureAwait(false);
            if (primary.Status is not null)
            {
                outcomes.Add(new ItemOutcome(unit.Primary, primary.Status.Value, primary.ExistingAssetId, primary.Message, null));
                // Companions of a skipped primary are imported on their own.
                foreach (var companion in new[] { unit.RawPair, unit.LiveVideoCandidate }.OfType<string>())
                {
                    outcomes.AddRange(await ImportUnitAsync(library, job, new ImportUnit(companion, null, null, null), claimedHashes, ct).ConfigureAwait(false));
                }

                if (unit.Sidecar is not null)
                {
                    outcomes.Add(new ItemOutcome(unit.Sidecar, ImportItemStatus.Skipped, null, "Its photo was not imported.", null));
                }

                return outcomes;
            }

            var meta = primary.Metadata!;
            var files = new List<AssetFile> { primary.File! };
            var folderHint = primary.Target is { } t ? Path.GetDirectoryName(t) : null;
            var companionsToImportSeparately = new List<string>();

            if (unit.RawPair is not null)
            {
                var raw = await PrepareFileAsync(library, job, unit.RawPair, FileRole.RawPair, claimedHashes, created, ct, folderHint: folderHint).ConfigureAwait(false);
                if (raw.File is not null)
                {
                    files.Add(raw.File);
                    outcomes.Add(new ItemOutcome(unit.RawPair, ImportItemStatus.Attached, null, "RAW file paired with " + Path.GetFileName(unit.Primary), raw.Target));
                }
                else
                {
                    outcomes.Add(new ItemOutcome(unit.RawPair, raw.Status ?? ImportItemStatus.Failed, raw.ExistingAssetId, raw.Message, null));
                }
            }

            var hasLive = false;
            if (unit.LiveVideoCandidate is not null)
            {
                var videoMeta = _metadata.Read(unit.LiveVideoCandidate, FormatFamily.Video);
                var idsMatch = meta.ContentIdentifier is not null && string.Equals(meta.ContentIdentifier, videoMeta.ContentIdentifier, StringComparison.Ordinal);
                var isShort = videoMeta.Duration is { } d && d <= MaxLiveClipDuration;
                if (idsMatch || isShort)
                {
                    var live = await PrepareFileAsync(library, job, unit.LiveVideoCandidate, FileRole.LiveVideo, claimedHashes, created, ct, folderHint: folderHint).ConfigureAwait(false);
                    if (live.File is not null)
                    {
                        files.Add(live.File);
                        hasLive = true;
                        outcomes.Add(new ItemOutcome(unit.LiveVideoCandidate, ImportItemStatus.Attached, null, "Motion clip paired with " + Path.GetFileName(unit.Primary), live.Target));
                    }
                    else
                    {
                        outcomes.Add(new ItemOutcome(unit.LiveVideoCandidate, live.Status ?? ImportItemStatus.Failed, live.ExistingAssetId, live.Message, null));
                    }
                }
                else
                {
                    companionsToImportSeparately.Add(unit.LiveVideoCandidate);
                }
            }

            var keywords = meta.Keywords.ToList();
            string? title = meta.Title, description = meta.Description;
            var rating = meta.Rating ?? 0;
            var capture = meta.CaptureDate;
            if (unit.Sidecar is not null)
            {
                var side = _metadata.ReadSidecar(unit.Sidecar);
                title = side.Title ?? title;
                description = side.Description ?? description;
                rating = side.Rating ?? rating;
                capture ??= side.CaptureDate;
                foreach (var k in side.Keywords.Where(k => !keywords.Contains(k, StringComparer.OrdinalIgnoreCase))) keywords.Add(k);
                var sidecar = await PrepareFileAsync(library, job, unit.Sidecar, FileRole.Sidecar, claimedHashes, created, ct, sidecar: true, folderHint: folderHint).ConfigureAwait(false);
                if (sidecar.File is not null)
                {
                    files.Add(sidecar.File);
                    outcomes.Add(new ItemOutcome(unit.Sidecar, ImportItemStatus.Attached, null, "XMP sidecar", sidecar.Target));
                }
            }

            var info = new FileInfo(unit.Primary);
            var family = primary.Family;
            var mediaType = family == FormatFamily.Video ? MediaType.Video : MediaType.Photo;
            var (width, height) = (meta.Width ?? 0, meta.Height ?? 0);
            if ((width == 0 || height == 0) && mediaType == MediaType.Photo && _renderer.CanDecode(family))
            {
                (width, height) = ProbeDimensions(unit.Primary, family);
            }

            var asset = new Asset
            {
                MediaType = mediaType,
                CaptureDate = capture ?? DateParsing.Fallback(info.Name, info.LastWriteTimeUtc, _clock.LocalZone),
                ImportedUtc = _clock.UtcNow,
                FileModifiedUtc = info.LastWriteTimeUtc,
                PixelWidth = width,
                PixelHeight = height,
                Orientation = meta.Orientation,
                Duration = meta.Duration,
                FileName = info.Name,
                Format = MediaFormats.FromExtension(unit.Primary)?.Name ?? family.ToString(),
                FileSize = info.Length,
                Camera = meta.Camera,
                Location = meta.Location,
                Title = title,
                Description = description,
                Rating = Math.Clamp(rating, 0, 5),
                HasLiveVideo = hasLive,
                HasRawPair = files.Any(f => f.Role == FileRole.RawPair) || family == FormatFamily.Raw,
                ThumbnailState = ThumbnailState.Pending,
            };

            long id;
            using (Diagnostics.Measure(ImportPhase.CatalogInsert))
            id = await _assets.InsertAsync(new NewAssetRecord(asset, files, keywords), ct).ConfigureAwait(false);
            created.Clear(); // committed: copies now belong to the catalog
            outcomes.Insert(0, new ItemOutcome(unit.Primary, ImportItemStatus.Imported, id, meta.Warnings.Count > 0 ? string.Join("; ", meta.Warnings.Take(3)) : null, primary.Target));

            foreach (var separate in companionsToImportSeparately)
            {
                outcomes.AddRange(await ImportUnitAsync(library, job, new ImportUnit(separate, null, null, null), claimedHashes, ct).ConfigureAwait(false));
            }

            return outcomes;
        }
        catch (OperationCanceledException)
        {
            RollBack(created);
            throw;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or MediaDecodeException or ArgumentException or System.Data.Common.DbException)
        {
            RollBack(created);
            _logger.LogWarning(ex, "Import failed for {File}", Path.GetFileName(unit.Primary));
            var failed = outcomes.Select(o => o.Path).ToHashSet(StringComparer.Ordinal);
            foreach (var path in unit.AllFiles().Where(p => !failed.Contains(p)))
            {
                outcomes.Add(new ItemOutcome(path, ImportItemStatus.Failed, null, ex.Message, null));
            }

            return outcomes.Select(o => o.Status == ImportItemStatus.Attached ? o with { Status = ImportItemStatus.Failed, Message = ex.Message } : o).ToList();
        }
    }

    private sealed record PreparedFile(AssetFile? File, MediaMetadata? Metadata, FormatFamily Family, ImportItemStatus? Status, string? Message, long? ExistingAssetId, string? Target);

    /// <summary>Managed copies are organised as originals/yyyy/MM by capture date; companions follow their primary.</summary>
    private string ManagedFolder(LibraryInfo library, string path, MediaMetadata? metadata, FileInfo info)
    {
        var date = metadata?.CaptureDate?.LocalDateTime
            ?? (DateParsing.TryParseFromFileName(info.Name, out var fromName) ? fromName : info.LastWriteTime);
        return Path.Combine(library.OriginalsPath,
            date.ToString("yyyy", System.Globalization.CultureInfo.InvariantCulture),
            date.ToString("MM", System.Globalization.CultureInfo.InvariantCulture));
    }

    private async Task<PreparedFile> PrepareFileAsync(LibraryInfo library, ImportJob job, string path, FileRole role, ConcurrentDictionary<string, byte> claimedHashes, List<string> created, CancellationToken ct, bool sidecar = false, string? folderHint = null)
    {
        var info = new FileInfo(path);
        if (!info.Exists)
        {
            return new PreparedFile(null, null, FormatFamily.Unknown, ImportItemStatus.Failed, "The file is no longer available (moved, deleted or the drive was disconnected).", null, null);
        }

        // Signature check: never trust the extension alone.
        var diag = Diagnostics;
        var header = new byte[MediaFormats.SniffLength];
        int read;
        using (diag.Measure(ImportPhase.Signature))
        await using (var s = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            read = await s.ReadAtLeastAsync(header, header.Length, throwOnEndOfStream: false, ct).ConfigureAwait(false);
        }

        var family = MediaFormats.Sniff(header.AsSpan(0, read), Path.GetExtension(path).ToLowerInvariant());
        if (!MediaFormats.SignatureMatchesExtension(family, path))
        {
            return new PreparedFile(null, null, family, ImportItemStatus.Unsupported, info.Length == 0 ? "The file is empty." : "The file content does not match its extension or the format is not supported.", null, null);
        }

        var insideOriginals = PathSafety.IsUnder(path, library.OriginalsPath);
        var managed = job.Mode == ImportMode.CopyToLibrary || insideOriginals;
        var storedPathIfInPlace = insideOriginals ? Path.GetRelativePath(library.RootPath, path) : path;

        // Already catalogued at this exact location?
        if (!managed || insideOriginals)
        {
            AssetFile? existing;
            using (diag.Measure(ImportPhase.LookupExisting))
            {
                existing = await _assets.FindFileByStoredPathAsync(storedPathIfInPlace, ct).ConfigureAwait(false);
            }

            if (existing is not null)
            {
                return new PreparedFile(null, null, family, ImportItemStatus.AlreadyInLibrary, "This file is already in the library.", existing.AssetId, null);
            }
        }

        MediaMetadata? metadata = null;
        if (role == FileRole.Original)
        {
            using (diag.Measure(ImportPhase.Metadata)) metadata = _metadata.Read(path, family);
        }

        string sha;
        string storedPath;
        string? target = null;
        if (managed && !insideOriginals)
        {
            var folder = folderHint ?? ManagedFolder(library, path, metadata, info);
            Directory.CreateDirectory(folder);
            var final = PathSafety.UniquePath(folder, info.Name);
            var partial = final + PathSafety.PartialSuffix;
            using (diag.Measure(ImportPhase.CopyAndHash))
            {
                sha = await FileHashing.CopyWithHashAsync(path, partial, ct).ConfigureAwait(false);
            }

            diag.AddBytes(info.Length);
            created.Add(partial);
            if (!sidecar && !job.ImportDuplicates && await CheckDuplicateAsync(sha, claimedHashes, ct).ConfigureAwait(false) is { IsDuplicate: true } dup)
            {
                RollBack([partial]);
                created.Remove(partial);
                return new PreparedFile(null, null, family, ImportItemStatus.Duplicate, "An identical file is already in the library.", dup.ExistingAssetId, null);
            }

            // The .pwpartial file reserved the name; only recompute it if another writer took it meanwhile.
            for (var attempt = 0; ; attempt++)
            {
                try
                {
                    File.Move(partial, final, overwrite: false);
                    break;
                }
                catch (IOException) when (File.Exists(final) && attempt < 5)
                {
                    final = PathSafety.UniquePath(folder, info.Name);
                }
            }

            created.Remove(partial);
            created.Add(final);
            storedPath = Path.GetRelativePath(library.RootPath, final);
            target = final;
        }
        else
        {
            using (diag.Measure(ImportPhase.Hash))
            {
                sha = await FileHashing.Sha256Async(path, ct).ConfigureAwait(false);
            }

            diag.AddBytes(info.Length);
            if (!sidecar && !job.ImportDuplicates && await CheckDuplicateAsync(sha, claimedHashes, ct).ConfigureAwait(false) is { IsDuplicate: true } dup)
            {
                return new PreparedFile(null, null, family, ImportItemStatus.Duplicate, "An identical file is already in the library.", dup.ExistingAssetId, null);
            }

            storedPath = storedPathIfInPlace;
        }

        var file = new AssetFile
        {
            Role = role,
            StorageMode = managed ? StorageMode.Managed : StorageMode.Referenced,
            StoredPath = storedPath,
            SizeBytes = info.Length,
            Sha256 = sha,
            FileModifiedUtc = info.LastWriteTimeUtc,
            Format = MediaFormats.FromExtension(path)?.Name ?? family.ToString(),
            IsAvailable = true,
            LastVerifiedUtc = _clock.UtcNow,
        };

        return new PreparedFile(file, metadata, family, null, null, null, target);
    }

    /// <summary>Returns the id of an asset that already owns this content, or null. Also deduplicates within the running job.</summary>
    private async Task<(bool IsDuplicate, long? ExistingAssetId)> CheckDuplicateAsync(string sha, ConcurrentDictionary<string, byte> claimedHashes, CancellationToken ct)
    {
        IReadOnlyList<AssetFile> existing;
        using (Diagnostics.Measure(ImportPhase.DuplicateLookup))
        {
            existing = await _assets.FindFilesByHashAsync(sha, ct).ConfigureAwait(false);
        }

        if (existing.Count > 0) return (true, existing[0].AssetId);
        // Two identical files in the same batch: only the first one wins.
        return claimedHashes.TryAdd(sha, 0) ? (false, null) : (true, null);
    }

    private (int Width, int Height) ProbeDimensions(string path, FormatFamily family)
    {
        try
        {
            using var _ = Diagnostics.Measure(ImportPhase.Dimensions);
            return _renderer.ProbeSize(path, family) ?? (0, 0);
        }
        catch (Exception ex) when (ex is MediaDecodeException or IOException or UnauthorizedAccessException)
        {
            _logger.LogDebug(ex, "Could not probe dimensions of {File}", Path.GetFileName(path));
            return (0, 0);
        }
    }

    private void RollBack(IEnumerable<string> created)
    {
        foreach (var path in created.ToList())
        {
            try
            {
                if (File.Exists(path)) File.Delete(path);
            }
            catch (IOException ex)
            {
                _logger.LogWarning(ex, "Could not remove an incomplete copy; library verification will report it");
            }
            catch (UnauthorizedAccessException ex)
            {
                _logger.LogWarning(ex, "Could not remove an incomplete copy; library verification will report it");
            }
        }
    }

    /// <summary>Collects item updates from parallel workers and writes them in one transaction per batch.</summary>
    private sealed class ItemBatch(IImportJobRepository jobs, int size)
    {
        private readonly Lock _lock = new();
        private readonly SemaphoreSlim _writer = new(1, 1);
        private List<ImportItemUpdate> _items = [];

        public void Add(ImportItemUpdate update)
        {
            lock (_lock) _items.Add(update);
        }

        public Task FlushIfFullAsync()
        {
            lock (_lock)
            {
                if (_items.Count < size) return Task.CompletedTask;
            }

            return FlushAsync();
        }

        public async Task FlushAsync()
        {
            await _writer.WaitAsync().ConfigureAwait(false);
            try
            {
                List<ImportItemUpdate> batch;
                lock (_lock)
                {
                    if (_items.Count == 0) return;
                    (batch, _items) = (_items, []);
                }

                await jobs.UpdateItemsAsync(batch, CancellationToken.None).ConfigureAwait(false);
            }
            finally
            {
                _writer.Release();
            }
        }
    }

    private sealed class Counters
    {
        private int _processed, _imported, _duplicates, _skipped, _failed;

        public Counters(IReadOnlyList<ImportItem> items)
        {
            Total = items.Count;
            foreach (var item in items.Where(i => i.Status != ImportItemStatus.Pending)) Record(item.Status);
            ProcessedAtStart = _processed;
        }

        public int Total { get; }
        public int ProcessedAtStart { get; }
        public int Processed => Volatile.Read(ref _processed);
        public int Imported => Volatile.Read(ref _imported);
        public int Duplicates => Volatile.Read(ref _duplicates);
        public int Skipped => Volatile.Read(ref _skipped);
        public int Failed => Volatile.Read(ref _failed);

        public void Record(ImportItemStatus status)
        {
            Interlocked.Increment(ref _processed);
            switch (status)
            {
                case ImportItemStatus.Imported or ImportItemStatus.Attached: Interlocked.Increment(ref _imported); break;
                case ImportItemStatus.Duplicate or ImportItemStatus.AlreadyInLibrary: Interlocked.Increment(ref _duplicates); break;
                case ImportItemStatus.Unsupported or ImportItemStatus.Skipped: Interlocked.Increment(ref _skipped); break;
                case ImportItemStatus.Failed: Interlocked.Increment(ref _failed); break;
            }
        }
    }
}
