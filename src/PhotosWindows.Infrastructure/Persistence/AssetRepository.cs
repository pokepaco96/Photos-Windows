using System.Text.Json;
using Dapper;
using Microsoft.Data.Sqlite;
using PhotosWindows.Application.Abstractions;
using PhotosWindows.Domain;

namespace PhotosWindows.Infrastructure.Persistence;

public sealed class AssetRepository : IAssetRepository
{
    private const string SummaryColumns = """
        a.id, a.uid, a.media_type, a.capture_local, a.pixel_width, a.pixel_height, a.orientation, a.duration_ms,
        a.is_favorite, a.edit_version, a.has_live_video, a.has_raw_pair, a.original_available, a.thumb_state, a.file_name, a.deleted_utc
        """;

    private readonly SqliteDb _db;
    private readonly IClock _clock;

    public AssetRepository(SqliteDb db, IClock clock)
    {
        _db = db;
        _clock = clock;
    }

    public Task<long> InsertAsync(NewAssetRecord record, CancellationToken cancellationToken) =>
        _db.WriteAsync(async (c, t) =>
        {
            var a = record.Asset;
            var id = await c.ExecuteScalarAsync<long>("""
                INSERT INTO assets (uid, media_type, capture_local, capture_offset_min, date_source, imported_utc, file_modified_utc,
                    pixel_width, pixel_height, orientation, duration_ms, file_name, format, file_size,
                    camera_make, camera_model, lens, f_number, exposure_s, iso, focal_mm, latitude, longitude, altitude,
                    title, description, rating, is_favorite, is_hidden, has_live_video, has_raw_pair, thumb_state, metadata_json)
                VALUES (@Uid, @MediaType, @CaptureLocal, @OffsetMin, @DateSource, @Imported, @Modified,
                    @W, @H, @Orientation, @DurationMs, @FileName, @Format, @FileSize,
                    @Make, @Model, @Lens, @FNumber, @Exposure, @Iso, @Focal, @Lat, @Lon, @Alt,
                    @Title, @Description, @Rating, @Favorite, @Hidden, @Live, @RawPair, @Thumb, NULL);
                SELECT last_insert_rowid();
                """, new
            {
                Uid = a.Uid.ToString("D"),
                MediaType = (int)a.MediaType,
                CaptureLocal = DbFormat.Local(a.CaptureDate.LocalDateTime),
                OffsetMin = a.CaptureDate.UtcOffset is { } o ? (int?)o.TotalMinutes : null,
                DateSource = (int)a.CaptureDate.Source,
                Imported = DbFormat.Utc(a.ImportedUtc),
                Modified = DbFormat.Utc(a.FileModifiedUtc),
                W = a.PixelWidth,
                H = a.PixelHeight,
                a.Orientation,
                DurationMs = a.Duration is { } d ? (long?)d.TotalMilliseconds : null,
                a.FileName,
                a.Format,
                a.FileSize,
                a.Camera.Make,
                a.Camera.Model,
                a.Camera.Lens,
                a.Camera.FNumber,
                Exposure = a.Camera.ExposureTimeSeconds,
                a.Camera.Iso,
                Focal = a.Camera.FocalLengthMm,
                Lat = a.Location?.Latitude,
                Lon = a.Location?.Longitude,
                Alt = a.Location?.Altitude,
                a.Title,
                a.Description,
                a.Rating,
                Favorite = a.IsFavorite ? 1 : 0,
                Hidden = a.IsHidden ? 1 : 0,
                Live = a.HasLiveVideo ? 1 : 0,
                RawPair = a.HasRawPair ? 1 : 0,
                Thumb = (int)a.ThumbnailState,
            }, t).ConfigureAwait(false);

            foreach (var file in record.Files)
            {
                file.AssetId = id;
                file.Id = await InsertFileAsync(c, t, file).ConfigureAwait(false);
            }

            await KeywordRepository.AddInTransactionAsync(c, t, [id], record.Keywords).ConfigureAwait(false);
            await RefreshFtsAsync(c, t, [id]).ConfigureAwait(false);
            a.Id = id;
            return id;
        }, cancellationToken);

    private static Task<long> InsertFileAsync(SqliteConnection c, SqliteTransaction t, AssetFile file) =>
        c.ExecuteScalarAsync<long>("""
            INSERT INTO asset_files (asset_id, role, storage_mode, stored_path, stored_path_key, size_bytes, sha256, file_modified_utc, format, is_available, last_verified_utc)
            VALUES (@AssetId, @Role, @Mode, @Path, @Key, @Size, @Sha, @Modified, @Format, @Available, @Verified);
            SELECT last_insert_rowid();
            """, new
        {
            file.AssetId,
            Role = (int)file.Role,
            Mode = (int)file.StorageMode,
            Path = file.StoredPath,
            Key = DbFormat.PathKey(file.StoredPath),
            Size = file.SizeBytes,
            Sha = file.Sha256,
            Modified = DbFormat.Utc(file.FileModifiedUtc),
            file.Format,
            Available = file.IsAvailable ? 1 : 0,
            Verified = DbFormat.Utc(file.LastVerifiedUtc == default ? DateTime.UtcNow : file.LastVerifiedUtc),
        }, t);

    public Task AttachFileAsync(AssetFile file, CancellationToken cancellationToken) =>
        _db.WriteAsync(async (c, t) =>
        {
            file.Id = await InsertFileAsync(c, t, file).ConfigureAwait(false);
            var column = file.Role switch
            {
                FileRole.LiveVideo => "has_live_video",
                FileRole.RawPair => "has_raw_pair",
                _ => null,
            };
            if (column is not null)
            {
                await c.ExecuteAsync($"UPDATE assets SET {column} = 1 WHERE id = @id", new { id = file.AssetId }, t).ConfigureAwait(false);
            }
        }, cancellationToken);

    public Task<Asset?> GetAsync(long id, CancellationToken cancellationToken) =>
        _db.ReadAsync(async c =>
        {
            var row = await c.QuerySingleOrDefaultAsync<AssetRow>("SELECT * FROM assets WHERE id = @id", new { id }).ConfigureAwait(false);
            return row?.ToAsset();
        }, cancellationToken);

    public Task<IReadOnlyList<Asset>> GetManyAsync(IReadOnlyCollection<long> ids, CancellationToken cancellationToken) =>
        _db.ReadAsync<IReadOnlyList<Asset>>(async c =>
        {
            var rows = await c.QueryAsync<AssetRow>("SELECT * FROM assets WHERE id IN (SELECT value FROM json_each(@ids))", new { ids = Json(ids) }).ConfigureAwait(false);
            return rows.Select(r => r.ToAsset()).ToList();
        }, cancellationToken);

    public Task<IReadOnlyList<AssetFile>> GetFilesAsync(long assetId, CancellationToken cancellationToken) =>
        _db.ReadAsync<IReadOnlyList<AssetFile>>(async c =>
            (await c.QueryAsync<FileRow>("SELECT * FROM asset_files WHERE asset_id = @assetId ORDER BY role, id", new { assetId }).ConfigureAwait(false))
            .Select(r => r.ToFile()).ToList(), cancellationToken);

    public Task<AssetFile?> GetOriginalFileAsync(long assetId, CancellationToken cancellationToken) =>
        _db.ReadAsync(async c =>
            (await c.QueryFirstOrDefaultAsync<FileRow>("SELECT * FROM asset_files WHERE asset_id = @assetId AND role = 0 ORDER BY id LIMIT 1", new { assetId }).ConfigureAwait(false))?.ToFile(),
            cancellationToken);

    public Task<IReadOnlyList<AssetSummary>> QueryAsync(AssetQuery query, CancellationToken cancellationToken) =>
        _db.ReadAsync<IReadOnlyList<AssetSummary>>(async c =>
        {
            var sql = AssetQuerySql.Build(query, _clock.UtcNow);
            var limit = query.Limit is { } l ? $" LIMIT {Math.Max(0, l)}" : string.Empty;
            var text = $"SELECT {SummaryColumns} FROM assets a {sql.Joins} {sql.WhereClause} {AssetQuerySql.OrderBy(query.Sort, sql.Joins.Length > 0)}{limit}";
            var command = new CommandDefinition(text, sql.Parameters, cancellationToken: cancellationToken);
            var rows = await c.QueryAsync<SummaryRow>(command).ConfigureAwait(false);
            return rows.Select(r => r.ToSummary()).ToList();
        }, cancellationToken);

    public Task<int> CountAsync(AssetQuery query, CancellationToken cancellationToken) =>
        _db.ReadAsync(async c =>
        {
            var sql = AssetQuerySql.Build(query, _clock.UtcNow);
            return (int)await c.ExecuteScalarAsync<long>($"SELECT count(*) FROM assets a {sql.Joins} {sql.WhereClause}", sql.Parameters).ConfigureAwait(false);
        }, cancellationToken);

    public Task<AssetFile?> FindFileByStoredPathAsync(string storedPath, CancellationToken cancellationToken) =>
        _db.ReadAsync(async c =>
            (await c.QuerySingleOrDefaultAsync<FileRow>("SELECT * FROM asset_files WHERE stored_path_key = @key", new { key = DbFormat.PathKey(storedPath) }).ConfigureAwait(false))?.ToFile(),
            cancellationToken);

    public Task<IReadOnlyList<AssetFile>> FindFilesByHashAsync(string sha256, CancellationToken cancellationToken) =>
        _db.ReadAsync<IReadOnlyList<AssetFile>>(async c =>
            (await c.QueryAsync<FileRow>("SELECT * FROM asset_files WHERE sha256 = @sha256", new { sha256 }).ConfigureAwait(false)).Select(r => r.ToFile()).ToList(),
            cancellationToken);

    public Task SetFavoriteAsync(IReadOnlyCollection<long> ids, bool favorite, CancellationToken cancellationToken) =>
        _db.WriteAsync((c, t) => c.ExecuteAsync("UPDATE assets SET is_favorite = @v WHERE id IN (SELECT value FROM json_each(@ids))", new { v = favorite ? 1 : 0, ids = Json(ids) }, t), cancellationToken);

    public Task SetHiddenAsync(IReadOnlyCollection<long> ids, bool hidden, CancellationToken cancellationToken) =>
        _db.WriteAsync((c, t) => c.ExecuteAsync("UPDATE assets SET is_hidden = @v WHERE id IN (SELECT value FROM json_each(@ids))", new { v = hidden ? 1 : 0, ids = Json(ids) }, t), cancellationToken);

    public Task MoveToTrashAsync(IReadOnlyCollection<long> ids, DateTime deletedUtc, CancellationToken cancellationToken) =>
        _db.WriteAsync((c, t) => c.ExecuteAsync("UPDATE assets SET deleted_utc = @d WHERE deleted_utc IS NULL AND id IN (SELECT value FROM json_each(@ids))", new { d = DbFormat.Utc(deletedUtc), ids = Json(ids) }, t), cancellationToken);

    public Task RestoreFromTrashAsync(IReadOnlyCollection<long> ids, CancellationToken cancellationToken) =>
        _db.WriteAsync((c, t) => c.ExecuteAsync("UPDATE assets SET deleted_utc = NULL WHERE id IN (SELECT value FROM json_each(@ids))", new { ids = Json(ids) }, t), cancellationToken);

    public Task<IReadOnlyList<AssetFile>> RemoveFromCatalogAsync(IReadOnlyCollection<long> ids, CancellationToken cancellationToken) =>
        _db.WriteAsync<IReadOnlyList<AssetFile>>(async (c, t) =>
        {
            var p = new { ids = Json(ids) };
            var files = (await c.QueryAsync<FileRow>("SELECT * FROM asset_files WHERE asset_id IN (SELECT value FROM json_each(@ids))", p, t).ConfigureAwait(false)).Select(r => r.ToFile()).ToList();
            await c.ExecuteAsync("DELETE FROM assets_fts WHERE rowid IN (SELECT value FROM json_each(@ids))", p, t).ConfigureAwait(false);
            await c.ExecuteAsync("DELETE FROM assets WHERE id IN (SELECT value FROM json_each(@ids))", p, t).ConfigureAwait(false);
            return files;
        }, cancellationToken);

    public Task UpdateDetailsAsync(long id, AssetDetailsUpdate update, CancellationToken cancellationToken) =>
        _db.WriteAsync(async (c, t) =>
        {
            await c.ExecuteAsync("""
                UPDATE assets SET title = @Title, description = @Description, rating = @Rating,
                    latitude  = CASE WHEN @Clear = 1 THEN NULL WHEN @Lat IS NULL THEN latitude ELSE @Lat END,
                    longitude = CASE WHEN @Clear = 1 THEN NULL WHEN @Lat IS NULL THEN longitude ELSE @Lon END,
                    altitude  = CASE WHEN @Clear = 1 THEN NULL WHEN @Lat IS NULL THEN altitude ELSE @Alt END
                WHERE id = @id
                """, new
            {
                id,
                Title = string.IsNullOrWhiteSpace(update.Title) ? null : update.Title.Trim(),
                Description = string.IsNullOrWhiteSpace(update.Description) ? null : update.Description.Trim(),
                Rating = Math.Clamp(update.Rating, 0, 5),
                Clear = update.ClearLocation ? 1 : 0,
                Lat = update.Location?.Latitude,
                Lon = update.Location?.Longitude,
                Alt = update.Location?.Altitude,
            }, t).ConfigureAwait(false);
            await RefreshFtsAsync(c, t, [id]).ConfigureAwait(false);
        }, cancellationToken);

    public Task UpdateCaptureDateAsync(long id, CaptureDate date, CancellationToken cancellationToken) =>
        _db.WriteAsync((c, t) => c.ExecuteAsync(
            "UPDATE assets SET capture_local = @local, capture_offset_min = @offset, date_source = @source WHERE id = @id",
            new { id, local = DbFormat.Local(date.LocalDateTime), offset = date.UtcOffset is { } o ? (int?)o.TotalMinutes : null, source = (int)date.Source }, t), cancellationToken);

    public Task SetThumbnailStateAsync(long id, ThumbnailState state, CancellationToken cancellationToken) =>
        _db.WriteAsync((c, t) => c.ExecuteAsync("UPDATE assets SET thumb_state = @s WHERE id = @id", new { id, s = (int)state }, t), cancellationToken);

    public Task SetPerceptualHashAsync(long id, ulong hash, CancellationToken cancellationToken) =>
        _db.WriteAsync((c, t) => c.ExecuteAsync("UPDATE assets SET phash = @h WHERE id = @id", new { id, h = unchecked((long)hash) }, t), cancellationToken);

    public Task<IReadOnlyList<long>> GetIdsWithThumbnailStateAsync(ThumbnailState state, int limit, CancellationToken cancellationToken) =>
        _db.ReadAsync<IReadOnlyList<long>>(async c =>
            (await c.QueryAsync<long>("SELECT id FROM assets WHERE thumb_state = @s AND deleted_utc IS NULL ORDER BY capture_local DESC LIMIT @limit", new { s = (int)state, limit }).ConfigureAwait(false)).ToList(),
            cancellationToken);

    public Task<IReadOnlyList<(long Id, ulong Hash)>> GetPerceptualHashesAsync(CancellationToken cancellationToken) =>
        _db.ReadAsync<IReadOnlyList<(long, ulong)>>(async c =>
            (await c.QueryAsync<(long Id, long Hash)>("SELECT id, phash FROM assets WHERE phash IS NOT NULL AND deleted_utc IS NULL").ConfigureAwait(false))
            .Select(r => (r.Id, unchecked((ulong)r.Hash))).ToList(),
            cancellationToken);

    public Task<IReadOnlyList<long>> GetPhotosWithoutPerceptualHashAsync(int limit, CancellationToken cancellationToken) =>
        _db.ReadAsync<IReadOnlyList<long>>(async c =>
            (await c.QueryAsync<long>("SELECT id FROM assets WHERE phash IS NULL AND media_type = 0 AND deleted_utc IS NULL AND original_available = 1 AND thumb_state <> 2 LIMIT @limit", new { limit }).ConfigureAwait(false)).ToList(),
            cancellationToken);

    public Task UpdateFileAvailabilityAsync(long fileId, bool available, DateTime verifiedUtc, CancellationToken cancellationToken) =>
        _db.WriteAsync(async (c, t) =>
        {
            await c.ExecuteAsync("UPDATE asset_files SET is_available = @a, last_verified_utc = @v WHERE id = @fileId", new { fileId, a = available ? 1 : 0, v = DbFormat.Utc(verifiedUtc) }, t).ConfigureAwait(false);
            await c.ExecuteAsync("UPDATE assets SET original_available = @a WHERE id = (SELECT asset_id FROM asset_files WHERE id = @fileId AND role = 0)", new { fileId, a = available ? 1 : 0 }, t).ConfigureAwait(false);
        }, cancellationToken);

    public Task UpdateFileLocationAsync(long fileId, string storedPath, long size, DateTime modifiedUtc, CancellationToken cancellationToken) =>
        _db.WriteAsync(async (c, t) =>
        {
            await c.ExecuteAsync("""
                UPDATE asset_files SET stored_path = @storedPath, stored_path_key = @key, size_bytes = @size, file_modified_utc = @modified,
                    is_available = 1, last_verified_utc = @now WHERE id = @fileId
                """, new { fileId, storedPath, key = DbFormat.PathKey(storedPath), size, modified = DbFormat.Utc(modifiedUtc), now = DbFormat.Utc(_clock.UtcNow) }, t).ConfigureAwait(false);
            await c.ExecuteAsync("""
                UPDATE assets SET original_available = 1, file_name = @name
                WHERE id = (SELECT asset_id FROM asset_files WHERE id = @fileId AND role = 0)
                """, new { fileId, name = Path.GetFileName(storedPath) }, t).ConfigureAwait(false);
        }, cancellationToken);

    public Task<IReadOnlyList<AssetFile>> GetAllFilesAsync(CancellationToken cancellationToken) =>
        _db.ReadAsync<IReadOnlyList<AssetFile>>(async c =>
            (await c.QueryAsync<FileRow>("SELECT * FROM asset_files ORDER BY id").ConfigureAwait(false)).Select(r => r.ToFile()).ToList(),
            cancellationToken);

    public Task<IReadOnlyList<IReadOnlyList<long>>> GetExactDuplicateGroupsAsync(CancellationToken cancellationToken) =>
        _db.ReadAsync<IReadOnlyList<IReadOnlyList<long>>>(async c =>
        {
            var rows = await c.QueryAsync<(string Sha, long AssetId)>("""
                SELECT f.sha256, f.asset_id FROM asset_files f JOIN assets a ON a.id = f.asset_id
                WHERE f.role = 0 AND a.deleted_utc IS NULL AND f.sha256 IN (
                    SELECT f2.sha256 FROM asset_files f2 JOIN assets a2 ON a2.id = f2.asset_id
                    WHERE f2.role = 0 AND a2.deleted_utc IS NULL GROUP BY f2.sha256 HAVING count(DISTINCT f2.asset_id) > 1)
                ORDER BY f.sha256, f.asset_id
                """).ConfigureAwait(false);
            return rows.GroupBy(r => r.Sha).Select(g => (IReadOnlyList<long>)g.Select(x => x.AssetId).Distinct().ToList()).ToList();
        }, cancellationToken);

    public Task<IReadOnlyList<long>> GetTrashedBeforeAsync(DateTime cutoffUtc, CancellationToken cancellationToken) =>
        _db.ReadAsync<IReadOnlyList<long>>(async c =>
            (await c.QueryAsync<long>("SELECT id FROM assets WHERE deleted_utc IS NOT NULL AND deleted_utc < @cutoff", new { cutoff = DbFormat.Utc(cutoffUtc) }).ConfigureAwait(false)).ToList(),
            cancellationToken);

    public Task<LibraryStatistics> GetStatisticsAsync(CancellationToken cancellationToken) =>
        _db.ReadAsync(async c =>
        {
            var s = await c.QuerySingleAsync<StatsRow>("""
                SELECT
                  (SELECT count(*) FROM assets WHERE media_type = 0 AND deleted_utc IS NULL) AS photos,
                  (SELECT count(*) FROM assets WHERE media_type = 1 AND deleted_utc IS NULL) AS videos,
                  (SELECT count(*) FROM assets WHERE is_hidden = 1 AND deleted_utc IS NULL) AS hidden,
                  (SELECT count(*) FROM assets WHERE deleted_utc IS NOT NULL) AS trash,
                  (SELECT count(*) FROM albums) AS albums,
                  (SELECT coalesce(sum(size_bytes), 0) FROM asset_files WHERE storage_mode = 0) AS referenced_bytes,
                  (SELECT coalesce(sum(size_bytes), 0) FROM asset_files WHERE storage_mode = 1) AS managed_bytes,
                  (SELECT count(*) FROM asset_files WHERE is_available = 0) AS unavailable,
                  (SELECT count(*) FROM assets WHERE thumb_state = 0) AS pending_thumbs
                """).ConfigureAwait(false);
            return new LibraryStatistics((int)s.Photos, (int)s.Videos, (int)s.Hidden, (int)s.Trash, (int)s.Albums, s.ReferencedBytes, s.ManagedBytes, (int)s.Unavailable, (int)s.PendingThumbs);
        }, cancellationToken);

    public Task<IReadOnlyList<string>> GetDistinctValuesAsync(SmartField field, CancellationToken cancellationToken)
    {
        var sql = field switch
        {
            SmartField.Camera => "SELECT DISTINCT trim(CASE WHEN camera_model LIKE camera_make || '%' THEN camera_model ELSE coalesce(camera_make,'') || ' ' || coalesce(camera_model,'') END) AS v FROM assets WHERE camera_model IS NOT NULL OR camera_make IS NOT NULL ORDER BY v",
            SmartField.Lens => "SELECT DISTINCT lens FROM assets WHERE lens IS NOT NULL ORDER BY lens",
            SmartField.Format => "SELECT DISTINCT format FROM assets ORDER BY format",
            SmartField.Keyword => "SELECT name FROM keywords ORDER BY name",
            _ => throw new ArgumentOutOfRangeException(nameof(field), field, "No distinct list for this field."),
        };
        return _db.ReadAsync<IReadOnlyList<string>>(async c => (await c.QueryAsync<string>(sql).ConfigureAwait(false)).Where(v => !string.IsNullOrWhiteSpace(v)).ToList(), cancellationToken);
    }

    public Task RefreshSearchIndexAsync(IReadOnlyCollection<long>? ids, CancellationToken cancellationToken) =>
        _db.WriteAsync((c, t) => RefreshFtsAsync(c, t, ids), cancellationToken);

    /// <summary>Rebuilds FTS rows. Must be called inside the transaction that changed the indexed data.</summary>
    internal static async Task RefreshFtsAsync(SqliteConnection c, SqliteTransaction t, IReadOnlyCollection<long>? ids)
    {
        var filter = ids is null ? string.Empty : "WHERE a.id IN (SELECT value FROM json_each(@ids))";
        var deleteFilter = ids is null ? string.Empty : "WHERE rowid IN (SELECT value FROM json_each(@ids))";
        var p = new { ids = ids is null ? "[]" : Json(ids) };
        await c.ExecuteAsync($"DELETE FROM assets_fts {deleteFilter}", p, t).ConfigureAwait(false);
        await c.ExecuteAsync($"""
            INSERT INTO assets_fts (rowid, file_name, title, description, keywords, camera, lens, albums, format)
            SELECT a.id, a.file_name, coalesce(a.title, ''), coalesce(a.description, ''),
                coalesce((SELECT group_concat(k.name, ' ') FROM asset_keywords ak JOIN keywords k ON k.id = ak.keyword_id WHERE ak.asset_id = a.id), ''),
                trim(coalesce(a.camera_make, '') || ' ' || coalesce(a.camera_model, '')),
                coalesce(a.lens, ''),
                coalesce((SELECT group_concat(al.name, ' ') FROM album_assets x JOIN albums al ON al.id = x.album_id WHERE x.asset_id = a.id), ''),
                a.format
            FROM assets a {filter}
            """, p, t).ConfigureAwait(false);
    }

    internal static string Json(IEnumerable<long> ids) => JsonSerializer.Serialize(ids);

    // ---- Row types (SQLite returns INTEGER as long and REAL as double) ----
#pragma warning disable CA1812 // Instantiated by Dapper
    private sealed class StatsRow
    {
        public long Photos { get; set; }
        public long Videos { get; set; }
        public long Hidden { get; set; }
        public long Trash { get; set; }
        public long Albums { get; set; }
        public long ReferencedBytes { get; set; }
        public long ManagedBytes { get; set; }
        public long Unavailable { get; set; }
        public long PendingThumbs { get; set; }
    }

    private sealed class SummaryRow
    {
        public long Id { get; set; }
        public string Uid { get; set; } = "";
        public long MediaType { get; set; }
        public string CaptureLocal { get; set; } = "";
        public long PixelWidth { get; set; }
        public long PixelHeight { get; set; }
        public long Orientation { get; set; }
        public long? DurationMs { get; set; }
        public long IsFavorite { get; set; }
        public long EditVersion { get; set; }
        public long HasLiveVideo { get; set; }
        public long HasRawPair { get; set; }
        public long OriginalAvailable { get; set; }
        public long ThumbState { get; set; }
        public string FileName { get; set; } = "";
        public string? DeletedUtc { get; set; }

        public AssetSummary ToSummary()
        {
            var rotated = Orientation is >= 5 and <= 8;
            return new AssetSummary(Id, Guid.Parse(Uid), (MediaType)MediaType, DbFormat.ParseLocal(CaptureLocal),
                (int)(rotated ? PixelHeight : PixelWidth), (int)(rotated ? PixelWidth : PixelHeight),
                DurationMs is { } d ? TimeSpan.FromMilliseconds(d) : null,
                IsFavorite != 0, EditVersion > 0, (int)EditVersion, HasLiveVideo != 0, HasRawPair != 0, OriginalAvailable != 0,
                (ThumbnailState)ThumbState, FileName, DbFormat.ParseUtcOrNull(DeletedUtc));
        }
    }

    internal sealed class AssetRow
    {
        public long Id { get; set; }
        public string Uid { get; set; } = "";
        public long MediaType { get; set; }
        public string CaptureLocal { get; set; } = "";
        public long? CaptureOffsetMin { get; set; }
        public long DateSource { get; set; }
        public string ImportedUtc { get; set; } = "";
        public string FileModifiedUtc { get; set; } = "";
        public long PixelWidth { get; set; }
        public long PixelHeight { get; set; }
        public long Orientation { get; set; }
        public long? DurationMs { get; set; }
        public string FileName { get; set; } = "";
        public string Format { get; set; } = "";
        public long FileSize { get; set; }
        public string? CameraMake { get; set; }
        public string? CameraModel { get; set; }
        public string? Lens { get; set; }
        public double? FNumber { get; set; }
        public double? ExposureS { get; set; }
        public long? Iso { get; set; }
        public double? FocalMm { get; set; }
        public double? Latitude { get; set; }
        public double? Longitude { get; set; }
        public double? Altitude { get; set; }
        public string? Title { get; set; }
        public string? Description { get; set; }
        public long Rating { get; set; }
        public long IsFavorite { get; set; }
        public long IsHidden { get; set; }
        public string? DeletedUtc { get; set; }
        public long EditVersion { get; set; }
        public long HasLiveVideo { get; set; }
        public long HasRawPair { get; set; }
        public long ThumbState { get; set; }
        public long OriginalAvailable { get; set; }
        public long? Phash { get; set; }
        public string? MetadataJson { get; set; }

        public Asset ToAsset()
        {
            GeoLocation? location = null;
            if (Latitude is { } lat && Longitude is { } lon && GeoLocation.TryCreate(lat, lon, Altitude, out var g))
            {
                location = g;
            }

            return new Asset
            {
                Id = Id,
                Uid = Guid.Parse(Uid),
                MediaType = (MediaType)MediaType,
                CaptureDate = new CaptureDate(DbFormat.ParseLocal(CaptureLocal), CaptureOffsetMin is { } o ? TimeSpan.FromMinutes(o) : null, (DateSource)DateSource),
                ImportedUtc = DbFormat.ParseUtc(ImportedUtc),
                FileModifiedUtc = DbFormat.ParseUtc(FileModifiedUtc),
                PixelWidth = (int)PixelWidth,
                PixelHeight = (int)PixelHeight,
                Orientation = (int)Orientation,
                Duration = DurationMs is { } d ? TimeSpan.FromMilliseconds(d) : null,
                FileName = FileName,
                Format = Format,
                FileSize = FileSize,
                Camera = new CameraInfo(CameraMake, CameraModel, Lens, FNumber, ExposureS, Iso is { } i ? (int)i : null, FocalMm),
                Location = location,
                Title = Title,
                Description = Description,
                Rating = (int)Rating,
                IsFavorite = IsFavorite != 0,
                IsHidden = IsHidden != 0,
                DeletedUtc = DbFormat.ParseUtcOrNull(DeletedUtc),
                HasEdits = EditVersion > 0,
                HasLiveVideo = HasLiveVideo != 0,
                HasRawPair = HasRawPair != 0,
                ThumbnailState = (ThumbnailState)ThumbState,
                PerceptualHash = Phash is { } h ? unchecked((ulong)h) : null,
            };
        }
    }

    internal sealed class FileRow
    {
        public long Id { get; set; }
        public long AssetId { get; set; }
        public long Role { get; set; }
        public long StorageMode { get; set; }
        public string StoredPath { get; set; } = "";
        public long SizeBytes { get; set; }
        public string Sha256 { get; set; } = "";
        public string FileModifiedUtc { get; set; } = "";
        public string Format { get; set; } = "";
        public long IsAvailable { get; set; }
        public string LastVerifiedUtc { get; set; } = "";

        public AssetFile ToFile() => new()
        {
            Id = Id,
            AssetId = AssetId,
            Role = (FileRole)Role,
            StorageMode = (StorageMode)StorageMode,
            StoredPath = StoredPath,
            SizeBytes = SizeBytes,
            Sha256 = Sha256,
            FileModifiedUtc = DbFormat.ParseUtc(FileModifiedUtc),
            Format = Format,
            IsAvailable = IsAvailable != 0,
            LastVerifiedUtc = DbFormat.ParseUtc(LastVerifiedUtc),
        };
    }
#pragma warning restore CA1812
}
