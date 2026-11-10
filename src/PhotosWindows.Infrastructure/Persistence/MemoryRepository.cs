using Dapper;
using PhotosWindows.Application.Abstractions;
using PhotosWindows.Domain;

namespace PhotosWindows.Infrastructure.Persistence;

public sealed class MemoryRepository : IMemoryRepository
{
    private const string Usable = "a.is_hidden = 0 AND a.deleted_utc IS NULL AND a.original_available = 1 AND a.thumb_state <> 2";
    private readonly SqliteDb _db;

    public MemoryRepository(SqliteDb db) => _db = db;

    public Task<IReadOnlyList<MemoryItem>> GetItemsAsync(CancellationToken cancellationToken) =>
        _db.ReadAsync<IReadOnlyList<MemoryItem>>(async c =>
        {
            var rows = await c.QueryAsync<ItemRow>(new CommandDefinition(
                $"SELECT a.id, a.capture_local, a.latitude, a.longitude, a.is_favorite, a.edit_version, a.media_type FROM assets a WHERE {Usable}",
                cancellationToken: cancellationToken)).ConfigureAwait(false);
            var list = new List<MemoryItem>();
            foreach (var r in rows)
            {
                // Same rule as the rest of the app: out-of-range values and the (0,0) placeholder are "no location".
                var located = r.Latitude is { } lat && r.Longitude is { } lon && GeoLocation.TryCreate(lat, lon, null, out _);
                list.Add(new MemoryItem(r.Id, DbFormat.ParseLocal(r.CaptureLocal), located ? r.Latitude : null, located ? r.Longitude : null,
                    r.IsFavorite != 0, r.EditVersion > 0, r.MediaType == (long)MediaType.Video));
            }

            return list;
        }, cancellationToken);

    public Task<IReadOnlyList<MemoryPerson>> GetNamedPeopleAsync(CancellationToken cancellationToken) =>
        _db.ReadAsync<IReadOnlyList<MemoryPerson>>(async c =>
        {
            var rows = await c.QueryAsync<PersonRow>(new CommandDefinition($"""
                SELECT DISTINCT p.id AS person_id, p.name, f.asset_id
                FROM people p JOIN faces f ON f.person_id = p.id JOIN assets a ON a.id = f.asset_id
                WHERE p.name IS NOT NULL AND trim(p.name) <> '' AND {Usable}
                ORDER BY p.id, f.asset_id
                """, cancellationToken: cancellationToken)).ConfigureAwait(false);
            return rows.GroupBy(r => (r.PersonId, r.Name))
                .Select(g => new MemoryPerson(g.Key.PersonId, g.Key.Name, g.Select(r => r.AssetId).ToList()))
                .ToList();
        }, cancellationToken);

    public Task<IReadOnlyList<HiddenMemory>> GetHiddenAsync(CancellationToken cancellationToken) =>
        _db.ReadAsync<IReadOnlyList<HiddenMemory>>(async c =>
            (await c.QueryAsync<HiddenRow>("SELECT key, kind, start_local, end_local, hidden_utc FROM memory_hidden ORDER BY hidden_utc DESC, key").ConfigureAwait(false))
            .Select(r => new HiddenMemory(r.Key, (MemoryKind)r.Kind, DbFormat.ParseLocal(r.StartLocal), DbFormat.ParseLocal(r.EndLocal), DbFormat.ParseUtc(r.HiddenUtc)))
            .ToList(), cancellationToken);

    public Task HideAsync(HiddenMemory memory, CancellationToken cancellationToken) =>
        _db.WriteAsync((c, t) => c.ExecuteAsync(
            "INSERT INTO memory_hidden(key, kind, start_local, end_local, hidden_utc) VALUES (@key, @kind, @start, @end, @hidden) ON CONFLICT(key) DO UPDATE SET hidden_utc = excluded.hidden_utc",
            new { key = memory.Key, kind = (int)memory.Kind, start = DbFormat.Local(memory.Start), end = DbFormat.Local(memory.End), hidden = DbFormat.Utc(memory.HiddenUtc) }, t), cancellationToken);

    public Task UnhideAsync(string key, CancellationToken cancellationToken) =>
        _db.WriteAsync((c, t) => c.ExecuteAsync("DELETE FROM memory_hidden WHERE key = @key", new { key }, t), cancellationToken);

    public Task<IReadOnlyList<MemoryExclusion>> GetExclusionsAsync(CancellationToken cancellationToken) =>
        _db.ReadAsync<IReadOnlyList<MemoryExclusion>>(async c =>
            (await c.QueryAsync<ExclusionRow>("SELECT id, kind, from_local, to_local FROM memory_exclusions ORDER BY id").ConfigureAwait(false))
            .Select(r => new MemoryExclusion(r.Id, r.Kind is { } k ? (MemoryKind)k : null,
                r.FromLocal is null ? null : DbFormat.ParseLocal(r.FromLocal), r.ToLocal is null ? null : DbFormat.ParseLocal(r.ToLocal)))
            .ToList(), cancellationToken);

    public Task<long> ExcludeKindAsync(MemoryKind kind, DateTime createdUtc, CancellationToken cancellationToken) =>
        _db.WriteAsync(async (c, t) =>
        {
            await c.ExecuteAsync("INSERT OR IGNORE INTO memory_exclusions(kind, created_utc) VALUES (@kind, @created)", new { kind = (int)kind, created = DbFormat.Utc(createdUtc) }, t).ConfigureAwait(false);
            return await c.ExecuteScalarAsync<long>("SELECT id FROM memory_exclusions WHERE kind = @kind", new { kind = (int)kind }, t).ConfigureAwait(false);
        }, cancellationToken);

    public Task<long> ExcludeRangeAsync(DateTime from, DateTime to, DateTime createdUtc, CancellationToken cancellationToken)
    {
        var (a, b) = from.Date <= to.Date ? (from.Date, to.Date) : (to.Date, from.Date);
        return _db.WriteAsync((c, t) => c.ExecuteScalarAsync<long>(
            "INSERT INTO memory_exclusions(from_local, to_local, created_utc) VALUES (@a, @b, @created); SELECT last_insert_rowid();",
            new { a = DbFormat.Local(a), b = DbFormat.Local(b), created = DbFormat.Utc(createdUtc) }, t), cancellationToken);
    }

    public Task RemoveExclusionAsync(long id, CancellationToken cancellationToken) =>
        _db.WriteAsync((c, t) => c.ExecuteAsync("DELETE FROM memory_exclusions WHERE id = @id", new { id }, t), cancellationToken);

    private sealed class ItemRow
    {
        public long Id { get; set; }
        public string CaptureLocal { get; set; } = "";
        public double? Latitude { get; set; }
        public double? Longitude { get; set; }
        public long IsFavorite { get; set; }
        public long EditVersion { get; set; }
        public long MediaType { get; set; }
    }

    private sealed class PersonRow
    {
        public long PersonId { get; set; }
        public string Name { get; set; } = "";
        public long AssetId { get; set; }
    }

    private sealed class HiddenRow
    {
        public string Key { get; set; } = "";
        public long Kind { get; set; }
        public string StartLocal { get; set; } = "";
        public string EndLocal { get; set; } = "";
        public string HiddenUtc { get; set; } = "";
    }

    private sealed class ExclusionRow
    {
        public long Id { get; set; }
        public long? Kind { get; set; }
        public string? FromLocal { get; set; }
        public string? ToLocal { get; set; }
    }
}
