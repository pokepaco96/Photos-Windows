using System.Text.Json;
using System.Text.Json.Serialization;
using Dapper;
using Microsoft.Data.Sqlite;
using PhotosWindows.Application.Abstractions;
using PhotosWindows.Domain;

namespace PhotosWindows.Infrastructure.Persistence;

public sealed class AlbumRepository : IAlbumRepository
{
    internal static readonly JsonSerializerOptions JsonOptions = new() { Converters = { new JsonStringEnumConverter() } };

    private readonly SqliteDb _db;
    private readonly IClock _clock;

    public AlbumRepository(SqliteDb db, IClock clock)
    {
        _db = db;
        _clock = clock;
    }

    private const string AlbumSelect = """
        SELECT al.id, al.uid, al.name, al.kind, al.folder_id, al.smart_json, al.sort_order, al.created_utc,
            (SELECT count(*) FROM album_assets x JOIN assets a ON a.id = x.asset_id WHERE x.album_id = al.id AND a.deleted_utc IS NULL AND a.is_hidden = 0) AS item_count,
            (SELECT x.asset_id FROM album_assets x JOIN assets a ON a.id = x.asset_id WHERE x.album_id = al.id AND a.deleted_utc IS NULL AND a.is_hidden = 0 ORDER BY x.position LIMIT 1) AS cover_asset_id
        FROM albums al
        """;

    public Task<IReadOnlyList<Album>> GetAlbumsAsync(CancellationToken cancellationToken) =>
        _db.ReadAsync<IReadOnlyList<Album>>(async c =>
            (await c.QueryAsync<AlbumRow>(AlbumSelect + " ORDER BY al.sort_order, al.name COLLATE NOCASE").ConfigureAwait(false)).Select(r => r.ToAlbum()).ToList(),
            cancellationToken);

    public Task<Album?> GetAlbumAsync(long id, CancellationToken cancellationToken) =>
        _db.ReadAsync(async c => (await c.QuerySingleOrDefaultAsync<AlbumRow>(AlbumSelect + " WHERE al.id = @id", new { id }).ConfigureAwait(false))?.ToAlbum(), cancellationToken);

    public Task<IReadOnlyList<AlbumFolder>> GetFoldersAsync(CancellationToken cancellationToken) =>
        _db.ReadAsync<IReadOnlyList<AlbumFolder>>(async c =>
            (await c.QueryAsync<(long Id, string Name, long? ParentId, long SortOrder)>("SELECT id, name, parent_id, sort_order FROM album_folders ORDER BY sort_order, name COLLATE NOCASE").ConfigureAwait(false))
            .Select(r => new AlbumFolder { Id = r.Id, Name = r.Name, ParentId = r.ParentId, SortOrder = (int)r.SortOrder }).ToList(),
            cancellationToken);

    public Task<long> CreateAlbumAsync(string name, AlbumKind kind, long? folderId, SmartAlbumDefinition? definition, CancellationToken cancellationToken)
    {
        var normalized = LibraryRules.NormalizeName(name, nameof(name));
        if (kind == AlbumKind.Smart && definition is null)
        {
            throw new ArgumentException("A smart album needs a rule definition.", nameof(definition));
        }

        return _db.WriteAsync((c, t) => c.ExecuteScalarAsync<long>("""
            INSERT INTO albums(uid, name, kind, folder_id, smart_json, sort_order, created_utc)
            VALUES (@uid, @name, @kind, @folderId, @json, coalesce((SELECT max(sort_order) + 1 FROM albums), 0), @now);
            SELECT last_insert_rowid();
            """, new
        {
            uid = Guid.NewGuid().ToString("D"),
            name = normalized,
            kind = (int)kind,
            folderId,
            json = kind == AlbumKind.Smart ? JsonSerializer.Serialize(definition, JsonOptions) : null,
            now = DbFormat.Utc(_clock.UtcNow),
        }, t), cancellationToken);
    }

    public Task RenameAlbumAsync(long id, string name, CancellationToken cancellationToken)
    {
        var normalized = LibraryRules.NormalizeName(name, nameof(name));
        return _db.WriteAsync(async (c, t) =>
        {
            await c.ExecuteAsync("UPDATE albums SET name = @normalized WHERE id = @id", new { id, normalized }, t).ConfigureAwait(false);
            await RefreshAlbumFtsAsync(c, t, id).ConfigureAwait(false);
        }, cancellationToken);
    }

    public Task UpdateSmartDefinitionAsync(long id, SmartAlbumDefinition definition, CancellationToken cancellationToken) =>
        _db.WriteAsync((c, t) => c.ExecuteAsync("UPDATE albums SET smart_json = @json WHERE id = @id AND kind = 1", new { id, json = JsonSerializer.Serialize(definition, JsonOptions) }, t), cancellationToken);

    public Task DeleteAlbumAsync(long id, CancellationToken cancellationToken) =>
        _db.WriteAsync(async (c, t) =>
        {
            var members = (await c.QueryAsync<long>("SELECT asset_id FROM album_assets WHERE album_id = @id", new { id }, t).ConfigureAwait(false)).ToList();
            await c.ExecuteAsync("DELETE FROM albums WHERE id = @id", new { id }, t).ConfigureAwait(false);
            await AssetRepository.RefreshFtsAsync(c, t, members).ConfigureAwait(false);
        }, cancellationToken);

    public Task MoveAlbumAsync(long id, long? folderId, CancellationToken cancellationToken) =>
        _db.WriteAsync((c, t) => c.ExecuteAsync("UPDATE albums SET folder_id = @folderId WHERE id = @id", new { id, folderId }, t), cancellationToken);

    public Task<long> CreateFolderAsync(string name, long? parentId, CancellationToken cancellationToken)
    {
        var normalized = LibraryRules.NormalizeName(name, nameof(name));
        return _db.WriteAsync((c, t) => c.ExecuteScalarAsync<long>(
            "INSERT INTO album_folders(name, parent_id, sort_order) VALUES (@normalized, @parentId, coalesce((SELECT max(sort_order) + 1 FROM album_folders), 0)); SELECT last_insert_rowid();",
            new { normalized, parentId }, t), cancellationToken);
    }

    public Task RenameFolderAsync(long id, string name, CancellationToken cancellationToken)
    {
        var normalized = LibraryRules.NormalizeName(name, nameof(name));
        return _db.WriteAsync((c, t) => c.ExecuteAsync("UPDATE album_folders SET name = @normalized WHERE id = @id", new { id, normalized }, t), cancellationToken);
    }

    public Task DeleteFolderAsync(long id, CancellationToken cancellationToken) =>
        _db.WriteAsync(async (c, t) =>
        {
            var parent = await c.ExecuteScalarAsync<long?>("SELECT parent_id FROM album_folders WHERE id = @id", new { id }, t).ConfigureAwait(false);
            await c.ExecuteAsync("UPDATE albums SET folder_id = @parent WHERE folder_id = @id", new { id, parent }, t).ConfigureAwait(false);
            await c.ExecuteAsync("UPDATE album_folders SET parent_id = @parent WHERE parent_id = @id", new { id, parent }, t).ConfigureAwait(false);
            await c.ExecuteAsync("DELETE FROM album_folders WHERE id = @id", new { id }, t).ConfigureAwait(false);
        }, cancellationToken);

    public Task MoveFolderAsync(long id, long? parentId, CancellationToken cancellationToken) =>
        _db.WriteAsync(async (c, t) =>
        {
            // Refuse cycles: the new parent cannot be the folder itself or one of its descendants.
            var cursor = parentId;
            while (cursor is { } current)
            {
                if (current == id)
                {
                    throw new InvalidOperationException("A folder cannot be moved inside itself.");
                }

                cursor = await c.ExecuteScalarAsync<long?>("SELECT parent_id FROM album_folders WHERE id = @current", new { current }, t).ConfigureAwait(false);
            }

            await c.ExecuteAsync("UPDATE album_folders SET parent_id = @parentId WHERE id = @id", new { id, parentId }, t).ConfigureAwait(false);
        }, cancellationToken);

    public Task<int> AddAssetsAsync(long albumId, IReadOnlyCollection<long> assetIds, CancellationToken cancellationToken) =>
        _db.WriteAsync(async (c, t) =>
        {
            await EnsureManualAsync(c, t, albumId).ConfigureAwait(false);
            var next = await c.ExecuteScalarAsync<long>("SELECT coalesce(max(position) + 1, 0) FROM album_assets WHERE album_id = @albumId", new { albumId }, t).ConfigureAwait(false);
            var added = 0;
            var now = DbFormat.Utc(_clock.UtcNow);
            foreach (var assetId in assetIds.Distinct())
            {
                var rows = await c.ExecuteAsync(
                    "INSERT OR IGNORE INTO album_assets(album_id, asset_id, position, added_utc) SELECT @albumId, @assetId, @position, @now WHERE EXISTS (SELECT 1 FROM assets WHERE id = @assetId)",
                    new { albumId, assetId, position = next, now }, t).ConfigureAwait(false);
                if (rows > 0)
                {
                    added++;
                    next++;
                }
            }

            await AssetRepository.RefreshFtsAsync(c, t, assetIds).ConfigureAwait(false);
            return added;
        }, cancellationToken);

    public Task RemoveAssetsAsync(long albumId, IReadOnlyCollection<long> assetIds, CancellationToken cancellationToken) =>
        _db.WriteAsync(async (c, t) =>
        {
            await c.ExecuteAsync("DELETE FROM album_assets WHERE album_id = @albumId AND asset_id IN (SELECT value FROM json_each(@ids))", new { albumId, ids = AssetRepository.Json(assetIds) }, t).ConfigureAwait(false);
            await AssetRepository.RefreshFtsAsync(c, t, assetIds).ConfigureAwait(false);
        }, cancellationToken);

    public Task MoveAssetAsync(long albumId, long assetId, int newIndex, CancellationToken cancellationToken) =>
        _db.WriteAsync(async (c, t) =>
        {
            var order = (await c.QueryAsync<long>("SELECT asset_id FROM album_assets WHERE album_id = @albumId ORDER BY position", new { albumId }, t).ConfigureAwait(false)).ToList();
            if (!order.Remove(assetId))
            {
                throw new InvalidOperationException("The item is not part of this album.");
            }

            order.Insert(Math.Clamp(newIndex, 0, order.Count), assetId);
            for (var i = 0; i < order.Count; i++)
            {
                await c.ExecuteAsync("UPDATE album_assets SET position = @i WHERE album_id = @albumId AND asset_id = @id", new { i, albumId, id = order[i] }, t).ConfigureAwait(false);
            }
        }, cancellationToken);

    public Task<IReadOnlyList<long>> GetAlbumAssetIdsAsync(long albumId, CancellationToken cancellationToken) =>
        _db.ReadAsync<IReadOnlyList<long>>(async c =>
            (await c.QueryAsync<long>("SELECT asset_id FROM album_assets WHERE album_id = @albumId ORDER BY position", new { albumId }).ConfigureAwait(false)).ToList(),
            cancellationToken);

    public Task<IReadOnlyList<Album>> GetAlbumsContainingAsync(long assetId, CancellationToken cancellationToken) =>
        _db.ReadAsync<IReadOnlyList<Album>>(async c =>
            (await c.QueryAsync<AlbumRow>(AlbumSelect + " WHERE al.id IN (SELECT album_id FROM album_assets WHERE asset_id = @assetId) ORDER BY al.name COLLATE NOCASE", new { assetId }).ConfigureAwait(false))
            .Select(r => r.ToAlbum()).ToList(),
            cancellationToken);

    private static async Task EnsureManualAsync(SqliteConnection c, SqliteTransaction t, long albumId)
    {
        var kind = await c.ExecuteScalarAsync<long?>("SELECT kind FROM albums WHERE id = @albumId", new { albumId }, t).ConfigureAwait(false)
            ?? throw new InvalidOperationException("The album no longer exists.");
        if (kind != (long)AlbumKind.Manual)
        {
            throw new InvalidOperationException("Items cannot be added manually to a smart album.");
        }
    }

    private static async Task RefreshAlbumFtsAsync(SqliteConnection c, SqliteTransaction t, long albumId)
    {
        var members = (await c.QueryAsync<long>("SELECT asset_id FROM album_assets WHERE album_id = @albumId", new { albumId }, t).ConfigureAwait(false)).ToList();
        await AssetRepository.RefreshFtsAsync(c, t, members).ConfigureAwait(false);
    }

#pragma warning disable CA1812
    private sealed class AlbumRow
    {
        public long Id { get; set; }
        public string Uid { get; set; } = "";
        public string Name { get; set; } = "";
        public long Kind { get; set; }
        public long? FolderId { get; set; }
        public string? SmartJson { get; set; }
        public long SortOrder { get; set; }
        public string CreatedUtc { get; set; } = "";
        public long ItemCount { get; set; }
        public long? CoverAssetId { get; set; }

        public Album ToAlbum() => new()
        {
            Id = Id,
            Uid = Guid.Parse(Uid),
            Name = Name,
            Kind = (AlbumKind)Kind,
            FolderId = FolderId,
            SmartDefinition = SmartJson is null ? null : JsonSerializer.Deserialize<SmartAlbumDefinition>(SmartJson, JsonOptions),
            SortOrder = (int)SortOrder,
            CreatedUtc = DbFormat.ParseUtc(CreatedUtc),
            ItemCount = (int)ItemCount,
            CoverAssetId = CoverAssetId,
        };
    }
#pragma warning restore CA1812
}
