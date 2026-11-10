using System.Diagnostics;
using Dapper;
using Microsoft.Data.Sqlite;
using PhotosWindows.Application.Abstractions;
using PhotosWindows.Application.Import;
using PhotosWindows.Application.Library;
using PhotosWindows.Application.Memories;
using PhotosWindows.Application.People;
using PhotosWindows.Domain;
using PhotosWindows.Integration.Tests.Support;

namespace PhotosWindows.Integration.Tests;

/// <summary>Memories end to end on a real catalog. The fake clock says 15 March 2025, 10:00 UTC, zone UTC+1.</summary>
public sealed class MemoriesServiceTests : IAsyncLifetime
{
    private TestHost _host = null!;
    private LibraryInfo _library = null!;
    private string _dir = null!;
    private Dictionary<string, long> _ids = null!;

    public async Task InitializeAsync()
    {
        _host = new TestHost();
        _library = await _host.CreateLibraryAsync();
        _dir = _host.SourceDir("memories");
        var seed = 700;
        void Photo(string name, DateTime taken, GeoLocation? location = null, TimeSpan? offset = null) =>
            Fixtures.Jpeg(Path.Combine(_dir, name), new Fixtures.PhotoSpec { Seed = seed++, Taken = taken, Offset = offset, Location = location });

        // On this day, two years ago: three photos without UTC offset plus a video stamped in UTC.
        for (var i = 0; i < 3; i++) Photo($"day2023_{i}.jpg", new DateTime(2023, 3, 15, 9 + i, 0, 0));
        Fixtures.QuickTime(Path.Combine(_dir, "day2023_clip.mov"), TimeSpan.FromSeconds(2), new DateTime(2023, 3, 14, 23, 30, 0, DateTimeKind.Utc)); // 00:30 on the 15th at UTC+1
        // The day before and after, to prove the boundaries.
        Photo("eve2023.jpg", new DateTime(2023, 3, 14, 23, 59, 59));
        Photo("after2023.jpg", new DateTime(2023, 3, 16, 0, 0, 0));
        // One evening in 2024: an event of nine photos in Madrid.
        for (var i = 0; i < 9; i++) Photo($"party_{i}.jpg", new DateTime(2024, 6, 1, 19, i * 5, 0), new GeoLocation(40.4168, -3.7038), TimeSpan.FromHours(2));
        await _host.ImportAsync(_dir, ImportMode.AddInPlace);
        _ids = (await _host.AllAsync()).ToDictionary(a => a.FileName, a => a.Id);
    }

    public async Task DisposeAsync() => await _host.DisposeAsync();

    private MemoriesService Memories => _host.Get<MemoriesService>();

    private async Task<IReadOnlyList<MemoryCandidate>> Get(bool regenerate = true) => (await Memories.GetAsync(regenerate, CancellationToken.None)).Memories;

    private string Name(long id) => _ids.Single(kv => kv.Value == id).Key;

    [Fact]
    public async Task On_this_day_uses_local_days_for_photos_without_offset_and_for_utc_videos()
    {
        Assert.Equal(new DateOnly(2025, 3, 15), Memories.Today);
        var day = (await Get()).Single(m => m.Kind == MemoryKind.OnThisDay);
        Assert.Equal("onthisday:2023-03-15", day.Key);
        Assert.Equal(["day2023_0.jpg", "day2023_1.jpg", "day2023_2.jpg", "day2023_clip.mov"], day.AssetIds.Select(Name).Order());
        Assert.Equal(2, day.Why.YearsAgo);
        Assert.NotEqual(_ids["day2023_clip.mov"], day.CoverAssetId); // a photo, not the video, is the cover
    }

    [Fact]
    public async Task Today_follows_the_users_time_zone_across_midnight()
    {
        _host.Clock.UtcNow = new DateTime(2025, 3, 15, 22, 59, 0, DateTimeKind.Utc);
        Assert.Equal(new DateOnly(2025, 3, 15), Memories.Today);
        _host.Clock.UtcNow = new DateTime(2025, 3, 15, 23, 0, 0, DateTimeKind.Utc); // already the 16th at UTC+1
        Assert.Equal(new DateOnly(2025, 3, 16), Memories.Today);
        Assert.DoesNotContain(await Get(), m => m.Key == "onthisday:2023-03-15");
        _host.Clock.UtcNow = new DateTime(2025, 12, 31, 23, 30, 0, DateTimeKind.Utc); // New Year already
        Assert.Equal(new DateOnly(2026, 1, 1), Memories.Today);
    }

    [Fact]
    public async Task Event_is_found_and_explained_with_its_facts()
    {
        var party = (await Get()).Single(m => m.Kind == MemoryKind.Event);
        Assert.Equal("event:202406011900", party.Key);
        Assert.Equal((9, 9), (party.Why.Count, party.Why.Matched));
        Assert.Equal(40.4168, party.Why.Latitude!.Value, 3);
        Assert.Equal(new DateTime(2024, 6, 1, 19, 40, 0), party.Why.Last);
    }

    [Fact]
    public async Task Hidden_deleted_disconnected_and_undecodable_items_are_never_used()
    {
        await _host.Organization.SetHiddenAsync([_ids["day2023_0.jpg"]], true, CancellationToken.None);
        var memories = await Get();
        Assert.Equal(3, memories.Single(m => m.Kind == MemoryKind.OnThisDay).AssetIds.Count);

        await _host.Organization.MoveToTrashAsync([_ids["day2023_1.jpg"]], CancellationToken.None);
        Assert.DoesNotContain(await Get(), m => m.Kind == MemoryKind.OnThisDay); // two left: below the minimum, nothing half-empty is shown

        Memories.Rules = new MemoryRules { EventMinItems = 5 };
        // A thumbnail that failed (corrupt original) and an original that disappeared (disconnected drive).
        await _host.Assets.SetThumbnailStateAsync(_ids["party_0.jpg"], ThumbnailState.Failed, CancellationToken.None);
        File.Delete(Path.Combine(_dir, "party_1.jpg"));
        await _host.Maintenance.VerifyAsync(false, null, CancellationToken.None);
        var party = (await Get()).Single(m => m.Kind == MemoryKind.Event);
        Assert.Equal(7, party.AssetIds.Count);
        Assert.DoesNotContain(_ids["party_0.jpg"], party.AssetIds);
        Assert.DoesNotContain(_ids["party_1.jpg"], party.AssetIds);
    }

    [Fact]
    public async Task Hiding_with_undo_and_exclusions_persist_after_reopening_and_delete_nothing()
    {
        var before = Directory.GetFiles(_dir).ToDictionary(f => f, Fixtures.Sha256);
        var count = (await _host.AllAsync()).Count;
        var party = (await Get()).Single(m => m.Kind == MemoryKind.Event);

        await Memories.HideAsync(party, CancellationToken.None);
        Assert.DoesNotContain(await Get(regenerate: false), m => m.Key == party.Key); // hiding invalidates the cache
        await Memories.UnhideAsync(party.Key, CancellationToken.None);                 // undo
        Assert.Contains(await Get(), m => m.Key == party.Key);
        await Memories.HideAsync(party, CancellationToken.None);
        await Memories.HideAsync(party, CancellationToken.None);                       // twice: still one row

        await Memories.ExcludeKindAsync(MemoryKind.OnThisDay, CancellationToken.None);
        await Memories.ExcludeKindAsync(MemoryKind.OnThisDay, CancellationToken.None); // idempotent
        var range = await Memories.ExcludeRangeAsync(new DateTime(2024, 12, 31), new DateTime(2024, 1, 1), CancellationToken.None); // either order

        _host.Libs.Close();
        SqliteConnection.ClearAllPools();
        await _host.Libs.OpenAsync(_library.RootPath, CancellationToken.None);

        Assert.Empty(await Get());
        var hidden = Assert.Single(await Memories.GetHiddenAsync(CancellationToken.None));
        Assert.Equal((party.Key, MemoryKind.Event, party.Start), (hidden.Key, hidden.Kind, hidden.Start));
        var exclusions = await Memories.GetExclusionsAsync(CancellationToken.None);
        Assert.Equal(2, exclusions.Count);
        var r = exclusions.Single(e => e.Id == range);
        Assert.Equal((new DateTime(2024, 1, 1), new DateTime(2024, 12, 31)), (r.From, r.To));
        Assert.True(r.Covers(new DateTime(2024, 12, 31, 23, 59, 59)));
        Assert.False(r.Covers(new DateTime(2025, 1, 1)));

        // Removing the exclusions and the hidden mark brings everything back; no photo was deleted or changed.
        foreach (var e in exclusions) await Memories.RemoveExclusionAsync(e.Id, CancellationToken.None);
        await Memories.UnhideAsync(party.Key, CancellationToken.None);
        Assert.Equal(2, (await Get()).Count);
        Assert.Equal(count, (await _host.AllAsync()).Count);
        Assert.Equal(before, Directory.GetFiles(_dir).ToDictionary(f => f, Fixtures.Sha256));
    }

    [Fact]
    public async Task Excluded_date_range_removes_those_photos_from_every_memory()
    {
        await Memories.ExcludeRangeAsync(new DateTime(2023, 3, 15), new DateTime(2023, 3, 15), CancellationToken.None);
        var memories = await Get();
        Assert.DoesNotContain(memories, m => m.Kind == MemoryKind.OnThisDay);
        Assert.Contains(memories, m => m.Kind == MemoryKind.Event);
    }

    [Fact]
    public async Task Cache_is_reused_until_something_changes_or_regeneration_is_asked()
    {
        var first = await Memories.GetAsync(false, CancellationToken.None);
        Assert.Same(first, await Memories.GetAsync(false, CancellationToken.None));
        Assert.NotSame(first, await Memories.GetAsync(true, CancellationToken.None));
        var second = await Memories.GetAsync(false, CancellationToken.None);
        await _host.Organization.SetFavoriteAsync([_ids["party_3.jpg"]], true, CancellationToken.None); // a library change
        var third = await Memories.GetAsync(false, CancellationToken.None);
        Assert.NotSame(second, third);
        Assert.Equal(_ids["party_3.jpg"], third.Memories.Single(m => m.Kind == MemoryKind.Event).CoverAssetId); // favourite becomes the cover
        // The user can turn the favourite preference off.
        var store = _host.Get<ISettingsStore>();
        store.Save(store.Load() with { MemoriesPreferFavorites = false });
        var plain = await Memories.GetAsync(false, CancellationToken.None);
        Assert.NotEqual(_ids["party_3.jpg"], plain.Memories.Single(m => m.Kind == MemoryKind.Event).CoverAssetId);
    }

    [Fact]
    public async Task People_memories_need_people_turned_on_with_consent_and_a_name_given_by_the_user()
    {
        Memories.Rules = new MemoryRules { PersonMinItems = 5 };
        // The same photos already form an event, and overlapping memories are shown once: leave events out here.
        await Memories.ExcludeKindAsync(MemoryKind.Event, CancellationToken.None);
        await using (var c = new SqliteConnection($"Data Source={_library.DatabasePath}"))
        {
            await c.OpenAsync();
            await c.ExecuteAsync("INSERT INTO people(id, name, created_utc, updated_utc) VALUES (1, 'Ana', '2025-01-01T00:00:00Z', '2025-01-01T00:00:00Z'), (2, NULL, '2025-01-01T00:00:00Z', '2025-01-01T00:00:00Z')");
            foreach (var (name, id) in _ids.Where(kv => kv.Key.StartsWith("party_", StringComparison.Ordinal)))
            {
                await c.ExecuteAsync("INSERT INTO faces(asset_id, model_version, x, y, w, h, score, embedding, person_id, created_utc) VALUES (@id, 'test', 0.1, 0.1, 0.2, 0.2, 0.9, x'00', @person, '2025-01-01T00:00:00Z')",
                    new { id, person = name is "party_0.jpg" or "party_1.jpg" ? 2 : 1 });
            }
        }

        // Face data exists but People is off for this library: Memories must not use it.
        Assert.False(await _host.Get<PeopleService>().IsEnabledAsync(CancellationToken.None));
        var off = await Memories.GetAsync(true, CancellationToken.None);
        Assert.False(off.PeopleUsed);
        Assert.DoesNotContain(off.Memories, m => m.Kind == MemoryKind.Person);

        await _host.Get<PeopleService>().EnableAsync(CancellationToken.None);
        var on = await Memories.GetAsync(false, CancellationToken.None); // enabling People invalidates the cache
        Assert.True(on.PeopleUsed);
        var ana = on.Memories.Single(m => m.Kind == MemoryKind.Person);
        Assert.Equal(("person:1", "Ana", 7), (ana.Key, ana.Why.PersonName, ana.AssetIds.Count)); // the unnamed group is never shown

        await Memories.ExcludeKindAsync(MemoryKind.Person, CancellationToken.None);
        Assert.False((await Memories.GetAsync(false, CancellationToken.None)).PeopleUsed);
        await _host.Get<PeopleService>().DisableAsync(CancellationToken.None);
        Assert.False(await _host.Get<PeopleService>().IsEnabledAsync(CancellationToken.None)); // Memories never turns People back on
    }

    [Fact]
    public async Task Album_from_a_memory_holds_exactly_its_photos()
    {
        var party = (await Get()).Single(m => m.Kind == MemoryKind.Event);
        var album = await Memories.CreateAlbumAsync(party, "  Fiesta  ", CancellationToken.None);
        var inAlbum = await _host.Assets.QueryAsync(new AssetQuery { AlbumId = album }, CancellationToken.None);
        Assert.Equal(party.AssetIds.Order(), inAlbum.Select(a => a.Id).Order());
        Assert.Equal("Fiesta", (await _host.Organization.GetAlbumsAsync(CancellationToken.None)).Single(a => a.Id == album).Name);
        await Assert.ThrowsAnyAsync<ArgumentException>(() => Memories.CreateAlbumAsync(party, "  ", CancellationToken.None));
    }

    [Fact]
    public async Task Catalog_from_version_2_gains_the_memory_tables_and_the_migration_can_be_undone()
    {
        await Memories.HideAsync((await Get()).First(), CancellationToken.None);
        // Back to exactly what version 2 had (the documented way to undo migration 3).
        await using (var c = new SqliteConnection($"Data Source={_library.DatabasePath}"))
        {
            await c.OpenAsync();
            await c.ExecuteAsync("DROP TABLE memory_exclusions; DROP TABLE memory_hidden; DELETE FROM schema_migrations WHERE version = 3; PRAGMA user_version = 2;");
            Assert.Equal("ok", await c.ExecuteScalarAsync<string>("PRAGMA integrity_check"));
            Assert.Equal(0, await c.ExecuteScalarAsync<long>("SELECT count(*) FROM pragma_foreign_key_check"));
        }

        SqliteConnection.ClearAllPools();
        _host.Libs.Close();
        await _host.Libs.OpenAsync(_library.RootPath, CancellationToken.None);
        await using var check = new SqliteConnection($"Data Source={_library.DatabasePath}");
        Assert.Equal(_host.Get<ICatalogStore>().CurrentSchemaVersion, await check.ExecuteScalarAsync<long>("PRAGMA user_version"));
        Assert.Equal(3, _host.Get<ICatalogStore>().CurrentSchemaVersion);
        Assert.Equal(_ids.Count, (await _host.AllAsync()).Count);       // the library's data is intact
        Assert.Empty(await Memories.GetHiddenAsync(CancellationToken.None));
        Assert.Equal(2, (await Get()).Count);
    }

    [Fact]
    public async Task Library_without_dates_gps_or_enough_photos_simply_has_no_memories()
    {
        await using var host = new TestHost();
        await host.CreateLibraryAsync("Vacía");
        Assert.Empty((await host.Get<MemoriesService>().GetAsync(true, CancellationToken.None)).Memories);
        var dir = host.SourceDir("few");
        Fixtures.Png(Path.Combine(dir, "sin_exif.png"), seed: 1);
        Fixtures.Jpeg(Path.Combine(dir, "una.jpg"), new Fixtures.PhotoSpec { Seed = 2, Taken = new DateTime(2023, 3, 15, 9, 0, 0) });
        await host.ImportAsync(dir, ImportMode.AddInPlace);
        var result = await host.Get<MemoriesService>().GetAsync(true, CancellationToken.None);
        Assert.Empty(result.Memories);
        Assert.Equal(2, result.ItemsConsidered);
    }
}

/// <summary>Measured, not asserted against a target: generating and ranking memories for large catalogs.</summary>
public sealed class MemoriesScaleTests
{
    [Theory]
    [InlineData(10_000)]
    [InlineData(100_000)]
    public void Generation_time_is_measured(int count)
    {
        var rnd = new Random(9);
        var items = new MemoryItem[count];
        for (var i = 0; i < count; i++)
        {
            var away = rnd.Next(12) == 0;
            items[i] = new MemoryItem(i + 1, new DateTime(2012, 1, 1).AddMinutes(rnd.Next(0, 7_500_000)),
                rnd.Next(4) == 0 ? null : (away ? 48.8 : 40.4) + rnd.NextDouble() * 0.2, rnd.Next(4) == 0 ? null : (away ? 2.3 : -3.7) + rnd.NextDouble() * 0.2,
                rnd.Next(20) == 0, rnd.Next(30) == 0, rnd.Next(25) == 0);
        }

        // A coordinate can be missing on one axis only in this generator: normalise to "both or none".
        for (var i = 0; i < count; i++) if (items[i].Latitude is null || items[i].Longitude is null) items[i] = items[i] with { Latitude = null, Longitude = null };
        var rules = new MemoryRules();
        MemoryCandidateGenerator.Generate(items.AsSpan(0, 2000).ToArray(), new DateOnly(2026, 11, 10), rules); // warm-up
        var sw = Stopwatch.StartNew();
        var candidates = MemoryCandidateGenerator.Generate(items, new DateOnly(2026, 11, 10), rules);
        var generated = sw.Elapsed;
        sw.Restart();
        var ranked = MemoryRanker.Rank(candidates, new DateOnly(2026, 11, 10), rules);
        Console.WriteLine($"[perf] memories {count} items: generate {generated.TotalMilliseconds:0} ms ({candidates.Count} candidates), rank {sw.Elapsed.TotalMilliseconds:0} ms ({ranked.Count} shown)");
        Assert.NotEmpty(ranked);
        Assert.True(ranked.Count <= rules.MaxMemories);
    }
}
