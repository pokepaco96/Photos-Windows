using System.Globalization;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using PhotosWindows.Application.Abstractions;
using PhotosWindows.Application.Common;
using PhotosWindows.Application.Library;

namespace PhotosWindows.Application.Backup;

public sealed record BackupFileEntry(string Path, long Size, string Sha256);

public sealed record BackupManifest
{
    public const string FileName = "backup.json";
    public const string FormatName = "photos-windows-backup";

    public string Format { get; init; } = FormatName;
    public int FormatVersion { get; init; } = 1;
    public DateTime CreatedUtc { get; init; }
    public string AppVersion { get; init; } = string.Empty;
    public int SchemaVersion { get; init; }
    public Guid LibraryId { get; init; }
    public string LibraryName { get; init; } = string.Empty;
    public bool IncludesManagedOriginals { get; init; }
    public int AssetCount { get; init; }
    public IReadOnlyList<BackupFileEntry> Files { get; init; } = [];
}

public sealed record BackupProgress(string Stage, int Done, int Total);

public sealed record BackupVerification(bool IsValid, BackupManifest? Manifest, IReadOnlyList<string> Problems);

/// <summary>
/// Backups are folders "PhotosWindows-Backup-&lt;name&gt;-&lt;timestamp&gt;" containing catalog.db (consistent SQLite
/// snapshot), library.json, optionally originals/, and backup.json with SHA-256 checksums of every file.
/// A backup is written under a ".partial" name and renamed only after it has been verified, so an interrupted backup
/// can never look complete. Existing backups are never deleted or overwritten.
/// </summary>
public sealed class BackupService
{
    private const string PartialSuffix = ".partial";
    private readonly ILibraryContext _context;
    private readonly ICatalogStore _catalog;
    private readonly IAssetRepository _assets;
    private readonly LibraryManager _libraries;
    private readonly IClock _clock;
    private readonly ILogger<BackupService> _logger;

    public BackupService(ILibraryContext context, ICatalogStore catalog, IAssetRepository assets, LibraryManager libraries, IClock clock, ILogger<BackupService> logger)
    {
        _context = context;
        _catalog = catalog;
        _assets = assets;
        _libraries = libraries;
        _clock = clock;
        _logger = logger;
    }

    public static string AppVersion => typeof(BackupService).Assembly.GetName().Version?.ToString(3) ?? "0.0.0";

    public async Task<string> CreateBackupAsync(string destinationFolder, bool includeManagedOriginals, IProgress<BackupProgress>? progress, CancellationToken ct)
    {
        var library = _context.RequireCurrent();
        var destination = Path.GetFullPath(destinationFolder);
        if (includeManagedOriginals && PathSafety.IsUnder(destination, library.OriginalsPath))
        {
            throw new ArgumentException("A backup cannot be stored inside the originals folder.", nameof(destinationFolder));
        }

        Directory.CreateDirectory(destination);
        var stamp = _clock.UtcNow.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
        var finalDir = Path.Combine(destination, $"PhotosWindows-Backup-{PathSafety.SanitizeFileName(library.Name)}-{stamp}");
        var workDir = finalDir + PartialSuffix;
        if (Directory.Exists(finalDir) || Directory.Exists(workDir)) throw new IOException("A backup with the same name already exists.");
        Directory.CreateDirectory(workDir);

        try
        {
            progress?.Report(new BackupProgress("catalog", 0, 1));
            await _catalog.BackupToAsync(Path.Combine(workDir, LibraryManager.DatabaseFileName), ct).ConfigureAwait(false);
            File.Copy(Path.Combine(library.RootPath, LibraryManifest.FileName), Path.Combine(workDir, LibraryManifest.FileName));

            if (includeManagedOriginals && Directory.Exists(library.OriginalsPath))
            {
                var originals = Directory.EnumerateFiles(library.OriginalsPath, "*", new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = false })
                    .Where(f => !f.EndsWith(PathSafety.PartialSuffix, StringComparison.OrdinalIgnoreCase))
                    .ToList();
                for (var i = 0; i < originals.Count; i++)
                {
                    ct.ThrowIfCancellationRequested();
                    progress?.Report(new BackupProgress("originals", i, originals.Count));
                    var relative = Path.GetRelativePath(library.RootPath, originals[i]);
                    var target = Path.Combine(workDir, relative);
                    Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                    await FileHashing.CopyWithHashAsync(originals[i], target, ct).ConfigureAwait(false);
                }
            }

            progress?.Report(new BackupProgress("checksums", 0, 1));
            var entries = new List<BackupFileEntry>();
            foreach (var file in Directory.EnumerateFiles(workDir, "*", SearchOption.AllDirectories).Order(StringComparer.Ordinal))
            {
                ct.ThrowIfCancellationRequested();
                entries.Add(new BackupFileEntry(Path.GetRelativePath(workDir, file).Replace('\\', '/'), new FileInfo(file).Length, await FileHashing.Sha256Async(file, ct).ConfigureAwait(false)));
            }

            var stats = await _assets.GetStatisticsAsync(ct).ConfigureAwait(false);
            var manifest = new BackupManifest
            {
                CreatedUtc = _clock.UtcNow,
                AppVersion = AppVersion,
                SchemaVersion = await _catalog.GetSchemaVersionAsync(Path.Combine(workDir, LibraryManager.DatabaseFileName), ct).ConfigureAwait(false),
                LibraryId = library.Id,
                LibraryName = library.Name,
                IncludesManagedOriginals = includeManagedOriginals,
                AssetCount = stats.PhotoCount + stats.VideoCount,
                Files = entries,
            };
            await File.WriteAllTextAsync(Path.Combine(workDir, BackupManifest.FileName), JsonSerializer.Serialize(manifest, LibraryManager.JsonOptions), ct).ConfigureAwait(false);

            progress?.Report(new BackupProgress("verify", 0, 1));
            var verification = await VerifyAsync(workDir, ct).ConfigureAwait(false);
            if (!verification.IsValid)
            {
                throw new IOException("The backup could not be verified: " + string.Join("; ", verification.Problems.Take(3)));
            }

            Directory.Move(workDir, finalDir);
            _logger.LogInformation("Backup created ({Files} files)", entries.Count);
            return finalDir;
        }
        catch
        {
            // Leave nothing that could be mistaken for a valid backup.
            try
            {
                if (Directory.Exists(workDir)) Directory.Delete(workDir, recursive: true);
            }
            catch (IOException cleanup)
            {
                _logger.LogWarning(cleanup, "Incomplete backup folder could not be removed");
            }

            throw;
        }
    }

    public async Task<BackupVerification> VerifyAsync(string backupFolder, CancellationToken ct)
    {
        var problems = new List<string>();
        var manifestPath = Path.Combine(backupFolder, BackupManifest.FileName);
        if (!File.Exists(manifestPath))
        {
            return new BackupVerification(false, null, ["backup.json is missing: this is not a complete backup."]);
        }

        BackupManifest? manifest;
        try
        {
            manifest = JsonSerializer.Deserialize<BackupManifest>(await File.ReadAllTextAsync(manifestPath, ct).ConfigureAwait(false), LibraryManager.JsonOptions);
        }
        catch (JsonException ex)
        {
            return new BackupVerification(false, null, ["backup.json is damaged: " + ex.Message]);
        }

        if (manifest is null || manifest.Format != BackupManifest.FormatName)
        {
            return new BackupVerification(false, null, ["backup.json does not describe a Photos Windows backup."]);
        }

        if (manifest.Files.All(f => f.Path != LibraryManager.DatabaseFileName)) problems.Add("The catalog is not listed in the backup.");
        foreach (var entry in manifest.Files)
        {
            ct.ThrowIfCancellationRequested();
            var full = Path.GetFullPath(Path.Combine(backupFolder, entry.Path));
            if (!PathSafety.IsUnder(full, backupFolder))
            {
                problems.Add($"Invalid path in manifest: {entry.Path}");
                continue;
            }

            if (!File.Exists(full))
            {
                problems.Add($"Missing: {entry.Path}");
                continue;
            }

            if (new FileInfo(full).Length != entry.Size || !string.Equals(await FileHashing.Sha256Async(full, ct).ConfigureAwait(false), entry.Sha256, StringComparison.OrdinalIgnoreCase))
            {
                problems.Add($"Checksum mismatch: {entry.Path}");
            }
        }

        if (manifest.SchemaVersion > _catalog.CurrentSchemaVersion)
        {
            problems.Add("The backup was made by a newer version of Photos Windows.");
        }

        return new BackupVerification(problems.Count == 0, manifest, problems);
    }

    /// <summary>
    /// Restores a verified backup as a NEW library in <paramref name="targetParentFolder"/> and opens it.
    /// The current library is not modified. Referenced originals keep their recorded paths.
    /// </summary>
    public async Task<LibraryInfo> RestoreAsNewLibraryAsync(string backupFolder, string targetParentFolder, CancellationToken ct)
    {
        var verification = await VerifyAsync(backupFolder, ct).ConfigureAwait(false);
        if (!verification.IsValid) throw new InvalidOperationException("The backup is not valid: " + string.Join("; ", verification.Problems.Take(3)));
        var manifest = verification.Manifest!;

        var name = PathSafety.SanitizeFileName(manifest.LibraryName + " (restored)");
        var root = Path.Combine(targetParentFolder, name + LibraryManager.LibraryExtension);
        for (var i = 2; Directory.Exists(root); i++) root = Path.Combine(targetParentFolder, $"{name} {i}{LibraryManager.LibraryExtension}");
        var work = root + PartialSuffix;
        Directory.CreateDirectory(work);
        try
        {
            foreach (var entry in manifest.Files)
            {
                ct.ThrowIfCancellationRequested();
                var target = Path.GetFullPath(Path.Combine(work, entry.Path));
                if (!PathSafety.IsUnder(target, work)) throw new InvalidOperationException("Invalid path in backup manifest.");
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                var hash = await FileHashing.CopyWithHashAsync(Path.Combine(backupFolder, entry.Path), target, ct).ConfigureAwait(false);
                if (!string.Equals(hash, entry.Sha256, StringComparison.OrdinalIgnoreCase)) throw new IOException($"Checksum mismatch while restoring {entry.Path}.");
            }

            var libraryManifest = await LibraryManager.ReadManifestAsync(work, ct).ConfigureAwait(false);
            await LibraryManager.WriteManifestAsync(work, libraryManifest with { Name = libraryManifest.Name + " (restored)", Id = Guid.NewGuid(), CachePath = null }, ct).ConfigureAwait(false);
            Directory.Move(work, root);
        }
        catch
        {
            if (Directory.Exists(work)) Directory.Delete(work, recursive: true);
            throw;
        }

        return await _libraries.OpenAsync(root, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Replaces the catalog of the open library with the backup's catalog. The current catalog is first saved to
    /// backups/replaced-&lt;timestamp&gt;.db so the operation can be undone. Originals are not touched.
    /// </summary>
    public async Task<LibraryInfo> RestoreCatalogInPlaceAsync(string backupFolder, CancellationToken ct)
    {
        var library = _context.RequireCurrent();
        var verification = await VerifyAsync(backupFolder, ct).ConfigureAwait(false);
        if (!verification.IsValid) throw new InvalidOperationException("The backup is not valid: " + string.Join("; ", verification.Problems.Take(3)));
        if (verification.Manifest!.LibraryId != library.Id)
        {
            throw new InvalidOperationException("This backup belongs to a different library. Restore it as a new library instead.");
        }

        var safety = Path.Combine(library.BackupsPath, $"replaced-{_clock.UtcNow:yyyyMMdd-HHmmss}.db");
        await _catalog.BackupToAsync(safety, ct).ConfigureAwait(false);
        var root = library.RootPath;
        _libraries.Close();

        var db = Path.Combine(root, LibraryManager.DatabaseFileName);
        var incoming = db + ".restore";
        await FileHashing.CopyWithHashAsync(Path.Combine(backupFolder, LibraryManager.DatabaseFileName), incoming, ct).ConfigureAwait(false);
        foreach (var sidecar in new[] { db + "-wal", db + "-shm" }.Where(File.Exists)) File.Delete(sidecar);
        File.Move(incoming, db, overwrite: true);
        return await _libraries.OpenAsync(root, ct).ConfigureAwait(false);
    }

    /// <summary>Lists complete backups found in a folder (newest first).</summary>
    public static IReadOnlyList<string> FindBackups(string folder) =>
        Directory.Exists(folder)
            ? Directory.EnumerateDirectories(folder, "PhotosWindows-Backup-*")
                .Where(d => !d.EndsWith(PartialSuffix, StringComparison.Ordinal) && File.Exists(Path.Combine(d, BackupManifest.FileName)))
                .OrderByDescending(d => d, StringComparer.Ordinal)
                .ToList()
            : [];
}
