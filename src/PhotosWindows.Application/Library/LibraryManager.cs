using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using PhotosWindows.Application.Abstractions;
using PhotosWindows.Application.Common;
using PhotosWindows.Domain;

namespace PhotosWindows.Application.Library;

public enum LibraryOpenError
{
    NotFound,
    NotALibrary,
    CatalogMissing,
    NewerVersion,
    Damaged,
    AccessDenied,
}

public sealed class LibraryOpenException : Exception
{
    public LibraryOpenException(LibraryOpenError error, string message, Exception? inner = null) : base(message, inner) => Error = error;
    public LibraryOpenException() { }
    public LibraryOpenException(string message) : base(message) { }
    public LibraryOpenException(string message, Exception inner) : base(message, inner) { }

    public LibraryOpenError Error { get; }
}

public sealed record LibraryManifest
{
    public const string FormatName = "photos-windows-library";
    public const string FileName = "library.json";

    public string Format { get; init; } = FormatName;
    public int FormatVersion { get; init; } = 1;
    public Guid Id { get; init; } = Guid.NewGuid();
    public string Name { get; init; } = "Photos";
    public DateTime CreatedUtc { get; init; }
    /// <summary>Absolute cache location; null means "&lt;library&gt;/cache".</summary>
    public string? CachePath { get; init; }
}

public sealed record LibraryStorageReport(LibraryStatistics Catalog, long DatabaseBytes, long CacheBytes, string RootPath, string CachePath);

/// <summary>Creates, opens, switches and describes libraries.</summary>
public sealed class LibraryManager
{
    public const string LibraryExtension = ".pwlibrary";
    public const string DatabaseFileName = "catalog.db";
    public const string OriginalsFolder = "originals";
    public const string CacheFolder = "cache";
    public const string BackupsFolder = "backups";

    internal static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true, Converters = { new JsonStringEnumConverter() } };

    private readonly LibraryContext _context;
    private readonly ICatalogStore _catalog;
    private readonly IAssetRepository _assets;
    private readonly IImportJobRepository _imports;
    private readonly ISettingsStore _settings;
    private readonly IClock _clock;
    private readonly ILogger<LibraryManager> _logger;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public LibraryManager(LibraryContext context, ICatalogStore catalog, IAssetRepository assets, IImportJobRepository imports, ISettingsStore settings, IClock clock, ILogger<LibraryManager> logger)
    {
        _context = context;
        _catalog = catalog;
        _assets = assets;
        _imports = imports;
        _settings = settings;
        _clock = clock;
        _logger = logger;
    }

    public LibraryInfo? Current => _context.Current;

    /// <summary>Creates "&lt;parent&gt;/&lt;name&gt;.pwlibrary" and opens it.</summary>
    public async Task<LibraryInfo> CreateAsync(string parentFolder, string name, CancellationToken cancellationToken)
    {
        var normalizedName = LibraryRules.NormalizeName(name, nameof(name));
        if (!Directory.Exists(parentFolder))
        {
            throw new DirectoryNotFoundException($"The folder '{parentFolder}' does not exist.");
        }

        var root = Path.Combine(parentFolder, PathSafety.SanitizeFileName(normalizedName) + LibraryExtension);
        for (var i = 2; Directory.Exists(root) || File.Exists(root); i++)
        {
            root = Path.Combine(parentFolder, $"{PathSafety.SanitizeFileName(normalizedName)} {i}{LibraryExtension}");
        }

        Directory.CreateDirectory(root);
        Directory.CreateDirectory(Path.Combine(root, OriginalsFolder));
        Directory.CreateDirectory(Path.Combine(root, CacheFolder));
        Directory.CreateDirectory(Path.Combine(root, BackupsFolder));
        var manifest = new LibraryManifest { Name = normalizedName, CreatedUtc = _clock.UtcNow };
        await WriteManifestAsync(root, manifest, cancellationToken).ConfigureAwait(false);
        await _catalog.InitializeAsync(Path.Combine(root, DatabaseFileName), cancellationToken).ConfigureAwait(false);
        _logger.LogInformation("Created library {Name}", normalizedName);
        return await OpenAsync(root, cancellationToken).ConfigureAwait(false);
    }

    public async Task<LibraryInfo> OpenAsync(string rootPath, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var root = Path.GetFullPath(rootPath);
            if (!Directory.Exists(root))
            {
                throw new LibraryOpenException(LibraryOpenError.NotFound, $"The library folder was not found: {root}. If it is on an external drive, connect the drive and try again.");
            }

            var manifest = await ReadManifestAsync(root, cancellationToken).ConfigureAwait(false);
            var dbPath = Path.Combine(root, DatabaseFileName);
            if (!File.Exists(dbPath))
            {
                throw new LibraryOpenException(LibraryOpenError.CatalogMissing, "The library catalog (catalog.db) is missing. Restore it from a backup in Settings › Backup.");
            }

            await MigrateWithBackupAsync(root, dbPath, cancellationToken).ConfigureAwait(false);

            var cache = manifest.CachePath ?? Path.Combine(root, CacheFolder);
            Directory.CreateDirectory(cache);
            Directory.CreateDirectory(Path.Combine(root, OriginalsFolder));
            Directory.CreateDirectory(Path.Combine(root, BackupsFolder));

            var info = new LibraryInfo(manifest.Id, manifest.Name, root, dbPath, Path.Combine(root, OriginalsFolder), cache, Path.Combine(root, BackupsFolder));
            if (_context.Current is not null)
            {
                _catalog.ReleaseConnections();
            }

            _context.Set(info);
            var interrupted = await _imports.MarkRunningAsInterruptedAsync(cancellationToken).ConfigureAwait(false);
            if (interrupted.Count > 0)
            {
                _logger.LogWarning("{Count} import(s) were interrupted and can be resumed.", interrupted.Count);
            }

            RememberRecent(root);
            _logger.LogInformation("Opened library {Name}", manifest.Name);
            return info;
        }
        catch (UnauthorizedAccessException ex)
        {
            throw new LibraryOpenException(LibraryOpenError.AccessDenied, "Windows denied access to the library folder.", ex);
        }
        finally
        {
            _gate.Release();
        }
    }

    public void Close()
    {
        if (_context.Current is null) return;
        _context.Set(null);
        _catalog.ReleaseConnections();
    }

    public IReadOnlyList<string> GetRecentLibraries() => _settings.Load().RecentLibraries;

    public void ForgetRecent(string path)
    {
        var settings = _settings.Load();
        _settings.Save(settings with
        {
            RecentLibraries = settings.RecentLibraries.Where(p => !string.Equals(p, path, StringComparison.OrdinalIgnoreCase)).ToList(),
            LastLibraryPath = string.Equals(settings.LastLibraryPath, path, StringComparison.OrdinalIgnoreCase) ? null : settings.LastLibraryPath,
        });
    }

    public async Task<LibraryStorageReport> GetStorageReportAsync(CancellationToken cancellationToken)
    {
        var library = _context.RequireCurrent();
        var stats = await _assets.GetStatisticsAsync(cancellationToken).ConfigureAwait(false);
        var dbBytes = new[] { library.DatabasePath, library.DatabasePath + "-wal" }.Where(File.Exists).Sum(f => new FileInfo(f).Length);
        var cacheBytes = await Task.Run(() => DirectorySize(library.CachePath, cancellationToken), cancellationToken).ConfigureAwait(false);
        return new LibraryStorageReport(stats, dbBytes, cacheBytes, library.RootPath, library.CachePath);
    }

    /// <summary>Moves the regenerable cache to another folder (the old cache is deleted when it belongs to this library).</summary>
    public async Task<LibraryInfo> SetCacheLocationAsync(string? newCachePath, CancellationToken cancellationToken)
    {
        var library = _context.RequireCurrent();
        var manifest = await ReadManifestAsync(library.RootPath, cancellationToken).ConfigureAwait(false);
        string? target = string.IsNullOrWhiteSpace(newCachePath) ? null : Path.Combine(Path.GetFullPath(newCachePath), $"PhotosWindowsCache-{manifest.Id:N}");
        if (target is not null && PathSafety.IsUnder(target, library.OriginalsPath))
        {
            throw new ArgumentException("The cache cannot be placed inside the originals folder.", nameof(newCachePath));
        }

        await WriteManifestAsync(library.RootPath, manifest with { CachePath = target }, cancellationToken).ConfigureAwait(false);
        var oldCache = library.CachePath;
        var reopened = await OpenAsync(library.RootPath, cancellationToken).ConfigureAwait(false);
        if (!string.Equals(oldCache, reopened.CachePath, StringComparison.OrdinalIgnoreCase))
        {
            DeleteCacheFolder(oldCache, library);
        }

        return reopened;
    }

    /// <summary>Deletes generated thumbnails; they are rebuilt on demand.</summary>
    public async Task ClearCacheAsync(CancellationToken cancellationToken)
    {
        var library = _context.RequireCurrent();
        var thumbs = library.ThumbnailsPath;
        if (Directory.Exists(thumbs))
        {
            await Task.Run(() => Directory.Delete(thumbs, recursive: true), cancellationToken).ConfigureAwait(false);
        }

        foreach (var id in await _assets.GetIdsWithThumbnailStateAsync(ThumbnailState.Ready, int.MaxValue, cancellationToken).ConfigureAwait(false))
        {
            await _assets.SetThumbnailStateAsync(id, ThumbnailState.Pending, cancellationToken).ConfigureAwait(false);
        }
    }

    public static bool LooksLikeLibrary(string folder) => File.Exists(Path.Combine(folder, LibraryManifest.FileName));

    private async Task MigrateWithBackupAsync(string root, string dbPath, CancellationToken cancellationToken)
    {
        int version;
        try
        {
            version = await _catalog.GetSchemaVersionAsync(dbPath, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException and not LibraryOpenException)
        {
            throw new LibraryOpenException(LibraryOpenError.Damaged, "The library catalog could not be read. It may be damaged; restore it from a backup.", ex);
        }

        if (version > _catalog.CurrentSchemaVersion)
        {
            throw new LibraryOpenException(LibraryOpenError.NewerVersion, "This library was last used with a newer version of Photos Windows. Update the app to open it.");
        }

        if (version > 0 && version < _catalog.CurrentSchemaVersion)
        {
            var backups = Path.Combine(root, BackupsFolder);
            Directory.CreateDirectory(backups);
            var backup = Path.Combine(backups, $"pre-migration-v{version}-{_clock.UtcNow:yyyyMMddHHmmss}.db");
            await _catalog.BackupDatabaseAsync(dbPath, backup, cancellationToken).ConfigureAwait(false);
            _logger.LogInformation("Catalog backed up before migrating from schema {Version}", version);
        }

        await _catalog.InitializeAsync(dbPath, cancellationToken).ConfigureAwait(false);
    }

    internal static async Task<LibraryManifest> ReadManifestAsync(string root, CancellationToken cancellationToken)
    {
        var path = Path.Combine(root, LibraryManifest.FileName);
        if (!File.Exists(path))
        {
            throw new LibraryOpenException(LibraryOpenError.NotALibrary, "This folder is not a Photos Windows library (library.json not found).");
        }

        try
        {
            await using var stream = File.OpenRead(path);
            var manifest = await JsonSerializer.DeserializeAsync<LibraryManifest>(stream, JsonOptions, cancellationToken).ConfigureAwait(false);
            if (manifest is null || manifest.Format != LibraryManifest.FormatName)
            {
                throw new LibraryOpenException(LibraryOpenError.NotALibrary, "library.json does not describe a Photos Windows library.");
            }

            return manifest;
        }
        catch (JsonException ex)
        {
            throw new LibraryOpenException(LibraryOpenError.Damaged, "library.json is damaged.", ex);
        }
    }

    internal static async Task WriteManifestAsync(string root, LibraryManifest manifest, CancellationToken cancellationToken)
    {
        var path = Path.Combine(root, LibraryManifest.FileName);
        var temp = path + ".tmp";
        await using (var stream = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            await JsonSerializer.SerializeAsync(stream, manifest, JsonOptions, cancellationToken).ConfigureAwait(false);
        }

        File.Move(temp, path, overwrite: true);
    }

    private void RememberRecent(string root)
    {
        var settings = _settings.Load();
        var recent = new List<string> { root };
        recent.AddRange(settings.RecentLibraries.Where(p => !string.Equals(p, root, StringComparison.OrdinalIgnoreCase)).Take(9));
        _settings.Save(settings with { LastLibraryPath = root, RecentLibraries = recent });
    }

    private void DeleteCacheFolder(string cache, LibraryInfo library)
    {
        // Only delete folders we created: the default cache inside the library or a PhotosWindowsCache-* folder.
        var isOurs = PathSafety.IsUnder(cache, library.RootPath) || Path.GetFileName(cache).StartsWith("PhotosWindowsCache-", StringComparison.Ordinal);
        if (!isOurs || !Directory.Exists(cache)) return;
        try
        {
            Directory.Delete(cache, recursive: true);
        }
        catch (IOException ex)
        {
            _logger.LogWarning(ex, "The previous cache folder could not be removed");
        }
    }

    private static long DirectorySize(string path, CancellationToken cancellationToken)
    {
        if (!Directory.Exists(path)) return 0;
        long total = 0;
        foreach (var file in Directory.EnumerateFiles(path, "*", new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true }))
        {
            cancellationToken.ThrowIfCancellationRequested();
            total += new FileInfo(file).Length;
        }

        return total;
    }
}
