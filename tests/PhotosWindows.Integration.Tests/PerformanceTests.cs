using System.Diagnostics;
using Dapper;
using Microsoft.Data.Sqlite;
using PhotosWindows.Application.Abstractions;
using PhotosWindows.Domain;
using PhotosWindows.Integration.Tests.Support;
using Xunit.Abstractions;

namespace PhotosWindows.Integration.Tests;

/// <summary>
/// Catalogue performance on a synthetic library (no image files: measures the database and query layer that the
/// grid, search and smart albums depend on). Thresholds are generous ceilings to catch regressions, not targets.
/// Results are recorded in docs/PERFORMANCE.md.
/// </summary>
[Trait("Category", "Performance")]
public class PerformanceTests(ITestOutputHelper output)
{
    private static async Task SeedAsync(string databasePath, int count)
    {
        await using var c = new SqliteConnection($"Data Source={databasePath}");
        await c.OpenAsync();
        await using var t = c.BeginTransaction();
        var rng = new Random(42);
        string[] cameras = ["Acme Z100", "Pix 7", "Lumen X", "Nova 3"];
        string[] words = ["beach", "mountain", "family", "city", "snow", "birthday", "sunset", "forest", "river", "concert"];
        var start = new DateTime(2005, 1, 1);
        for (var i = 0; i < count; i++)
        {
            var date = start.AddMinutes(rng.Next(0, 20 * 365 * 24 * 60));
            var id = await c.ExecuteScalarAsync<long>("""
                INSERT INTO assets (uid, media_type, capture_local, date_source, imported_utc, file_modified_utc, pixel_width, pixel_height, orientation,
                    file_name, format, file_size, camera_make, camera_model, is_favorite, title, latitude, longitude, thumb_state)
                VALUES (@uid, @type, @capture, 1, @imported, @imported, 4000, 3000, 1, @name, 'JPEG', 3000000, 'Acme', @camera, @fav, @title, @lat, @lon, 1);
                SELECT last_insert_rowid();
                """, new
            {
                uid = Guid.NewGuid().ToString("D"),
                type = i % 12 == 0 ? 1 : 0,
                capture = date.ToString("yyyy-MM-dd'T'HH:mm:ss.fff", System.Globalization.CultureInfo.InvariantCulture),
                imported = "2025-01-01T00:00:00.0000000Z",
                name = $"IMG_{i:000000}.jpg",
                camera = cameras[i % cameras.Length],
                fav = i % 20 == 0 ? 1 : 0,
                title = i % 7 == 0 ? $"{words[i % words.Length]} trip {i}" : null,
                lat = i % 3 == 0 ? (double?)(40 + rng.NextDouble()) : null,
                lon = i % 3 == 0 ? (double?)(-3 - rng.NextDouble()) : null,
            }, t);
            await c.ExecuteAsync("INSERT INTO asset_files (asset_id, role, storage_mode, stored_path, stored_path_key, size_bytes, sha256, file_modified_utc, format, last_verified_utc) VALUES (@id, 0, 0, @p, @k, 3000000, @h, '2025-01-01T00:00:00.0000000Z', 'JPEG', '2025-01-01T00:00:00.0000000Z')",
                new { id, p = $"C:\\Photos\\IMG_{i:000000}.jpg", k = $"C:\\PHOTOS\\IMG_{i:000000}.JPG", h = Guid.NewGuid().ToString("N") }, t);
            if (i % 5 == 0)
            {
                await c.ExecuteAsync("INSERT INTO keywords(name) VALUES (@w) ON CONFLICT(name) DO NOTHING; INSERT OR IGNORE INTO asset_keywords SELECT @id, id FROM keywords WHERE name = @w", new { id, w = words[rng.Next(words.Length)] }, t);
            }
        }

        t.Commit();
    }

    private async Task<TimeSpan> MeasureAsync(string label, Func<Task<int>> action)
    {
        await action(); // warm-up (connection pool, statement cache)
        var sw = Stopwatch.StartNew();
        var n = await action();
        sw.Stop();
        output.WriteLine($"{label}: {sw.Elapsed.TotalMilliseconds:0} ms ({n} rows)");
        return sw.Elapsed;
    }

    [Theory]
    [InlineData(10_000)]
    [InlineData(50_000)]
    public async Task Catalogue_queries_stay_fast(int count)
    {
        await using var host = new TestHost();
        var lib = await host.CreateLibraryAsync();
        var seed = Stopwatch.StartNew();
        await SeedAsync(lib.DatabasePath, count);
        await host.Assets.RefreshSearchIndexAsync(null, CancellationToken.None);
        output.WriteLine($"Seed + FTS rebuild of {count} assets: {seed.Elapsed.TotalSeconds:0.0} s");

        var ceiling = TimeSpan.FromSeconds(count / 10_000.0 * 1.5);
        Assert.True(await MeasureAsync("Timeline (all, by date)", async () => (await host.Assets.QueryAsync(new AssetQuery(), CancellationToken.None)).Count) < ceiling);
        Assert.True(await MeasureAsync("Count", () => host.Assets.CountAsync(new AssetQuery(), CancellationToken.None)) < ceiling);
        Assert.True(await MeasureAsync("Favorites", async () => (await host.Assets.QueryAsync(new AssetQuery { Scope = LibraryScope.Favorites }, CancellationToken.None)).Count) < ceiling);
        Assert.True(await MeasureAsync("Search 'beach'", async () => (await host.Search.SearchAsync("beach", null, CancellationToken.None)).Count) < ceiling);
        Assert.True(await MeasureAsync("Search '2019 video'", async () => (await host.Search.SearchAsync("2019 video", null, CancellationToken.None)).Count) < ceiling);
        Assert.True(await MeasureAsync("Search file name 'IMG_0042'", async () => (await host.Search.SearchAsync("IMG_0042", null, CancellationToken.None)).Count) < ceiling);
        var smart = new SmartAlbumDefinition(SmartMatch.All, [new SmartRule(SmartField.Keyword, SmartOperator.Is, "beach"), new SmartRule(SmartField.Camera, SmartOperator.Contains, "pix"), new SmartRule(SmartField.HasLocation, SmartOperator.Is, "true")]);
        Assert.True(await MeasureAsync("Smart album (keyword + camera + location)", async () => (await host.Assets.QueryAsync(new AssetQuery { Smart = smart }, CancellationToken.None)).Count) < ceiling);
        Assert.True(await MeasureAsync("Area query", async () => (await host.Assets.QueryAsync(new AssetQuery { Area = (40.2, -3.8, 40.6, -3.2) }, CancellationToken.None)).Count) < ceiling);
        Assert.True(await MeasureAsync("Statistics", async () => (await host.Assets.GetStatisticsAsync(CancellationToken.None)).PhotoCount) < ceiling);

        var before = GC.GetTotalMemory(forceFullCollection: true);
        var rows = await host.Assets.QueryAsync(new AssetQuery(), CancellationToken.None);
        var after = GC.GetTotalMemory(forceFullCollection: true);
        output.WriteLine($"Timeline rows in memory: ~{(after - before) / Math.Max(1, rows.Count)} bytes per item");
        GC.KeepAlive(rows);
    }
}
