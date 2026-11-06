using System.Text.Json;
using Dapper;
using PhotosWindows.Application.Abstractions;
using PhotosWindows.Domain;

namespace PhotosWindows.Infrastructure.Persistence;

public sealed class EditRepository : IEditRepository
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = false };
    private readonly SqliteDb _db;

    public EditRepository(SqliteDb db) => _db = db;

    public Task<StoredRecipe?> GetAsync(long assetId, CancellationToken cancellationToken) =>
        _db.ReadAsync(async c =>
        {
            var row = await c.QuerySingleOrDefaultAsync<(string Json, long Version, string Updated)>(
                "SELECT recipe_json, version, updated_utc FROM edit_recipes WHERE asset_id = @assetId", new { assetId }).ConfigureAwait(false);
            return row.Json is null ? null : new StoredRecipe(Deserialize(row.Json), (int)row.Version, DbFormat.ParseUtc(row.Updated));
        }, cancellationToken);

    public Task<int> SaveAsync(long assetId, EditRecipe recipe, DateTime utcNow, CancellationToken cancellationToken)
    {
        var normalized = recipe.Normalize();
        var json = JsonSerializer.Serialize(normalized, JsonOptions);
        return _db.WriteAsync(async (c, t) =>
        {
            var version = (int)await c.ExecuteScalarAsync<long>("SELECT coalesce(max(version), 0) + 1 FROM edit_history WHERE asset_id = @assetId", new { assetId }, t).ConfigureAwait(false);
            var now = DbFormat.Utc(utcNow);
            await c.ExecuteAsync("INSERT INTO edit_history(asset_id, recipe_json, version, created_utc) VALUES (@assetId, @json, @version, @now)", new { assetId, json, version, now }, t).ConfigureAwait(false);

            if (normalized.IsIdentity)
            {
                await c.ExecuteAsync("DELETE FROM edit_recipes WHERE asset_id = @assetId", new { assetId }, t).ConfigureAwait(false);
                await c.ExecuteAsync("UPDATE assets SET edit_version = 0, thumb_state = 0 WHERE id = @assetId", new { assetId }, t).ConfigureAwait(false);
                return 0;
            }

            await c.ExecuteAsync("""
                INSERT INTO edit_recipes(asset_id, recipe_json, version, updated_utc) VALUES (@assetId, @json, @version, @now)
                ON CONFLICT(asset_id) DO UPDATE SET recipe_json = excluded.recipe_json, version = excluded.version, updated_utc = excluded.updated_utc
                """, new { assetId, json, version, now }, t).ConfigureAwait(false);
            await c.ExecuteAsync("UPDATE assets SET edit_version = @version, thumb_state = 0 WHERE id = @assetId", new { assetId, version }, t).ConfigureAwait(false);
            return version;
        }, cancellationToken);
    }

    public Task<IReadOnlyList<StoredRecipe>> GetHistoryAsync(long assetId, int limit, CancellationToken cancellationToken) =>
        _db.ReadAsync<IReadOnlyList<StoredRecipe>>(async c =>
            (await c.QueryAsync<(string Json, long Version, string Created)>(
                "SELECT recipe_json, version, created_utc FROM edit_history WHERE asset_id = @assetId ORDER BY version DESC LIMIT @limit", new { assetId, limit }).ConfigureAwait(false))
            .Select(r => new StoredRecipe(Deserialize(r.Json), (int)r.Version, DbFormat.ParseUtc(r.Created))).ToList(),
            cancellationToken);

    private static EditRecipe Deserialize(string json) =>
        (JsonSerializer.Deserialize<EditRecipe>(json, JsonOptions) ?? EditRecipe.Identity).Normalize();
}

public sealed class DuplicateDismissalRepository : IDuplicateDismissalRepository
{
    private readonly SqliteDb _db;

    public DuplicateDismissalRepository(SqliteDb db) => _db = db;

    public Task DismissAsync(long assetA, long assetB, CancellationToken cancellationToken)
    {
        var (a, b) = assetA < assetB ? (assetA, assetB) : (assetB, assetA);
        if (a == b) return Task.CompletedTask;
        return _db.WriteAsync((c, t) => c.ExecuteAsync("INSERT OR IGNORE INTO duplicate_dismissals(asset_a, asset_b) VALUES (@a, @b)", new { a, b }, t), cancellationToken);
    }

    public Task<IReadOnlySet<(long, long)>> GetDismissedAsync(CancellationToken cancellationToken) =>
        _db.ReadAsync<IReadOnlySet<(long, long)>>(async c =>
            (await c.QueryAsync<(long, long)>("SELECT asset_a, asset_b FROM duplicate_dismissals").ConfigureAwait(false)).ToHashSet(),
            cancellationToken);
}
