namespace PhotosWindows.Application.Abstractions;

/// <summary>
/// Paths of an opened library. A library is a folder (by convention named *.pwlibrary) containing:
/// <c>library.json</c> (identity), <c>catalog.db</c> (SQLite catalog), <c>originals/</c> (managed copies),
/// <c>cache/</c> (regenerable thumbnails, may be relocated) and <c>backups/</c>.
/// </summary>
public sealed record LibraryInfo(
    Guid Id,
    string Name,
    string RootPath,
    string DatabasePath,
    string OriginalsPath,
    string CachePath,
    string BackupsPath)
{
    public string ThumbnailsPath => Path.Combine(CachePath, "thumbs");

    /// <summary>Resolves a stored path (relative for managed files) to an absolute path.</summary>
    public string Resolve(Domain.AssetFile file) =>
        file.StorageMode == Domain.StorageMode.Managed ? Path.GetFullPath(Path.Combine(RootPath, file.StoredPath)) : file.StoredPath;
}

public interface ILibraryContext
{
    LibraryInfo? Current { get; }

    /// <summary>Throws <see cref="InvalidOperationException"/> when no library is open.</summary>
    LibraryInfo RequireCurrent();

    event EventHandler<LibraryInfo?>? LibraryChanged;
}

/// <summary>Creates/opens the physical catalog and applies schema migrations.</summary>
public interface ICatalogStore
{
    int CurrentSchemaVersion { get; }

    /// <summary>Creates or migrates the catalog database; returns the resulting schema version.</summary>
    Task<int> InitializeAsync(string databasePath, CancellationToken cancellationToken);

    Task<int> GetSchemaVersionAsync(string databasePath, CancellationToken cancellationToken);

    /// <summary>Runs SQLite's integrity and foreign-key checks; returns problems found (empty = healthy).</summary>
    Task<IReadOnlyList<string>> CheckIntegrityAsync(CancellationToken cancellationToken);

    /// <summary>Consistent online copy of the current catalog to <paramref name="destinationPath"/> (SQLite backup API).</summary>
    Task BackupToAsync(string destinationPath, CancellationToken cancellationToken);

    /// <summary>Consistent copy of any catalog file (used before migrations, when no library is open yet).</summary>
    Task BackupDatabaseAsync(string sourceDatabasePath, string destinationPath, CancellationToken cancellationToken);

    /// <summary>Closes pooled connections so files can be replaced (restore) or moved.</summary>
    void ReleaseConnections();
}

public interface IClock
{
    DateTime UtcNow { get; }

    /// <summary>Zone used to express UTC-only timestamps (e.g. video creation times) as local wall-clock time.</summary>
    TimeZoneInfo LocalZone { get; }
}

public sealed class SystemClock : IClock
{
    public DateTime UtcNow => DateTime.UtcNow;

    public TimeZoneInfo LocalZone => TimeZoneInfo.Local;
}

/// <summary>Per-user application folders (settings, logs). Never inside the repository.</summary>
public interface IAppPaths
{
    string AppDataPath { get; }
    string LogsPath { get; }
    string SettingsFilePath { get; }
}

/// <summary>Physical file deletion. The Windows implementation sends files to the Recycle Bin.</summary>
public interface IFileDeleter
{
    /// <summary>True when deleted files can still be recovered by the user (e.g. Recycle Bin).</summary>
    bool IsRecoverable { get; }

    void Delete(string path);
}
