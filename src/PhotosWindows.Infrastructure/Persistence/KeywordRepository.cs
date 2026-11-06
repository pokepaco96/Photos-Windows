using Dapper;
using Microsoft.Data.Sqlite;
using PhotosWindows.Application.Abstractions;
using PhotosWindows.Domain;

namespace PhotosWindows.Infrastructure.Persistence;

public sealed class KeywordRepository : IKeywordRepository
{
    private readonly SqliteDb _db;

    public KeywordRepository(SqliteDb db) => _db = db;

    public Task<IReadOnlyList<Keyword>> GetAllAsync(CancellationToken cancellationToken) =>
        _db.ReadAsync<IReadOnlyList<Keyword>>(async c =>
            (await c.QueryAsync<(long Id, string Name, long Count)>("""
                SELECT k.id, k.name, count(ak.asset_id) FROM keywords k
                LEFT JOIN asset_keywords ak ON ak.keyword_id = k.id
                GROUP BY k.id ORDER BY k.name COLLATE NOCASE
                """).ConfigureAwait(false)).Select(r => new Keyword(r.Id, r.Name, (int)r.Count)).ToList(),
            cancellationToken);

    public Task<IReadOnlyList<string>> GetForAssetAsync(long assetId, CancellationToken cancellationToken) =>
        _db.ReadAsync<IReadOnlyList<string>>(async c =>
            (await c.QueryAsync<string>("SELECT k.name FROM asset_keywords ak JOIN keywords k ON k.id = ak.keyword_id WHERE ak.asset_id = @assetId ORDER BY k.name COLLATE NOCASE", new { assetId }).ConfigureAwait(false)).ToList(),
            cancellationToken);

    public Task AddAsync(IReadOnlyCollection<long> assetIds, IReadOnlyCollection<string> keywords, CancellationToken cancellationToken) =>
        _db.WriteAsync(async (c, t) =>
        {
            await AddInTransactionAsync(c, t, assetIds, keywords).ConfigureAwait(false);
            await AssetRepository.RefreshFtsAsync(c, t, assetIds).ConfigureAwait(false);
        }, cancellationToken);

    internal static async Task AddInTransactionAsync(SqliteConnection c, SqliteTransaction t, IReadOnlyCollection<long> assetIds, IEnumerable<string> keywords)
    {
        foreach (var raw in keywords)
        {
            var name = raw?.Trim();
            if (string.IsNullOrEmpty(name)) continue;
            if (name.Length > LibraryRules.MaxNameLength) name = name[..LibraryRules.MaxNameLength];
            await c.ExecuteAsync("INSERT INTO keywords(name) VALUES (@name) ON CONFLICT(name) DO NOTHING", new { name }, t).ConfigureAwait(false);
            var keywordId = await c.ExecuteScalarAsync<long>("SELECT id FROM keywords WHERE name = @name", new { name }, t).ConfigureAwait(false);
            await c.ExecuteAsync(
                "INSERT OR IGNORE INTO asset_keywords(asset_id, keyword_id) SELECT value, @keywordId FROM json_each(@ids)",
                new { keywordId, ids = AssetRepository.Json(assetIds) }, t).ConfigureAwait(false);
        }
    }

    public Task RemoveAsync(IReadOnlyCollection<long> assetIds, string keyword, CancellationToken cancellationToken) =>
        _db.WriteAsync(async (c, t) =>
        {
            await c.ExecuteAsync(
                "DELETE FROM asset_keywords WHERE keyword_id = (SELECT id FROM keywords WHERE name = @keyword) AND asset_id IN (SELECT value FROM json_each(@ids))",
                new { keyword = keyword.Trim(), ids = AssetRepository.Json(assetIds) }, t).ConfigureAwait(false);
            await AssetRepository.RefreshFtsAsync(c, t, assetIds).ConfigureAwait(false);
        }, cancellationToken);

    public Task RenameAsync(long keywordId, string newName, CancellationToken cancellationToken)
    {
        var name = LibraryRules.NormalizeName(newName, nameof(newName));
        return _db.WriteAsync(async (c, t) =>
        {
            var existing = await c.ExecuteScalarAsync<long?>("SELECT id FROM keywords WHERE name = @name AND id <> @keywordId", new { name, keywordId }, t).ConfigureAwait(false);
            if (existing is { } target)
            {
                // Merge into the existing keyword with that name.
                await c.ExecuteAsync("INSERT OR IGNORE INTO asset_keywords(asset_id, keyword_id) SELECT asset_id, @target FROM asset_keywords WHERE keyword_id = @keywordId", new { target, keywordId }, t).ConfigureAwait(false);
                await c.ExecuteAsync("DELETE FROM keywords WHERE id = @keywordId", new { keywordId }, t).ConfigureAwait(false);
                keywordId = target;
            }
            else
            {
                await c.ExecuteAsync("UPDATE keywords SET name = @name WHERE id = @keywordId", new { name, keywordId }, t).ConfigureAwait(false);
            }

            var ids = (await c.QueryAsync<long>("SELECT asset_id FROM asset_keywords WHERE keyword_id = @keywordId", new { keywordId }, t).ConfigureAwait(false)).ToList();
            await AssetRepository.RefreshFtsAsync(c, t, ids).ConfigureAwait(false);
        }, cancellationToken);
    }

    public Task DeleteAsync(long keywordId, CancellationToken cancellationToken) =>
        _db.WriteAsync(async (c, t) =>
        {
            var ids = (await c.QueryAsync<long>("SELECT asset_id FROM asset_keywords WHERE keyword_id = @keywordId", new { keywordId }, t).ConfigureAwait(false)).ToList();
            await c.ExecuteAsync("DELETE FROM keywords WHERE id = @keywordId", new { keywordId }, t).ConfigureAwait(false);
            await AssetRepository.RefreshFtsAsync(c, t, ids).ConfigureAwait(false);
        }, cancellationToken);
}
