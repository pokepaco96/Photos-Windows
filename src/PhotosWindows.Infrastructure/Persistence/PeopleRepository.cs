using System.Runtime.InteropServices;
using Dapper;
using Microsoft.Data.Sqlite;
using PhotosWindows.Application.Abstractions;
using PhotosWindows.Domain;

namespace PhotosWindows.Infrastructure.Persistence;

/// <summary>
/// Faces, groups and analysis state. Counts and lists only include photos that are neither hidden nor deleted,
/// so hiding a photo also hides it from People.
/// </summary>
public sealed class PeopleRepository : IPeopleRepository
{
    private const string VisibleAsset = "a.is_hidden = 0 AND a.deleted_utc IS NULL";
    private const string FaceColumns = "f.id, f.asset_id, f.x, f.y, f.w, f.h, f.score, f.person_id, f.review, f.model_version";

    /// <summary>Minimum overlap for a new face to inherit the decision taken on an earlier face of the same photo.</summary>
    private const float SameFaceIoU = 0.5f;

    private readonly SqliteDb _db;

    public PeopleRepository(SqliteDb db) => _db = db;

    public Task<IReadOnlyList<long>> GetAssetsToAnalyzeAsync(string modelVersion, int limit, IReadOnlyCollection<long>? onlyAssets, CancellationToken cancellationToken) =>
        _db.ReadAsync<IReadOnlyList<long>>(async c =>
        {
            var only = onlyAssets is null ? string.Empty : "AND a.id IN @onlyAssets";
            return (await c.QueryAsync<long>($"""
                SELECT a.id FROM assets a
                WHERE a.media_type = 0 AND a.deleted_utc IS NULL AND a.original_available = 1 {only}
                  AND NOT EXISTS (SELECT 1 FROM face_exclusions e WHERE e.asset_id = a.id)
                  AND NOT EXISTS (SELECT 1 FROM face_analysis fa WHERE fa.asset_id = a.id AND fa.model_version = @modelVersion)
                ORDER BY a.id
                LIMIT @limit
                """, new { modelVersion, limit, onlyAssets }).ConfigureAwait(false)).ToList();
        }, cancellationToken);

    public Task SaveAnalysisAsync(long assetId, string modelVersion, FaceAnalysisState state, IReadOnlyList<NewFace> faces, DateTime utcNow, CancellationToken cancellationToken) =>
        _db.WriteAsync(async (c, t) =>
        {
            // Excluded or deleted meanwhile: drop the result.
            var stillWanted = await c.ExecuteScalarAsync<long>(
                "SELECT count(*) FROM assets a WHERE a.id = @assetId AND NOT EXISTS (SELECT 1 FROM face_exclusions e WHERE e.asset_id = a.id)",
                new { assetId }, t).ConfigureAwait(false);
            if (stillWanted == 0) return;

            var now = DbFormat.Utc(utcNow);
            if (state == FaceAnalysisState.Analyzed)
            {
                var previous = (await c.QueryAsync<FaceRow>($"SELECT {FaceColumns} FROM faces f WHERE f.asset_id = @assetId", new { assetId }, t).ConfigureAwait(false))
                    .Select(r => r.ToFace()).ToList();
                await c.ExecuteAsync("DELETE FROM faces WHERE asset_id = @assetId", new { assetId }, t).ConfigureAwait(false);
                foreach (var face in faces)
                {
                    // Keep the user's decision for the same face (same place in the photo).
                    var match = previous
                        .Where(p => p.Review != FaceReview.Suggested || p.PersonId is not null)
                        .Select(p => (Face: p, IoU: p.Box.IntersectionOverUnion(face.Box)))
                        .Where(m => m.IoU >= SameFaceIoU)
                        .OrderByDescending(m => m.IoU)
                        .Select(m => m.Face)
                        .FirstOrDefault();
                    if (match is not null) previous.Remove(match);
                    await c.ExecuteAsync("""
                        INSERT INTO faces(asset_id, model_version, x, y, w, h, score, embedding, person_id, review, created_utc)
                        VALUES (@assetId, @modelVersion, @x, @y, @w, @h, @score, @embedding, @personId, @review, @now)
                        """, new
                    {
                        assetId,
                        modelVersion,
                        x = face.Box.X,
                        y = face.Box.Y,
                        w = face.Box.Width,
                        h = face.Box.Height,
                        score = face.Score,
                        embedding = ToBlob(face.Embedding),
                        personId = match?.PersonId,
                        review = (int)(match?.Review ?? FaceReview.Suggested),
                        now,
                    }, t).ConfigureAwait(false);
                }

                await RemoveEmptyPeopleAsync(c, t).ConfigureAwait(false);
            }

            await c.ExecuteAsync("""
                INSERT INTO face_analysis(asset_id, model_version, state, face_count, analyzed_utc) VALUES (@assetId, @modelVersion, @state, @count, @now)
                ON CONFLICT(asset_id) DO UPDATE SET model_version = excluded.model_version, state = excluded.state,
                    face_count = excluded.face_count, analyzed_utc = excluded.analyzed_utc
                """, new { assetId, modelVersion, state = (int)state, count = state == FaceAnalysisState.Analyzed ? faces.Count : 0, now }, t).ConfigureAwait(false);
        }, cancellationToken);

    public Task<FaceAnalysisStats> GetStatsAsync(string modelVersion, CancellationToken cancellationToken) =>
        _db.ReadAsync(async c =>
        {
            var row = await c.QuerySingleAsync<(long Photos, long Analyzed, long Failed, long Excluded)>("""
                SELECT
                  (SELECT count(*) FROM assets a WHERE a.media_type = 0 AND a.deleted_utc IS NULL),
                  (SELECT count(*) FROM face_analysis fa JOIN assets a ON a.id = fa.asset_id
                     WHERE a.media_type = 0 AND a.deleted_utc IS NULL AND fa.model_version = @modelVersion AND fa.state = 1
                       AND NOT EXISTS (SELECT 1 FROM face_exclusions e WHERE e.asset_id = a.id)),
                  (SELECT count(*) FROM face_analysis fa JOIN assets a ON a.id = fa.asset_id
                     WHERE a.media_type = 0 AND a.deleted_utc IS NULL AND fa.model_version = @modelVersion AND fa.state = 2
                       AND NOT EXISTS (SELECT 1 FROM face_exclusions e WHERE e.asset_id = a.id)),
                  (SELECT count(*) FROM face_exclusions e JOIN assets a ON a.id = e.asset_id WHERE a.media_type = 0 AND a.deleted_utc IS NULL)
                """, new { modelVersion }).ConfigureAwait(false);
            var faces = await c.QuerySingleAsync<(long Faces, long People, long Ungrouped)>($"""
                SELECT
                  (SELECT count(*) FROM faces f JOIN assets a ON a.id = f.asset_id WHERE {VisibleAsset} AND f.review != 2),
                  (SELECT count(*) FROM people),
                  (SELECT count(*) FROM faces f JOIN assets a ON a.id = f.asset_id WHERE {VisibleAsset} AND f.person_id IS NULL AND f.review = 0)
                """).ConfigureAwait(false);
            return new FaceAnalysisStats((int)row.Photos, (int)row.Analyzed, (int)row.Failed, (int)row.Excluded, (int)faces.Faces, (int)faces.People, (int)faces.Ungrouped);
        }, cancellationToken);

    public Task<IReadOnlyList<FaceVector>> GetVectorsAsync(string modelVersion, CancellationToken cancellationToken) =>
        _db.ReadAsync<IReadOnlyList<FaceVector>>(async c =>
            (await c.QueryAsync<(long Id, long AssetId, long? PersonId, long Review, byte[] Embedding)>(
                "SELECT id, asset_id, person_id, review, embedding FROM faces WHERE model_version = @modelVersion AND review != 2 ORDER BY id",
                new { modelVersion }).ConfigureAwait(false))
            .Select(r => new FaceVector(r.Id, r.AssetId, r.PersonId, (FaceReview)r.Review, FromBlob(r.Embedding)))
            .ToList(), cancellationToken);

    public Task ApplyAssignmentsAsync(IReadOnlyList<FaceAssignment> assignments, DateTime utcNow, CancellationToken cancellationToken)
    {
        if (assignments.Count == 0) return Task.CompletedTask;
        return _db.WriteAsync(async (c, t) =>
        {
            var now = DbFormat.Utc(utcNow);
            var created = new Dictionary<int, long>();
            foreach (var a in assignments)
            {
                var personId = a.PersonId;
                if (personId is null && a.NewGroupKey is { } key)
                {
                    if (!created.TryGetValue(key, out var id))
                    {
                        id = await c.ExecuteScalarAsync<long>("INSERT INTO people(name, created_utc, updated_utc) VALUES (NULL, @now, @now) RETURNING id", new { now }, t).ConfigureAwait(false);
                        created[key] = id;
                    }

                    personId = id;
                }

                // Only suggestions move automatically; the user's decisions are never overwritten.
                await c.ExecuteAsync("UPDATE faces SET person_id = @personId WHERE id = @faceId AND review = 0", new { personId, faceId = a.FaceId }, t).ConfigureAwait(false);
            }

            await RemoveEmptyPeopleAsync(c, t).ConfigureAwait(false);
        }, cancellationToken);
    }

    public Task<IReadOnlyList<Person>> GetPeopleAsync(CancellationToken cancellationToken) =>
        _db.ReadAsync<IReadOnlyList<Person>>(async c =>
            (await c.QueryAsync<PersonRow>($"""
                SELECT p.id, p.name, p.cover_face_id,
                       (SELECT count(*) FROM faces f JOIN assets a ON a.id = f.asset_id WHERE f.person_id = p.id AND {VisibleAsset}) AS face_count,
                       (SELECT count(DISTINCT f.asset_id) FROM faces f JOIN assets a ON a.id = f.asset_id WHERE f.person_id = p.id AND {VisibleAsset}) AS asset_count
                FROM people p
                """).ConfigureAwait(false))
            .Select(r => r.ToPerson())
            .Where(p => p.FaceCount > 0)
            // Named people first (alphabetically), then the largest unnamed groups.
            .OrderBy(p => p.IsNamed ? 0 : 1)
            .ThenBy(p => p.IsNamed ? p.Name : null, StringComparer.CurrentCultureIgnoreCase)
            .ThenByDescending(p => p.AssetCount)
            .ThenBy(p => p.Id)
            .ToList(), cancellationToken);

    public Task<Person?> GetPersonAsync(long personId, CancellationToken cancellationToken) =>
        _db.ReadAsync(async c =>
            (await c.QuerySingleOrDefaultAsync<PersonRow>($"""
                SELECT p.id, p.name, p.cover_face_id,
                       (SELECT count(*) FROM faces f JOIN assets a ON a.id = f.asset_id WHERE f.person_id = p.id AND {VisibleAsset}) AS face_count,
                       (SELECT count(DISTINCT f.asset_id) FROM faces f JOIN assets a ON a.id = f.asset_id WHERE f.person_id = p.id AND {VisibleAsset}) AS asset_count
                FROM people p WHERE p.id = @personId
                """, new { personId }).ConfigureAwait(false))?.ToPerson(), cancellationToken);

    public Task<IReadOnlyList<Face>> GetFacesOfPersonAsync(long personId, CancellationToken cancellationToken) =>
        QueryFacesAsync($"WHERE f.person_id = @personId AND {VisibleAsset} ORDER BY a.capture_local DESC, f.id", new { personId }, cancellationToken);

    public Task<IReadOnlyList<Face>> GetUngroupedFacesAsync(int limit, CancellationToken cancellationToken) =>
        QueryFacesAsync($"WHERE f.person_id IS NULL AND f.review = 0 AND {VisibleAsset} ORDER BY a.capture_local DESC, f.id LIMIT @limit", new { limit }, cancellationToken);

    public Task<IReadOnlyList<Face>> GetFacesOfAssetAsync(long assetId, CancellationToken cancellationToken) =>
        QueryFacesAsync("WHERE f.asset_id = @assetId ORDER BY f.x", new { assetId }, cancellationToken);

    public async Task<Face?> GetFaceAsync(long faceId, CancellationToken cancellationToken) =>
        (await QueryFacesAsync("WHERE f.id = @faceId", new { faceId }, cancellationToken).ConfigureAwait(false)).FirstOrDefault();

    public Task RenameAsync(long personId, string? name, DateTime utcNow, CancellationToken cancellationToken)
    {
        var clean = string.IsNullOrWhiteSpace(name) ? null : name.Trim();
        return _db.WriteAsync(async (c, t) =>
        {
            await c.ExecuteAsync("UPDATE people SET name = @clean, updated_utc = @now WHERE id = @personId", new { clean, now = DbFormat.Utc(utcNow), personId }, t).ConfigureAwait(false);
            // Naming a group confirms its faces: they will no longer be regrouped automatically.
            if (clean is not null)
            {
                await c.ExecuteAsync("UPDATE faces SET review = 1 WHERE person_id = @personId AND review = 0", new { personId }, t).ConfigureAwait(false);
            }
        }, cancellationToken);
    }

    public Task MergeAsync(IReadOnlyCollection<long> sourcePersonIds, long targetPersonId, DateTime utcNow, CancellationToken cancellationToken)
    {
        var sources = sourcePersonIds.Where(id => id != targetPersonId).Distinct().ToList();
        if (sources.Count == 0) return Task.CompletedTask;
        return _db.WriteAsync(async (c, t) =>
        {
            var targetName = await c.ExecuteScalarAsync<string?>("SELECT name FROM people WHERE id = @targetPersonId", new { targetPersonId }, t).ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(targetName))
            {
                // Keep a name if only a source group had one.
                var sourceName = await c.ExecuteScalarAsync<string?>("SELECT name FROM people WHERE id IN @sources AND name IS NOT NULL ORDER BY id LIMIT 1", new { sources }, t).ConfigureAwait(false);
                if (sourceName is not null)
                {
                    await c.ExecuteAsync("UPDATE people SET name = @sourceName WHERE id = @targetPersonId", new { sourceName, targetPersonId }, t).ConfigureAwait(false);
                }
            }

            await c.ExecuteAsync("UPDATE faces SET person_id = @targetPersonId, review = 1 WHERE person_id IN @sources OR person_id = @targetPersonId", new { targetPersonId, sources }, t).ConfigureAwait(false);
            await c.ExecuteAsync("DELETE FROM people WHERE id IN @sources", new { sources }, t).ConfigureAwait(false);
            await c.ExecuteAsync("UPDATE people SET updated_utc = @now WHERE id = @targetPersonId", new { now = DbFormat.Utc(utcNow), targetPersonId }, t).ConfigureAwait(false);
        }, cancellationToken);
    }

    public Task<long> MoveFacesAsync(IReadOnlyCollection<long> faceIds, long? targetPersonId, DateTime utcNow, CancellationToken cancellationToken) =>
        _db.WriteAsync(async (c, t) =>
        {
            var now = DbFormat.Utc(utcNow);
            var target = targetPersonId
                ?? await c.ExecuteScalarAsync<long>("INSERT INTO people(name, created_utc, updated_utc) VALUES (NULL, @now, @now) RETURNING id", new { now }, t).ConfigureAwait(false);
            await c.ExecuteAsync("UPDATE faces SET person_id = @target, review = 1 WHERE id IN @faceIds", new { target, faceIds }, t).ConfigureAwait(false);
            await RemoveEmptyPeopleAsync(c, t).ConfigureAwait(false);
            return target;
        }, cancellationToken);

    public Task IgnoreFacesAsync(IReadOnlyCollection<long> faceIds, CancellationToken cancellationToken) =>
        _db.WriteAsync(async (c, t) =>
        {
            await c.ExecuteAsync("UPDATE faces SET person_id = NULL, review = 2 WHERE id IN @faceIds", new { faceIds }, t).ConfigureAwait(false);
            await RemoveEmptyPeopleAsync(c, t).ConfigureAwait(false);
        }, cancellationToken);

    public Task SetCoverAsync(long personId, long faceId, CancellationToken cancellationToken) =>
        _db.WriteAsync((c, t) => c.ExecuteAsync(
            "UPDATE people SET cover_face_id = @faceId WHERE id = @personId AND EXISTS (SELECT 1 FROM faces WHERE id = @faceId AND person_id = @personId)",
            new { personId, faceId }, t), cancellationToken);

    public Task DeletePersonAsync(long personId, CancellationToken cancellationToken) =>
        _db.WriteAsync(async (c, t) =>
        {
            await c.ExecuteAsync("DELETE FROM faces WHERE person_id = @personId", new { personId }, t).ConfigureAwait(false);
            await c.ExecuteAsync("DELETE FROM people WHERE id = @personId", new { personId }, t).ConfigureAwait(false);
        }, cancellationToken);

    public Task ExcludeAssetsAsync(IReadOnlyCollection<long> assetIds, DateTime utcNow, CancellationToken cancellationToken) =>
        _db.WriteAsync(async (c, t) =>
        {
            var now = DbFormat.Utc(utcNow);
            foreach (var assetId in assetIds)
            {
                await c.ExecuteAsync("INSERT OR IGNORE INTO face_exclusions(asset_id, created_utc) VALUES (@assetId, @now)", new { assetId, now }, t).ConfigureAwait(false);
            }

            await c.ExecuteAsync("DELETE FROM faces WHERE asset_id IN @assetIds", new { assetIds }, t).ConfigureAwait(false);
            await c.ExecuteAsync("DELETE FROM face_analysis WHERE asset_id IN @assetIds", new { assetIds }, t).ConfigureAwait(false);
            await RemoveEmptyPeopleAsync(c, t).ConfigureAwait(false);
        }, cancellationToken);

    public Task IncludeAssetsAsync(IReadOnlyCollection<long> assetIds, CancellationToken cancellationToken) =>
        _db.WriteAsync((c, t) => c.ExecuteAsync("DELETE FROM face_exclusions WHERE asset_id IN @assetIds", new { assetIds }, t), cancellationToken);

    public Task<bool> IsExcludedAsync(long assetId, CancellationToken cancellationToken) =>
        _db.ReadAsync(async c => await c.ExecuteScalarAsync<long>("SELECT count(*) FROM face_exclusions WHERE asset_id = @assetId", new { assetId }).ConfigureAwait(false) > 0, cancellationToken);

    public Task ResetAnalysisAsync(IReadOnlyCollection<long>? assetIds, CancellationToken cancellationToken) =>
        _db.WriteAsync((c, t) => assetIds is null
            ? c.ExecuteAsync("DELETE FROM face_analysis", transaction: t)
            : c.ExecuteAsync("DELETE FROM face_analysis WHERE asset_id IN @assetIds", new { assetIds }, t), cancellationToken);

    public async Task DeleteAllAsync(CancellationToken cancellationToken)
    {
        await _db.WriteAsync(async (c, t) =>
        {
            await c.ExecuteAsync("PRAGMA secure_delete = ON", transaction: t).ConfigureAwait(false);
            await c.ExecuteAsync("DELETE FROM faces; DELETE FROM people; DELETE FROM face_analysis;", transaction: t).ConfigureAwait(false);
        }, cancellationToken).ConfigureAwait(false);

        // Remove the freed pages from the files themselves: checkpoint the WAL into the database and rebuild it.
        await _db.ReadAsync(async c =>
        {
            await c.ExecuteAsync("PRAGMA secure_delete = ON; PRAGMA wal_checkpoint(TRUNCATE);").ConfigureAwait(false);
            await c.ExecuteAsync("VACUUM").ConfigureAwait(false);
            await c.ExecuteAsync("PRAGMA wal_checkpoint(TRUNCATE);").ConfigureAwait(false);
            return true;
        }, cancellationToken).ConfigureAwait(false);
    }

    public Task<string?> GetSettingAsync(string key, CancellationToken cancellationToken) =>
        _db.ReadAsync(c => c.ExecuteScalarAsync<string?>("SELECT value FROM library_info WHERE key = @key", new { key }), cancellationToken);

    public Task SetSettingAsync(string key, string? value, CancellationToken cancellationToken) =>
        _db.WriteAsync((c, t) => value is null
            ? c.ExecuteAsync("DELETE FROM library_info WHERE key = @key", new { key }, t)
            : c.ExecuteAsync("INSERT INTO library_info(key, value) VALUES (@key, @value) ON CONFLICT(key) DO UPDATE SET value = excluded.value", new { key, value }, t),
            cancellationToken);

    private Task<IReadOnlyList<Face>> QueryFacesAsync(string where, object parameters, CancellationToken cancellationToken) =>
        _db.ReadAsync<IReadOnlyList<Face>>(async c =>
            (await c.QueryAsync<FaceRow>($"SELECT {FaceColumns} FROM faces f JOIN assets a ON a.id = f.asset_id {where}", parameters).ConfigureAwait(false))
            .Select(r => r.ToFace()).ToList(), cancellationToken);

    /// <summary>Groups left without faces disappear; a cover pointing to a face of another group is cleared.</summary>
    private static async Task RemoveEmptyPeopleAsync(SqliteConnection c, SqliteTransaction t)
    {
        await c.ExecuteAsync("DELETE FROM people WHERE NOT EXISTS (SELECT 1 FROM faces f WHERE f.person_id = people.id)", transaction: t).ConfigureAwait(false);
        await c.ExecuteAsync(
            "UPDATE people SET cover_face_id = NULL WHERE cover_face_id IS NOT NULL AND NOT EXISTS (SELECT 1 FROM faces f WHERE f.id = people.cover_face_id AND f.person_id = people.id)",
            transaction: t).ConfigureAwait(false);
    }

    internal static byte[] ToBlob(float[] vector) => MemoryMarshal.AsBytes(vector.AsSpan()).ToArray();

    internal static float[] FromBlob(byte[] blob) => MemoryMarshal.Cast<byte, float>(blob).ToArray();

    private sealed record FaceRow(long Id, long AssetId, double X, double Y, double W, double H, double Score, long? PersonId, long Review, string ModelVersion)
    {
        public Face ToFace() => new(Id, AssetId, new FaceBox((float)X, (float)Y, (float)W, (float)H), (float)Score, PersonId, (FaceReview)Review, ModelVersion);
    }

    /// <summary>
    /// Property-mapped (not positional): with no rows SQLite reports computed columns as BLOB, which would not match a
    /// constructor; property mapping converts each value as it is read.
    /// </summary>
    private sealed class PersonRow
    {
        public long Id { get; set; }
        public string? Name { get; set; }
        public long? CoverFaceId { get; set; }
        public long FaceCount { get; set; }
        public long AssetCount { get; set; }

        public Person ToPerson() => new(Id, Name, CoverFaceId, (int)FaceCount, (int)AssetCount);
    }
}
