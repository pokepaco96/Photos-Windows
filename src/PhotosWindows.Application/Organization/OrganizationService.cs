using Microsoft.Extensions.Logging;
using PhotosWindows.Application.Abstractions;
using PhotosWindows.Application.Thumbnails;
using PhotosWindows.Domain;

namespace PhotosWindows.Application.Organization;

public sealed record PlannedFileDeletion(long AssetId, string AbsolutePath, StorageMode StorageMode, FileRole Role, long SizeBytes, bool WillDelete);

/// <summary>What a permanent deletion will do, shown to the user before confirming.</summary>
public sealed record DeletionPlan(IReadOnlyList<long> AssetIds, IReadOnlyList<PlannedFileDeletion> Files, bool IncludeReferencedFiles, bool FilesAreRecoverable)
{
    public int PhysicalFileCount => Files.Count(f => f.WillDelete);
    public long PhysicalBytes => Files.Where(f => f.WillDelete).Sum(f => f.SizeBytes);
    public int KeptReferencedFiles => Files.Count(f => !f.WillDelete);
}

public sealed record DeletionResult(int AssetsRemoved, int FilesDeleted, IReadOnlyList<string> Errors);

/// <summary>
/// Albums, keywords, favourites, hidden items and the deletion lifecycle:
/// Hide → only hidden from normal views; Move to Recently Deleted → logical trash, recoverable;
/// Delete permanently → explicit, previewed: managed copies are deleted, referenced originals are kept
/// unless the user explicitly opts in for that batch.
/// </summary>
public sealed class OrganizationService
{
    private readonly ILibraryContext _context;
    private readonly IAssetRepository _assets;
    private readonly IAlbumRepository _albums;
    private readonly IKeywordRepository _keywords;
    private readonly IFileDeleter _deleter;
    private readonly ThumbnailService _thumbnails;
    private readonly IClock _clock;
    private readonly ILogger<OrganizationService> _logger;

    public OrganizationService(ILibraryContext context, IAssetRepository assets, IAlbumRepository albums, IKeywordRepository keywords, IFileDeleter deleter, ThumbnailService thumbnails, IClock clock, ILogger<OrganizationService> logger)
    {
        _context = context;
        _assets = assets;
        _albums = albums;
        _keywords = keywords;
        _deleter = deleter;
        _thumbnails = thumbnails;
        _clock = clock;
        _logger = logger;
    }

    /// <summary>Raised after any change that affects what grids show.</summary>
    public event EventHandler? LibraryContentChanged;

    private void Changed() => LibraryContentChanged?.Invoke(this, EventArgs.Empty);

    // ---- Flags ----
    public async Task SetFavoriteAsync(IReadOnlyCollection<long> ids, bool favorite, CancellationToken ct)
    {
        await _assets.SetFavoriteAsync(ids, favorite, ct).ConfigureAwait(false);
        Changed();
    }

    public async Task SetHiddenAsync(IReadOnlyCollection<long> ids, bool hidden, CancellationToken ct)
    {
        await _assets.SetHiddenAsync(ids, hidden, ct).ConfigureAwait(false);
        Changed();
    }

    public async Task MoveToTrashAsync(IReadOnlyCollection<long> ids, CancellationToken ct)
    {
        await _assets.MoveToTrashAsync(ids, _clock.UtcNow, ct).ConfigureAwait(false);
        Changed();
    }

    public async Task RestoreAsync(IReadOnlyCollection<long> ids, CancellationToken ct)
    {
        await _assets.RestoreFromTrashAsync(ids, ct).ConfigureAwait(false);
        Changed();
    }

    public async Task<DeletionPlan> PlanPermanentDeletionAsync(IReadOnlyCollection<long> ids, bool includeReferencedFiles, CancellationToken ct)
    {
        var library = _context.RequireCurrent();
        var files = new List<PlannedFileDeletion>();
        foreach (var id in ids)
        {
            foreach (var f in await _assets.GetFilesAsync(id, ct).ConfigureAwait(false))
            {
                var willDelete = f.StorageMode == StorageMode.Managed || includeReferencedFiles;
                files.Add(new PlannedFileDeletion(id, library.Resolve(f), f.StorageMode, f.Role, f.SizeBytes, willDelete));
            }
        }

        return new DeletionPlan(ids.ToList(), files, includeReferencedFiles, _deleter.IsRecoverable);
    }

    /// <summary>Executes a plan produced by <see cref="PlanPermanentDeletionAsync"/>. Only trashed assets are deleted.</summary>
    public async Task<DeletionResult> DeletePermanentlyAsync(DeletionPlan plan, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(plan);
        var library = _context.RequireCurrent();
        var assets = await _assets.GetManyAsync(plan.AssetIds, ct).ConfigureAwait(false);
        var trashed = assets.Where(a => a.IsInTrash).ToList();
        if (trashed.Count != assets.Count)
        {
            throw new InvalidOperationException("Only items in Recently Deleted can be deleted permanently.");
        }

        var removedFiles = await _assets.RemoveFromCatalogAsync(trashed.Select(a => a.Id).ToList(), ct).ConfigureAwait(false);
        var errors = new List<string>();
        var deleted = 0;
        foreach (var file in removedFiles)
        {
            var planned = plan.Files.FirstOrDefault(p => p.AssetId == file.AssetId && string.Equals(p.AbsolutePath, library.Resolve(file), StringComparison.OrdinalIgnoreCase));
            if (planned is not { WillDelete: true }) continue;
            var path = library.Resolve(file);
            // Defence in depth: managed deletions must stay inside the library's originals folder.
            if (file.StorageMode == StorageMode.Managed && !Common.PathSafety.IsUnder(path, library.OriginalsPath))
            {
                errors.Add($"Skipped (outside the library): {path}");
                continue;
            }

            try
            {
                if (File.Exists(path))
                {
                    _deleter.Delete(path);
                    deleted++;
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or OperationCanceledException)
            {
                errors.Add($"{path}: {ex.Message}");
                _logger.LogWarning(ex, "Could not delete a file during permanent deletion");
            }
        }

        _thumbnails.DeleteFor(trashed.Select(a => a.Uid));
        Changed();
        return new DeletionResult(trashed.Count, deleted, errors);
    }

    // ---- Details ----
    public async Task UpdateDetailsAsync(long id, AssetDetailsUpdate update, CancellationToken ct)
    {
        await _assets.UpdateDetailsAsync(id, update, ct).ConfigureAwait(false);
        Changed();
    }

    public async Task SetCaptureDateAsync(long id, DateTime localDateTime, TimeSpan? offset, CancellationToken ct)
    {
        await _assets.UpdateCaptureDateAsync(id, new CaptureDate(localDateTime, offset, DateSource.Manual), ct).ConfigureAwait(false);
        Changed();
    }

    // ---- Keywords ----
    public Task<IReadOnlyList<Keyword>> GetKeywordsAsync(CancellationToken ct) => _keywords.GetAllAsync(ct);

    public Task<IReadOnlyList<string>> GetKeywordsForAsync(long assetId, CancellationToken ct) => _keywords.GetForAssetAsync(assetId, ct);

    public async Task AddKeywordsAsync(IReadOnlyCollection<long> ids, IReadOnlyCollection<string> keywords, CancellationToken ct)
    {
        await _keywords.AddAsync(ids, keywords, ct).ConfigureAwait(false);
        Changed();
    }

    public async Task RemoveKeywordAsync(IReadOnlyCollection<long> ids, string keyword, CancellationToken ct)
    {
        await _keywords.RemoveAsync(ids, keyword, ct).ConfigureAwait(false);
        Changed();
    }

    public Task RenameKeywordAsync(long id, string name, CancellationToken ct) => _keywords.RenameAsync(id, name, ct);

    public Task DeleteKeywordAsync(long id, CancellationToken ct) => _keywords.DeleteAsync(id, ct);

    // ---- Albums ----
    public Task<IReadOnlyList<Album>> GetAlbumsAsync(CancellationToken ct) => _albums.GetAlbumsAsync(ct);

    public Task<IReadOnlyList<AlbumFolder>> GetFoldersAsync(CancellationToken ct) => _albums.GetFoldersAsync(ct);

    public Task<Album?> GetAlbumAsync(long id, CancellationToken ct) => _albums.GetAlbumAsync(id, ct);

    public Task<IReadOnlyList<Album>> GetAlbumsContainingAsync(long assetId, CancellationToken ct) => _albums.GetAlbumsContainingAsync(assetId, ct);

    public async Task<long> CreateAlbumAsync(string name, long? folderId, IReadOnlyCollection<long>? initialAssets, CancellationToken ct)
    {
        var id = await _albums.CreateAlbumAsync(name, AlbumKind.Manual, folderId, null, ct).ConfigureAwait(false);
        if (initialAssets is { Count: > 0 }) await _albums.AddAssetsAsync(id, initialAssets, ct).ConfigureAwait(false);
        Changed();
        return id;
    }

    public async Task<long> CreateSmartAlbumAsync(string name, long? folderId, SmartAlbumDefinition definition, CancellationToken ct)
    {
        ValidateSmart(definition);
        var id = await _albums.CreateAlbumAsync(name, AlbumKind.Smart, folderId, definition, ct).ConfigureAwait(false);
        Changed();
        return id;
    }

    public async Task UpdateSmartAlbumAsync(long id, SmartAlbumDefinition definition, CancellationToken ct)
    {
        ValidateSmart(definition);
        await _albums.UpdateSmartDefinitionAsync(id, definition, ct).ConfigureAwait(false);
        Changed();
    }

    public async Task RenameAlbumAsync(long id, string name, CancellationToken ct)
    {
        await _albums.RenameAlbumAsync(id, name, ct).ConfigureAwait(false);
        Changed();
    }

    /// <summary>Deletes the album only; its photos stay in the library.</summary>
    public async Task DeleteAlbumAsync(long id, CancellationToken ct)
    {
        await _albums.DeleteAlbumAsync(id, ct).ConfigureAwait(false);
        Changed();
    }

    public async Task MoveAlbumAsync(long id, long? folderId, CancellationToken ct)
    {
        await _albums.MoveAlbumAsync(id, folderId, ct).ConfigureAwait(false);
        Changed();
    }

    public async Task<long> CreateFolderAsync(string name, long? parentId, CancellationToken ct)
    {
        var id = await _albums.CreateFolderAsync(name, parentId, ct).ConfigureAwait(false);
        Changed();
        return id;
    }

    public async Task RenameFolderAsync(long id, string name, CancellationToken ct)
    {
        await _albums.RenameFolderAsync(id, name, ct).ConfigureAwait(false);
        Changed();
    }

    public async Task DeleteFolderAsync(long id, CancellationToken ct)
    {
        await _albums.DeleteFolderAsync(id, ct).ConfigureAwait(false);
        Changed();
    }

    public async Task MoveFolderAsync(long id, long? parentId, CancellationToken ct)
    {
        await _albums.MoveFolderAsync(id, parentId, ct).ConfigureAwait(false);
        Changed();
    }

    public async Task<int> AddToAlbumAsync(long albumId, IReadOnlyCollection<long> ids, CancellationToken ct)
    {
        var added = await _albums.AddAssetsAsync(albumId, ids, ct).ConfigureAwait(false);
        Changed();
        return added;
    }

    public async Task RemoveFromAlbumAsync(long albumId, IReadOnlyCollection<long> ids, CancellationToken ct)
    {
        await _albums.RemoveAssetsAsync(albumId, ids, ct).ConfigureAwait(false);
        Changed();
    }

    public async Task MoveInAlbumAsync(long albumId, long assetId, int newIndex, CancellationToken ct)
    {
        await _albums.MoveAssetAsync(albumId, assetId, newIndex, ct).ConfigureAwait(false);
        Changed();
    }

    private static void ValidateSmart(SmartAlbumDefinition definition)
    {
        ArgumentNullException.ThrowIfNull(definition);
        if (definition.Rules.Count == 0)
        {
            throw new ArgumentException("A smart album needs at least one rule.", nameof(definition));
        }

        if (definition.Rules.Count > 30)
        {
            throw new ArgumentException("A smart album can have at most 30 rules.", nameof(definition));
        }

        foreach (var rule in definition.Rules)
        {
            if (SmartRuleValidator.Validate(rule) is { } error)
            {
                throw new ArgumentException(error, nameof(definition));
            }
        }
    }
}
