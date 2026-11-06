using Dapper;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using PhotosWindows.Application.Abstractions;

namespace PhotosWindows.Infrastructure.Persistence;

public sealed class SqliteCatalogStore : ICatalogStore
{
    private readonly ILibraryContext _context;
    private readonly IClock _clock;
    private readonly ILogger<SqliteCatalogStore> _logger;

    public SqliteCatalogStore(ILibraryContext context, IClock clock, ILogger<SqliteCatalogStore> logger)
    {
        _context = context;
        _clock = clock;
        _logger = logger;
    }

    public int CurrentSchemaVersion => Migrations.Latest;

    public async Task<int> InitializeAsync(string databasePath, CancellationToken cancellationToken)
    {
        await using var connection = await SqliteDb.OpenAsync(databasePath, cancellationToken).ConfigureAwait(false);

        // WAL survives across connections; it lets readers (grid) proceed while an import writes.
        await connection.ExecuteAsync("PRAGMA journal_mode = WAL;").ConfigureAwait(false);

        var version = await connection.ExecuteScalarAsync<long>("PRAGMA user_version;").ConfigureAwait(false);
        if (version > Migrations.Latest)
        {
            throw new CatalogVersionException(
                $"The catalog was created by a newer version of Photos Windows (schema {version}, this app supports {Migrations.Latest}). " +
                "Update the application to open it. The catalog has not been modified.");
        }

        foreach (var (target, description, sql) in Migrations.All.Where(m => m.Version > version))
        {
            _logger.LogInformation("Applying catalog migration {Version}: {Description}", target, description);
            await using var transaction = connection.BeginTransaction();
            await connection.ExecuteAsync(sql, transaction: transaction).ConfigureAwait(false);
            await connection.ExecuteAsync(
                "INSERT INTO schema_migrations(version, description, applied_utc) VALUES (@target, @description, @now);",
                new { target, description, now = DbFormat.Utc(_clock.UtcNow) },
                transaction).ConfigureAwait(false);
            await connection.ExecuteAsync($"PRAGMA user_version = {target};", transaction: transaction).ConfigureAwait(false);
            transaction.Commit();
            version = target;
        }

        return (int)version;
    }

    public async Task<int> GetSchemaVersionAsync(string databasePath, CancellationToken cancellationToken)
    {
        // Not pooled: this is used on backup copies that are renamed right after.
        await using var connection = await SqliteDb.OpenAsync(databasePath, cancellationToken, readOnly: true, pooled: false).ConfigureAwait(false);
        return (int)await connection.ExecuteScalarAsync<long>("PRAGMA user_version;").ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<string>> CheckIntegrityAsync(CancellationToken cancellationToken)
    {
        var problems = new List<string>();
        await using var connection = await SqliteDb.OpenAsync(_context.RequireCurrent().DatabasePath, cancellationToken).ConfigureAwait(false);
        var integrity = (await connection.QueryAsync<string>("PRAGMA integrity_check;").ConfigureAwait(false)).ToList();
        if (!(integrity.Count == 1 && integrity[0] == "ok"))
        {
            problems.AddRange(integrity.Select(i => "integrity: " + i));
        }

        var fk = await connection.QueryAsync("PRAGMA foreign_key_check;").ConfigureAwait(false);
        problems.AddRange(fk.Select(row => $"foreign key: {((IDictionary<string, object>)row)["table"]} rowid {((IDictionary<string, object>)row)["rowid"]}"));
        return problems;
    }

    public Task BackupToAsync(string destinationPath, CancellationToken cancellationToken) =>
        BackupDatabaseAsync(_context.RequireCurrent().DatabasePath, destinationPath, cancellationToken);

    public async Task BackupDatabaseAsync(string sourcePath, string destinationPath, CancellationToken cancellationToken)
    {
        if (File.Exists(destinationPath))
        {
            throw new IOException("The backup destination already exists; refusing to overwrite it.");
        }

        await using var source = await SqliteDb.OpenAsync(sourcePath, cancellationToken, pooled: false).ConfigureAwait(false);
        await using (var destination = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = destinationPath, Pooling = false }.ToString()))
        {
            await destination.OpenAsync(cancellationToken).ConfigureAwait(false);
            source.BackupDatabase(destination);
            // Make the copy self-contained (no -wal sidecar needed).
            await destination.ExecuteAsync("PRAGMA journal_mode = DELETE;").ConfigureAwait(false);
        }
    }

    public void ReleaseConnections() => SqliteConnection.ClearAllPools();
}

public sealed class CatalogVersionException : Exception
{
    public CatalogVersionException(string message) : base(message) { }
    public CatalogVersionException() { }
    public CatalogVersionException(string message, Exception inner) : base(message, inner) { }
}
