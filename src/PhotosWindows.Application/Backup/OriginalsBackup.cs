using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using PhotosWindows.Application.Abstractions;
using PhotosWindows.Application.Common;
using PhotosWindows.Domain;

namespace PhotosWindows.Application.Backup;

// ---------------------------------------------------------------------------------------------------------------
// Originals backup: every physical original of the catalog copied byte for byte into YEAR/MONTH folders.
// It is NOT the restorable library backup (BackupService): there is no catalog here, only the files and a manifest.
// ---------------------------------------------------------------------------------------------------------------

public enum OriginalsIssueKind
{
    /// <summary>The source file is not reachable (disconnected drive, deleted, no permission).</summary>
    SourceMissing = 0,
    /// <summary>The source no longer has the SHA-256 recorded when it was imported.</summary>
    ChangedSinceImport = 1,
    /// <summary>Size or modification time changed while the file was being copied.</summary>
    ChangedDuringCopy = 2,
    /// <summary>The destination file system cannot hold a file this large (e.g. FAT32 and 4 GiB).</summary>
    TooLargeForDestination = 3,
    /// <summary>The copy or its verification failed.</summary>
    CopyFailed = 4,
}

/// <summary>A file the backup wants to copy, with its destination inside the backup folder.</summary>
public sealed record OriginalsPlanEntry(
    long FileId,
    Guid AssetUid,
    MediaType MediaType,
    FileRole Role,
    string SourcePath,
    string OriginalName,
    string RelativePath,
    long CatalogSize,
    string CatalogSha256,
    DateTime CaptureLocal,
    DateSource DateSource);

public sealed record OriginalsManifestFile
{
    public Guid AssetId { get; init; }
    [JsonConverter(typeof(JsonStringEnumConverter))] public FileRole Role { get; init; }
    public string OriginalName { get; init; } = string.Empty;
    /// <summary>Path inside the backup folder, always with forward slashes: <c>2024/07/IMG_1234.HEIC</c>.</summary>
    public string Path { get; init; } = string.Empty;
    public long Size { get; init; }
    public string Sha256 { get; init; } = string.Empty;
    public string CaptureLocal { get; init; } = string.Empty;
    [JsonConverter(typeof(JsonStringEnumConverter))] public DateSource DateSource { get; init; }
    public bool MatchesCatalogHash { get; init; }
}

public sealed record OriginalsManifestIssue
{
    public Guid AssetId { get; init; }
    [JsonConverter(typeof(JsonStringEnumConverter))] public FileRole Role { get; init; }
    public string OriginalName { get; init; } = string.Empty;
    public string Path { get; init; } = string.Empty;
    [JsonConverter(typeof(JsonStringEnumConverter))] public OriginalsIssueKind Kind { get; init; }
}

/// <summary>
/// <c>backup-manifest.json</c>. Deliberately free of absolute source paths, GPS, people and any face data: it lists
/// what was copied and how to verify it, nothing else.
/// </summary>
public sealed record OriginalsManifest
{
    public const string FileName = "backup-manifest.json";
    public const string FormatName = "photos-windows-originals-backup";
    public const int CurrentVersion = 1;

    public string Format { get; init; } = FormatName;
    public int FormatVersion { get; init; } = CurrentVersion;
    public Guid LibraryId { get; init; }
    public string LibraryName { get; init; } = string.Empty;
    public DateTime CreatedUtc { get; init; }
    public string AppVersion { get; init; } = string.Empty;
    /// <summary>True only when every file of the catalog was copied and verified, with no issue at all.</summary>
    public bool Complete { get; init; }
    public int AssetCount { get; init; }
    public int PhotoCount { get; init; }
    public int VideoCount { get; init; }
    public int FileCount { get; init; }
    public long TotalBytes { get; init; }
    public IReadOnlyList<OriginalsManifestFile> Files { get; init; } = [];
    public IReadOnlyList<OriginalsManifestIssue> Issues { get; init; } = [];

    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true, PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    public string ToJson() => JsonSerializer.Serialize(this, Options);

    public static OriginalsManifest Parse(string json)
    {
        var manifest = JsonSerializer.Deserialize<OriginalsManifest>(json, Options) ?? throw new InvalidDataException("Empty manifest.");
        if (manifest.Format != FormatName) throw new InvalidDataException("Not an originals backup manifest.");
        if (manifest.FormatVersion is < 1 or > CurrentVersion) throw new InvalidDataException($"Unsupported manifest version {manifest.FormatVersion}.");
        return manifest;
    }
}

/// <summary>Pure planning: where each file goes. No I/O, so it is fully unit-testable.</summary>
public static class OriginalsBackupPlanner
{
    public const string UndatedFolder = "Sin fecha/00";

    private static readonly HashSet<string> Reserved = new(StringComparer.OrdinalIgnoreCase)
    {
        "CON", "PRN", "AUX", "NUL", "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9",
    };

    /// <summary>
    /// <c>YYYY/MM</c> from the capture date already stored in the catalog (local wall-clock, never converted), or
    /// <see cref="UndatedFolder"/> when the catalog does not know the date. No date is ever invented.
    /// </summary>
    public static string FolderFor(DateTime captureLocal, DateSource source) =>
        source == DateSource.Unknown || captureLocal.Year < 1900
            ? UndatedFolder
            : string.Create(CultureInfo.InvariantCulture, $"{captureLocal.Year:0000}/{captureLocal.Month:00}");

    /// <summary>A name Windows accepts: no reserved device names, no invalid characters, no trailing dot or space.</summary>
    public static string SafeName(string name)
    {
        var safe = PathSafety.SanitizeFileName(Path.GetFileName(name.Replace('\\', '/')));
        var stem = Path.GetFileNameWithoutExtension(safe);
        if (Reserved.Contains(stem)) safe = "_" + safe;
        return safe;
    }

    /// <summary>
    /// Assigns every file a unique destination. All files of an asset (original, RAW, Live Photo clip, sidecar) go to
    /// the asset's folder and share the same " (n)" suffix when a name is already taken, so pairs stay together.
    /// Deterministic: assets are processed in id order and names compare case-insensitively.
    /// </summary>
    public static IReadOnlyList<OriginalsPlanEntry> Plan(IEnumerable<OriginalFileRow> rows, Func<AssetFile, string> resolve)
    {
        var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var plan = new List<OriginalsPlanEntry>();
        foreach (var asset in rows.GroupBy(r => r.File.AssetId).OrderBy(g => g.Key))
        {
            var files = asset.OrderBy(r => r.File.Role).ThenBy(r => r.File.Id).ToList();
            var folder = FolderFor(files[0].CaptureLocal, files[0].DateSource);
            var names = new List<string>();
            foreach (var f in files)
            {
                // The original keeps the name shown in the library; companions keep their own file names.
                var raw = f.File.Role == FileRole.Original && !string.IsNullOrWhiteSpace(f.AssetFileName) ? f.AssetFileName : Path.GetFileName(f.File.StoredPath.Replace('\\', '/'));
                var name = SafeName(raw);
                // Two files of the same asset with the same name (unusual): keep both, told apart inside the asset.
                for (var k = 2; names.Contains(name, StringComparer.OrdinalIgnoreCase); k++) name = Suffix(SafeName(raw), $"-{k}");
                names.Add(name);
            }

            var n = 1;
            while (names.Any(name => used.Contains(folder + "/" + (n == 1 ? name : Suffix(name, $" ({n})"))))) n++;
            for (var i = 0; i < files.Count; i++)
            {
                var f = files[i];
                var relative = folder + "/" + (n == 1 ? names[i] : Suffix(names[i], $" ({n})"));
                used.Add(relative);
                plan.Add(new OriginalsPlanEntry(f.File.Id, f.AssetUid, f.MediaType, f.File.Role, resolve(f.File), names[i], relative,
                    f.File.SizeBytes, f.File.Sha256, files[0].CaptureLocal, files[0].DateSource));
            }
        }

        return plan;
    }

    private static string Suffix(string name, string suffix) => Path.GetFileNameWithoutExtension(name) + suffix + Path.GetExtension(name);
}

/// <summary>Free space and file-system limits of a destination. Replaced in tests to simulate a full or FAT32 disk.</summary>
public interface IVolumeInspector
{
    /// <summary>Bytes available to the user at <paramref name="path"/>, or null when it cannot be determined.</summary>
    long? FreeBytes(string path);

    /// <summary>Largest file the file system accepts, or null when there is no practical limit.</summary>
    long? MaxFileBytes(string path);
}

public sealed class SystemVolumeInspector : IVolumeInspector
{
    public const long Fat32MaxFileBytes = 4L * 1024 * 1024 * 1024 - 1;

    public long? FreeBytes(string path) => Drive(path)?.AvailableFreeSpace;

    public long? MaxFileBytes(string path) =>
        Drive(path) is { } d && d.DriveFormat is "FAT32" or "FAT" or "vfat" or "msdos" ? Fat32MaxFileBytes : null;

    private static DriveInfo? Drive(string path)
    {
        try
        {
            var full = Path.GetFullPath(path);
            var best = DriveInfo.GetDrives()
                .Where(d => d.IsReady && PathSafety.IsUnder(full, d.RootDirectory.FullName))
                .OrderByDescending(d => d.RootDirectory.FullName.Length)
                .FirstOrDefault();
            return best;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return null;
        }
    }
}

/// <summary>What the user sees before starting: sizes, what is missing, and whether the destination can hold it.</summary>
public sealed record OriginalsPreflight
{
    public int AssetCount { get; init; }
    public int PhotoCount { get; init; }
    public int VideoCount { get; init; }
    public int FileCount { get; init; }
    public long TotalBytes { get; init; }
    public int UndatedAssets { get; init; }
    /// <summary>Files that cannot be read right now (their names, never their folders).</summary>
    public IReadOnlyList<OriginalsManifestIssue> Unavailable { get; init; } = [];
    public IReadOnlyList<OriginalsManifestIssue> TooLarge { get; init; } = [];
    public long? FreeBytes { get; init; }
    public long RequiredBytes { get; init; }
    public bool EnoughSpace => FreeBytes is null || FreeBytes >= RequiredBytes;
    /// <summary>Why this destination cannot be used at all (inside the library, inside a source folder), or null.</summary>
    public string? DestinationProblem { get; init; }
    public bool CanBeComplete => Unavailable.Count == 0 && TooLarge.Count == 0;
    public bool CanStart => DestinationProblem is null && EnoughSpace && FileCount > 0;
}

public sealed record OriginalsProgress(int FilesDone, int FilesTotal, long BytesDone, long BytesTotal, string CurrentName);

public enum OriginalsBackupStatus
{
    /// <summary>Every file copied and verified; manifest says complete.</summary>
    Complete = 0,
    /// <summary>The user accepted leaving out unavailable files; verified, but the manifest says NOT complete.</summary>
    PartialAccepted = 1,
    /// <summary>Stopped by an error, a full or disconnected disk, or issues found while copying. Can be resumed.</summary>
    Incomplete = 2,
    Cancelled = 3,
}

public sealed record OriginalsBackupResult(
    OriginalsBackupStatus Status,
    string Folder,
    int FilesCopied,
    int FilesAlreadyPresent,
    long Bytes,
    IReadOnlyList<OriginalsManifestIssue> Issues,
    string? Error);

public sealed record OriginalsVerification(bool IsValid, OriginalsManifest? Manifest, int FilesChecked, IReadOnlyList<string> Problems);

public sealed class OriginalsBackupBlockedException(string message, OriginalsPreflight preflight) : InvalidOperationException(message)
{
    public OriginalsPreflight Preflight { get; } = preflight;
}
