using System.Data;
using System.Globalization;
using Dapper;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using PhotosWindows.Application.Abstractions;

namespace PhotosWindows.Infrastructure.Persistence;

/// <summary>
/// Opens connections to the catalog of the current library and runs work with limited retries on
/// transient lock errors (SQLITE_BUSY / SQLITE_LOCKED).
/// </summary>
public sealed class SqliteDb
{
    private const int MaxAttempts = 4;
    private readonly ILibraryContext _context;
    private readonly ILogger<SqliteDb> _logger;

    static SqliteDb()
    {
        DefaultTypeMap.MatchNamesWithUnderscores = true;
    }

    public SqliteDb(ILibraryContext context, ILogger<SqliteDb> logger)
    {
        _context = context;
        _logger = logger;
    }

    /// <param name="pooled">
    /// Pooled connections keep the file open after Dispose. Use <c>false</c> for one-off access to files that may be
    /// moved or deleted afterwards (backups, restores): Windows cannot rename or delete a file with an open handle.
    /// </param>
    public static string BuildConnectionString(string databasePath, bool readOnly = false, bool pooled = true) => new SqliteConnectionStringBuilder
    {
        DataSource = databasePath,
        Mode = readOnly ? SqliteOpenMode.ReadOnly : SqliteOpenMode.ReadWriteCreate,
        Cache = SqliteCacheMode.Private,
        Pooling = pooled,
        DefaultTimeout = 30,
    }.ToString();

    public static async Task<SqliteConnection> OpenAsync(string databasePath, CancellationToken cancellationToken, bool readOnly = false, bool pooled = true)
    {
        var connection = new SqliteConnection(BuildConnectionString(databasePath, readOnly, pooled));
        try
        {
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
            await connection.ExecuteAsync("PRAGMA foreign_keys = ON; PRAGMA busy_timeout = 5000; PRAGMA synchronous = NORMAL;").ConfigureAwait(false);
            return connection;
        }
        catch
        {
            await connection.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    public Task<SqliteConnection> OpenAsync(CancellationToken cancellationToken) =>
        OpenAsync(_context.RequireCurrent().DatabasePath, cancellationToken);

    public async Task<T> ReadAsync<T>(Func<SqliteConnection, Task<T>> work, CancellationToken cancellationToken)
    {
        return await RetryAsync(async () =>
        {
            await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
            return await work(connection).ConfigureAwait(false);
        }, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Runs <paramref name="work"/> inside a single IMMEDIATE transaction, committing on success.</summary>
    public async Task<T> WriteAsync<T>(Func<SqliteConnection, SqliteTransaction, Task<T>> work, CancellationToken cancellationToken)
    {
        return await RetryAsync(async () =>
        {
            await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
            // deferred:false => BEGIN IMMEDIATE, so writers queue on the lock instead of failing at upgrade time.
            await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(IsolationLevel.Serializable, cancellationToken).ConfigureAwait(false);
            var result = await work(connection, transaction).ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return result;
        }, cancellationToken).ConfigureAwait(false);
    }

    public Task WriteAsync(Func<SqliteConnection, SqliteTransaction, Task> work, CancellationToken cancellationToken) =>
        WriteAsync<bool>(async (c, t) => { await work(c, t).ConfigureAwait(false); return true; }, cancellationToken);

    private async Task<T> RetryAsync<T>(Func<Task<T>> action, CancellationToken cancellationToken)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                return await action().ConfigureAwait(false);
            }
            catch (SqliteException ex) when (IsTransient(ex) && attempt < MaxAttempts)
            {
                _logger.LogWarning("Catalog busy (SQLite {Code}); retry {Attempt}/{Max}.", ex.SqliteErrorCode, attempt, MaxAttempts - 1);
                await Task.Delay(TimeSpan.FromMilliseconds(100 * attempt * attempt), cancellationToken).ConfigureAwait(false);
            }
        }
    }

    private static bool IsTransient(SqliteException ex) => ex.SqliteErrorCode is 5 or 6; // SQLITE_BUSY, SQLITE_LOCKED
}

/// <summary>Canonical text encodings used in the catalog so values sort and compare correctly.</summary>
internal static class DbFormat
{
    public const string LocalFormat = "yyyy-MM-dd'T'HH:mm:ss.fff";
    public const string UtcFormat = "yyyy-MM-dd'T'HH:mm:ss.fffffff'Z'";

    public static string Local(DateTime value) => value.ToString(LocalFormat, CultureInfo.InvariantCulture);

    public static string Utc(DateTime value) =>
        (value.Kind == DateTimeKind.Local ? value.ToUniversalTime() : value).ToString(UtcFormat, CultureInfo.InvariantCulture);

    public static string? Utc(DateTime? value) => value is { } v ? Utc(v) : null;

    public static DateTime ParseLocal(string value) =>
        DateTime.SpecifyKind(DateTime.ParseExact(value, LocalFormat, CultureInfo.InvariantCulture), DateTimeKind.Unspecified);

    public static DateTime ParseUtc(string value) =>
        DateTime.ParseExact(value, UtcFormat, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal);

    public static DateTime? ParseUtcOrNull(string? value) => value is null ? null : ParseUtc(value);

    /// <summary>Windows paths are case-insensitive; this key enforces uniqueness the way the OS does.</summary>
    public static string PathKey(string storedPath) => storedPath.Replace('/', '\\').ToUpperInvariant();
}
