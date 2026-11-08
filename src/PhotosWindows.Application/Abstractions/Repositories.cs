using PhotosWindows.Domain;

namespace PhotosWindows.Application.Abstractions;

/// <summary>Lightweight row used by grids. Keeps the whole timeline cheap to hold in memory (~100 bytes/item).</summary>
public sealed record AssetSummary(
    long Id,
    Guid Uid,
    MediaType MediaType,
    DateTime CaptureLocal,
    int DisplayWidth,
    int DisplayHeight,
    TimeSpan? Duration,
    bool IsFavorite,
    bool HasEdits,
    int EditVersion,
    bool HasLiveVideo,
    bool HasRawPair,
    bool IsAvailable,
    ThumbnailState ThumbnailState,
    string FileName,
    DateTime? DeletedUtc);

/// <summary>Everything needed to insert a newly imported asset atomically.</summary>
public sealed record NewAssetRecord(Asset Asset, IReadOnlyList<AssetFile> Files, IReadOnlyList<string> Keywords);

public sealed record LibraryStatistics(
    int PhotoCount,
    int VideoCount,
    int HiddenCount,
    int TrashCount,
    int AlbumCount,
    long ReferencedBytes,
    long ManagedBytes,
    int UnavailableFiles,
    int PendingThumbnails);

public sealed record AssetDetailsUpdate(
    string? Title,
    string? Description,
    int Rating,
    GeoLocation? Location,
    bool ClearLocation = false);

public interface IAssetRepository
{
    Task<long> InsertAsync(NewAssetRecord record, CancellationToken cancellationToken);
    Task<Asset?> GetAsync(long id, CancellationToken cancellationToken);
    Task<IReadOnlyList<Asset>> GetManyAsync(IReadOnlyCollection<long> ids, CancellationToken cancellationToken);
    Task<IReadOnlyList<AssetFile>> GetFilesAsync(long assetId, CancellationToken cancellationToken);
    Task<AssetFile?> GetOriginalFileAsync(long assetId, CancellationToken cancellationToken);
    Task<IReadOnlyList<AssetSummary>> QueryAsync(AssetQuery query, CancellationToken cancellationToken);
    Task<int> CountAsync(AssetQuery query, CancellationToken cancellationToken);

    /// <summary>Coordinates of the located assets matching <paramref name="query"/> (assets without GPS are skipped).</summary>
    Task<IReadOnlyList<GeoPoint>> QueryGeoPointsAsync(AssetQuery query, CancellationToken cancellationToken);

    /// <summary>Sets (or clears, when null) the catalog location of several assets. Original files are never touched.</summary>
    Task SetLocationAsync(IReadOnlyCollection<long> ids, GeoLocation? location, CancellationToken cancellationToken);
    Task<AssetFile?> FindFileByStoredPathAsync(string storedPath, CancellationToken cancellationToken);
    Task<IReadOnlyList<AssetFile>> FindFilesByHashAsync(string sha256, CancellationToken cancellationToken);
    Task AttachFileAsync(AssetFile file, CancellationToken cancellationToken);

    Task SetFavoriteAsync(IReadOnlyCollection<long> ids, bool favorite, CancellationToken cancellationToken);
    Task SetHiddenAsync(IReadOnlyCollection<long> ids, bool hidden, CancellationToken cancellationToken);
    Task MoveToTrashAsync(IReadOnlyCollection<long> ids, DateTime deletedUtc, CancellationToken cancellationToken);
    Task RestoreFromTrashAsync(IReadOnlyCollection<long> ids, CancellationToken cancellationToken);

    /// <summary>Deletes catalog rows (asset, files, memberships, edits). Returns the files that were referenced.</summary>
    Task<IReadOnlyList<AssetFile>> RemoveFromCatalogAsync(IReadOnlyCollection<long> ids, CancellationToken cancellationToken);

    Task UpdateDetailsAsync(long id, AssetDetailsUpdate update, CancellationToken cancellationToken);
    Task UpdateCaptureDateAsync(long id, CaptureDate date, CancellationToken cancellationToken);
    Task SetThumbnailStateAsync(long id, ThumbnailState state, CancellationToken cancellationToken);
    Task SetPerceptualHashAsync(long id, ulong hash, CancellationToken cancellationToken);
    Task<IReadOnlyList<long>> GetIdsWithThumbnailStateAsync(ThumbnailState state, int limit, CancellationToken cancellationToken);
    Task<IReadOnlyList<(long Id, ulong Hash)>> GetPerceptualHashesAsync(CancellationToken cancellationToken);
    Task<IReadOnlyList<long>> GetPhotosWithoutPerceptualHashAsync(int limit, CancellationToken cancellationToken);

    Task UpdateFileAvailabilityAsync(long fileId, bool available, DateTime verifiedUtc, CancellationToken cancellationToken);
    Task UpdateFileLocationAsync(long fileId, string storedPath, long size, DateTime modifiedUtc, CancellationToken cancellationToken);
    Task<IReadOnlyList<AssetFile>> GetAllFilesAsync(CancellationToken cancellationToken);
    Task<IReadOnlyList<IReadOnlyList<long>>> GetExactDuplicateGroupsAsync(CancellationToken cancellationToken);
    Task<IReadOnlyList<long>> GetTrashedBeforeAsync(DateTime cutoffUtc, CancellationToken cancellationToken);
    Task<LibraryStatistics> GetStatisticsAsync(CancellationToken cancellationToken);
    Task<IReadOnlyList<string>> GetDistinctValuesAsync(SmartField field, CancellationToken cancellationToken);

    /// <summary>Rebuilds full-text rows for the given assets (or all when null).</summary>
    Task RefreshSearchIndexAsync(IReadOnlyCollection<long>? ids, CancellationToken cancellationToken);
}

public interface IKeywordRepository
{
    Task<IReadOnlyList<Keyword>> GetAllAsync(CancellationToken cancellationToken);
    Task<IReadOnlyList<string>> GetForAssetAsync(long assetId, CancellationToken cancellationToken);
    Task AddAsync(IReadOnlyCollection<long> assetIds, IReadOnlyCollection<string> keywords, CancellationToken cancellationToken);
    Task RemoveAsync(IReadOnlyCollection<long> assetIds, string keyword, CancellationToken cancellationToken);
    Task RenameAsync(long keywordId, string newName, CancellationToken cancellationToken);
    Task DeleteAsync(long keywordId, CancellationToken cancellationToken);
}

public interface IAlbumRepository
{
    Task<IReadOnlyList<Album>> GetAlbumsAsync(CancellationToken cancellationToken);
    Task<Album?> GetAlbumAsync(long id, CancellationToken cancellationToken);
    Task<IReadOnlyList<AlbumFolder>> GetFoldersAsync(CancellationToken cancellationToken);
    Task<long> CreateAlbumAsync(string name, AlbumKind kind, long? folderId, SmartAlbumDefinition? definition, CancellationToken cancellationToken);
    Task RenameAlbumAsync(long id, string name, CancellationToken cancellationToken);
    Task UpdateSmartDefinitionAsync(long id, SmartAlbumDefinition definition, CancellationToken cancellationToken);
    Task DeleteAlbumAsync(long id, CancellationToken cancellationToken);
    Task MoveAlbumAsync(long id, long? folderId, CancellationToken cancellationToken);
    Task<long> CreateFolderAsync(string name, long? parentId, CancellationToken cancellationToken);
    Task RenameFolderAsync(long id, string name, CancellationToken cancellationToken);
    /// <summary>Deletes the folder; contained albums and sub-folders move to the parent folder.</summary>
    Task DeleteFolderAsync(long id, CancellationToken cancellationToken);
    Task MoveFolderAsync(long id, long? parentId, CancellationToken cancellationToken);
    /// <summary>Appends assets not already in the album; returns how many were added.</summary>
    Task<int> AddAssetsAsync(long albumId, IReadOnlyCollection<long> assetIds, CancellationToken cancellationToken);
    Task RemoveAssetsAsync(long albumId, IReadOnlyCollection<long> assetIds, CancellationToken cancellationToken);
    /// <summary>Moves the given asset to <paramref name="newIndex"/> within the manual order.</summary>
    Task MoveAssetAsync(long albumId, long assetId, int newIndex, CancellationToken cancellationToken);
    Task<IReadOnlyList<long>> GetAlbumAssetIdsAsync(long albumId, CancellationToken cancellationToken);
    Task<IReadOnlyList<Album>> GetAlbumsContainingAsync(long assetId, CancellationToken cancellationToken);
}

public sealed record ImportJob(
    long Id,
    Guid Uid,
    ImportMode Mode,
    ImportJobStatus Status,
    DateTime StartedUtc,
    DateTime? FinishedUtc,
    int Total,
    int Processed,
    int Imported,
    int Duplicates,
    int Skipped,
    int Failed,
    bool ImportDuplicates);

public sealed record ImportItem(
    long Id,
    long JobId,
    string SourcePath,
    ImportItemStatus Status,
    long? AssetId,
    string? Message,
    string? TargetPath);

public interface IImportJobRepository
{
    Task<long> CreateJobAsync(ImportMode mode, bool importDuplicates, IReadOnlyList<string> sourcePaths, DateTime startedUtc, CancellationToken cancellationToken);
    Task<ImportJob?> GetJobAsync(long jobId, CancellationToken cancellationToken);
    Task<IReadOnlyList<ImportJob>> GetJobsAsync(int limit, CancellationToken cancellationToken);
    Task<IReadOnlyList<ImportItem>> GetItemsAsync(long jobId, ImportItemStatus? status, CancellationToken cancellationToken);
    Task UpdateItemAsync(long itemId, ImportItemStatus status, long? assetId, string? message, string? targetPath, CancellationToken cancellationToken);
    Task SetJobStatusAsync(long jobId, ImportJobStatus status, DateTime? finishedUtc, CancellationToken cancellationToken);
    /// <summary>Recomputes job counters from its items.</summary>
    Task RefreshCountersAsync(long jobId, CancellationToken cancellationToken);
    /// <summary>Marks jobs left in Running state (app crash/close) as Interrupted. Returns their ids.</summary>
    Task<IReadOnlyList<long>> MarkRunningAsInterruptedAsync(CancellationToken cancellationToken);
}

public sealed record StoredRecipe(EditRecipe Recipe, int Version, DateTime UpdatedUtc);

public interface IEditRepository
{
    Task<StoredRecipe?> GetAsync(long assetId, CancellationToken cancellationToken);
    /// <summary>Saves the recipe (identity recipes clear the edit) and appends it to history. Returns the new version.</summary>
    Task<int> SaveAsync(long assetId, EditRecipe recipe, DateTime utcNow, CancellationToken cancellationToken);
    Task<IReadOnlyList<StoredRecipe>> GetHistoryAsync(long assetId, int limit, CancellationToken cancellationToken);
}

public interface IDuplicateDismissalRepository
{
    Task DismissAsync(long assetA, long assetB, CancellationToken cancellationToken);
    Task<IReadOnlySet<(long, long)>> GetDismissedAsync(CancellationToken cancellationToken);
}
