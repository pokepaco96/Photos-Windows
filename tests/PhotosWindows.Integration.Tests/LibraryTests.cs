using Dapper;
using Microsoft.Data.Sqlite;
using PhotosWindows.Application.Abstractions;
using PhotosWindows.Application.Library;
using PhotosWindows.Integration.Tests.Support;

namespace PhotosWindows.Integration.Tests;

public class LibraryTests
{
    [Fact]
    public async Task Create_builds_the_documented_layout_and_opens_it()
    {
        await using var host = new TestHost();
        var lib = await host.CreateLibraryAsync("My Photos");
        Assert.EndsWith("My Photos.pwlibrary", lib.RootPath);
        Assert.True(File.Exists(Path.Combine(lib.RootPath, "library.json")));
        Assert.True(File.Exists(lib.DatabasePath));
        Assert.True(Directory.Exists(lib.OriginalsPath));
        Assert.True(Directory.Exists(lib.CachePath));
        Assert.Equal(lib, host.Get<ILibraryContext>().Current);
        Assert.Equal(lib.RootPath, host.Libs.GetRecentLibraries()[0]);
    }

    [Fact]
    public async Task Creating_twice_with_the_same_name_never_reuses_a_folder()
    {
        await using var host = new TestHost();
        var a = await host.CreateLibraryAsync("Dup");
        var b = await host.CreateLibraryAsync("Dup");
        Assert.NotEqual(a.RootPath, b.RootPath);
    }

    [Fact]
    public async Task Catalog_has_latest_schema_wal_foreign_keys_and_fts5()
    {
        await using var host = new TestHost();
        var lib = await host.CreateLibraryAsync();
        await using var c = new SqliteConnection($"Data Source={lib.DatabasePath};Pooling=False");
        await c.OpenAsync();
        Assert.Equal(host.Get<ICatalogStore>().CurrentSchemaVersion, await c.ExecuteScalarAsync<long>("PRAGMA user_version"));
        Assert.Equal("wal", await c.ExecuteScalarAsync<string>("PRAGMA journal_mode"));
        Assert.Equal(1, await c.ExecuteScalarAsync<long>("SELECT count(*) FROM schema_migrations"));
        Assert.Equal(1, await c.ExecuteScalarAsync<long>("SELECT count(*) FROM sqlite_master WHERE name = 'assets_fts'"));
        Assert.Empty(await host.Get<ICatalogStore>().CheckIntegrityAsync(CancellationToken.None));
    }

    [Fact]
    public async Task Initialize_is_idempotent()
    {
        await using var host = new TestHost();
        var lib = await host.CreateLibraryAsync();
        var store = host.Get<ICatalogStore>();
        Assert.Equal(store.CurrentSchemaVersion, await store.InitializeAsync(lib.DatabasePath, CancellationToken.None));
        Assert.Equal(store.CurrentSchemaVersion, await store.InitializeAsync(lib.DatabasePath, CancellationToken.None));
    }

    [Fact]
    public async Task Close_and_reopen_keeps_data()
    {
        await using var host = new TestHost();
        var lib = await host.CreateLibraryAsync();
        Fixtures.Jpeg(Path.Combine(host.SourceDir("a"), "one.jpg"));
        await host.ImportAsync(Path.Combine(host.Sources, "a"), Domain.ImportMode.AddInPlace);
        host.Libs.Close();
        Assert.Null(host.Get<ILibraryContext>().Current);
        await host.Libs.OpenAsync(lib.RootPath, CancellationToken.None);
        Assert.Single(await host.AllAsync());
    }

    [Fact]
    public async Task Opening_invalid_locations_reports_clear_errors()
    {
        await using var host = new TestHost();
        var missing = await Assert.ThrowsAsync<LibraryOpenException>(() => host.Libs.OpenAsync(Path.Combine(host.Root, "nope"), CancellationToken.None));
        Assert.Equal(LibraryOpenError.NotFound, missing.Error);

        var notLibrary = await Assert.ThrowsAsync<LibraryOpenException>(() => host.Libs.OpenAsync(host.Sources, CancellationToken.None));
        Assert.Equal(LibraryOpenError.NotALibrary, notLibrary.Error);

        var lib = await host.CreateLibraryAsync();
        host.Libs.Close();
        SqliteConnection.ClearAllPools();
        File.Move(lib.DatabasePath, lib.DatabasePath + ".moved");
        var noCatalog = await Assert.ThrowsAsync<LibraryOpenException>(() => host.Libs.OpenAsync(lib.RootPath, CancellationToken.None));
        Assert.Equal(LibraryOpenError.CatalogMissing, noCatalog.Error);
    }

    [Fact]
    public async Task Catalog_from_a_newer_version_is_refused_without_modification()
    {
        await using var host = new TestHost();
        var lib = await host.CreateLibraryAsync();
        host.Libs.Close();
        SqliteConnection.ClearAllPools();
        await using (var c = new SqliteConnection($"Data Source={lib.DatabasePath};Pooling=False"))
        {
            await c.OpenAsync();
            await c.ExecuteAsync("PRAGMA user_version = 99");
        }

        var ex = await Assert.ThrowsAsync<LibraryOpenException>(() => host.Libs.OpenAsync(lib.RootPath, CancellationToken.None));
        Assert.Equal(LibraryOpenError.NewerVersion, ex.Error);
        await using var check = new SqliteConnection($"Data Source={lib.DatabasePath};Pooling=False");
        await check.OpenAsync();
        Assert.Equal(99, await check.ExecuteScalarAsync<long>("PRAGMA user_version"));
    }

    [Fact]
    public async Task Damaged_catalog_is_reported_as_damaged()
    {
        await using var host = new TestHost();
        var lib = await host.CreateLibraryAsync();
        host.Libs.Close();
        SqliteConnection.ClearAllPools();
        foreach (var f in new[] { lib.DatabasePath + "-wal", lib.DatabasePath + "-shm" }.Where(File.Exists)) File.Delete(f);
        await File.WriteAllTextAsync(lib.DatabasePath, "this is not a database");
        var ex = await Assert.ThrowsAsync<LibraryOpenException>(() => host.Libs.OpenAsync(lib.RootPath, CancellationToken.None));
        Assert.Equal(LibraryOpenError.Damaged, ex.Error);
    }

    [Fact]
    public async Task Storage_report_counts_items_and_cache_location_can_move()
    {
        await using var host = new TestHost();
        await host.CreateLibraryAsync();
        Fixtures.Jpeg(Path.Combine(host.SourceDir("s"), "a.jpg"));
        await host.ImportAsync(Path.Combine(host.Sources, "s"), Domain.ImportMode.CopyToLibrary);
        await host.Thumbnails.ProcessPendingAsync(10, CancellationToken.None);
        var report = await host.Libs.GetStorageReportAsync(CancellationToken.None);
        Assert.Equal(1, report.Catalog.PhotoCount);
        Assert.True(report.Catalog.ManagedBytes > 0);
        Assert.True(report.CacheBytes > 0);
        Assert.True(report.DatabaseBytes > 0);

        var newCache = Path.Combine(host.Root, "cache-elsewhere");
        var moved = await host.Libs.SetCacheLocationAsync(newCache, CancellationToken.None);
        Assert.StartsWith(newCache, moved.CachePath);
        Assert.False(Directory.Exists(Path.Combine(moved.RootPath, "cache")));
    }
}
