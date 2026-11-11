using Dapper;
using PhotosWindows.Application.Abstractions;
using PhotosWindows.Domain;

namespace PhotosWindows.Infrastructure.Persistence;

public sealed class ImportJobRepository : IImportJobRepository
{
    private readonly SqliteDb _db;

    public ImportJobRepository(SqliteDb db) => _db = db;

    public Task<long> CreateJobAsync(ImportMode mode, bool importDuplicates, IReadOnlyList<string> sourcePaths, DateTime startedUtc, CancellationToken cancellationToken) =>
        _db.WriteAsync(async (c, t) =>
        {
            var jobId = await c.ExecuteScalarAsync<long>("""
                INSERT INTO import_jobs(uid, mode, status, import_duplicates, started_utc, total) VALUES (@uid, @mode, @status, @dup, @started, @total);
                SELECT last_insert_rowid();
                """, new
            {
                uid = Guid.NewGuid().ToString("D"),
                mode = (int)mode,
                status = (int)ImportJobStatus.Pending,
                dup = importDuplicates ? 1 : 0,
                started = DbFormat.Utc(startedUtc),
                total = sourcePaths.Count,
            }, t).ConfigureAwait(false);

            await c.ExecuteAsync(
                "INSERT INTO import_items(job_id, source_path, status) VALUES (@jobId, @path, 0)",
                sourcePaths.Select(path => new { jobId, path }), t).ConfigureAwait(false);
            return jobId;
        }, cancellationToken);

    public Task<ImportJob?> GetJobAsync(long jobId, CancellationToken cancellationToken) =>
        _db.ReadAsync(async c => (await c.QuerySingleOrDefaultAsync<JobRow>("SELECT * FROM import_jobs WHERE id = @jobId", new { jobId }).ConfigureAwait(false))?.ToJob(), cancellationToken);

    public Task<IReadOnlyList<ImportJob>> GetJobsAsync(int limit, CancellationToken cancellationToken) =>
        _db.ReadAsync<IReadOnlyList<ImportJob>>(async c =>
            (await c.QueryAsync<JobRow>("SELECT * FROM import_jobs ORDER BY id DESC LIMIT @limit", new { limit }).ConfigureAwait(false)).Select(r => r.ToJob()).ToList(),
            cancellationToken);

    public Task<IReadOnlyList<ImportItem>> GetItemsAsync(long jobId, ImportItemStatus? status, CancellationToken cancellationToken) =>
        _db.ReadAsync<IReadOnlyList<ImportItem>>(async c =>
            (await c.QueryAsync<ItemRow>(
                "SELECT * FROM import_items WHERE job_id = @jobId AND (@status IS NULL OR status = @status) ORDER BY id",
                new { jobId, status = (int?)status }).ConfigureAwait(false)).Select(r => r.ToItem()).ToList(),
            cancellationToken);

    public Task UpdateItemAsync(long itemId, ImportItemStatus status, long? assetId, string? message, string? targetPath, CancellationToken cancellationToken) =>
        _db.WriteAsync((c, t) => c.ExecuteAsync(
            "UPDATE import_items SET status = @status, asset_id = @assetId, message = @message, target_path = @targetPath WHERE id = @itemId",
            new { itemId, status = (int)status, assetId, message, targetPath }, t), cancellationToken);

    public Task UpdateItemsAsync(IReadOnlyList<ImportItemUpdate> updates, CancellationToken cancellationToken) =>
        updates.Count == 0
            ? Task.CompletedTask
            : _db.WriteAsync((c, t) => c.ExecuteAsync(
                "UPDATE import_items SET status = @status, asset_id = @assetId, message = @message, target_path = @targetPath WHERE id = @itemId",
                updates.Select(u => new { itemId = u.ItemId, status = (int)u.Status, assetId = u.AssetId, message = u.Message, targetPath = u.TargetPath }), t),
                cancellationToken);

    public Task SetJobStatusAsync(long jobId, ImportJobStatus status, DateTime? finishedUtc, CancellationToken cancellationToken) =>
        _db.WriteAsync((c, t) => c.ExecuteAsync(
            "UPDATE import_jobs SET status = @status, finished_utc = @finished WHERE id = @jobId",
            new { jobId, status = (int)status, finished = DbFormat.Utc(finishedUtc) }, t), cancellationToken);

    public Task RefreshCountersAsync(long jobId, CancellationToken cancellationToken) =>
        _db.WriteAsync((c, t) => c.ExecuteAsync("""
            UPDATE import_jobs SET
              total      = (SELECT count(*) FROM import_items WHERE job_id = @jobId),
              processed  = (SELECT count(*) FROM import_items WHERE job_id = @jobId AND status <> 0),
              imported   = (SELECT count(*) FROM import_items WHERE job_id = @jobId AND status IN (1, 7)),
              duplicates = (SELECT count(*) FROM import_items WHERE job_id = @jobId AND status IN (2, 3)),
              skipped    = (SELECT count(*) FROM import_items WHERE job_id = @jobId AND status IN (4, 6)),
              failed     = (SELECT count(*) FROM import_items WHERE job_id = @jobId AND status = 5)
            WHERE id = @jobId
            """, new { jobId }, t), cancellationToken);

    public Task<IReadOnlyList<long>> MarkRunningAsInterruptedAsync(CancellationToken cancellationToken) =>
        _db.WriteAsync<IReadOnlyList<long>>(async (c, t) =>
        {
            var ids = (await c.QueryAsync<long>("SELECT id FROM import_jobs WHERE status IN (0, 1)", transaction: t).ConfigureAwait(false)).ToList();
            await c.ExecuteAsync("UPDATE import_jobs SET status = @s WHERE status IN (0, 1)", new { s = (int)ImportJobStatus.Interrupted }, t).ConfigureAwait(false);
            return ids;
        }, cancellationToken);

#pragma warning disable CA1812
    private sealed class JobRow
    {
        public long Id { get; set; }
        public string Uid { get; set; } = "";
        public long Mode { get; set; }
        public long Status { get; set; }
        public long ImportDuplicates { get; set; }
        public string StartedUtc { get; set; } = "";
        public string? FinishedUtc { get; set; }
        public long Total { get; set; }
        public long Processed { get; set; }
        public long Imported { get; set; }
        public long Duplicates { get; set; }
        public long Skipped { get; set; }
        public long Failed { get; set; }

        public ImportJob ToJob() => new(Id, Guid.Parse(Uid), (ImportMode)Mode, (ImportJobStatus)Status, DbFormat.ParseUtc(StartedUtc), DbFormat.ParseUtcOrNull(FinishedUtc),
            (int)Total, (int)Processed, (int)Imported, (int)Duplicates, (int)Skipped, (int)Failed, ImportDuplicates != 0);
    }

    private sealed class ItemRow
    {
        public long Id { get; set; }
        public long JobId { get; set; }
        public string SourcePath { get; set; } = "";
        public long Status { get; set; }
        public long? AssetId { get; set; }
        public string? Message { get; set; }
        public string? TargetPath { get; set; }

        public ImportItem ToItem() => new(Id, JobId, SourcePath, (ImportItemStatus)Status, AssetId, Message, TargetPath);
    }
#pragma warning restore CA1812
}
