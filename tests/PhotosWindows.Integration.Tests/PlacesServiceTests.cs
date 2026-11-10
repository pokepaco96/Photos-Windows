using System.Diagnostics;
using PhotosWindows.Application.Import;
using PhotosWindows.Application.Library;
using PhotosWindows.Application.Places;
using PhotosWindows.Domain;
using PhotosWindows.Integration.Tests.Support;

namespace PhotosWindows.Integration.Tests;

/// <summary>Places: filters, clusters, the no-GPS hint, editing locations and filter persistence.</summary>
public sealed class PlacesServiceTests : IAsyncLifetime
{
    private TestHost _host = null!;
    private string _dir = null!;
    private Dictionary<string, long> _ids = null!;

    private static readonly (string Name, double? Lat, double? Lon, DateTime Taken)[] Photos =
    [
        ("madrid_1.jpg", 40.4168, -3.7038, new DateTime(2024, 5, 2, 12, 0, 0)),
        ("madrid_2.jpg", 40.4170, -3.7036, new DateTime(2024, 5, 3, 12, 0, 0)),
        ("paris.jpg", 48.8566, 2.3522, new DateTime(2023, 9, 10, 12, 0, 0)),
        ("fiji.jpg", -17.7134, 178.065, new DateTime(2023, 8, 1, 9, 0, 0)),
        ("samoa.jpg", -14.0, -179.5, new DateTime(2023, 8, 3, 9, 0, 0)),
        ("nogps_1.jpg", null, null, new DateTime(2024, 5, 2, 13, 0, 0)),
        ("nogps_2.jpg", null, null, new DateTime(2022, 1, 1, 13, 0, 0)),
    ];

    public async Task InitializeAsync()
    {
        _host = new TestHost();
        await _host.CreateLibraryAsync();
        _dir = _host.SourceDir("places");
        var seed = 100;
        foreach (var p in Photos)
        {
            Fixtures.Jpeg(Path.Combine(_dir, p.Name), new Fixtures.PhotoSpec
            {
                Seed = seed++,
                Taken = p.Taken,
                Location = p.Lat is { } lat ? new GeoLocation(lat, p.Lon!.Value) : null,
            });
        }

        await _host.ImportAsync(_dir, ImportMode.AddInPlace);
        _ids = (await _host.AllAsync()).ToDictionary(a => a.FileName, a => a.Id);
    }

    public async Task DisposeAsync() => await _host.DisposeAsync();

    private PlacesService Places => _host.Get<PlacesService>();

    private Task<PlacesSnapshot> Load(PlacesFilter? filter = null) => Places.LoadAsync(filter ?? new PlacesFilter(), CancellationToken.None);

    private string[] Names(PlacesSnapshot s) => s.Points.Select(p => _ids.Single(kv => kv.Value == p.AssetId).Key).Order().ToArray();

    [Fact]
    public async Task Loads_located_photos_and_counts_the_ones_without_gps()
    {
        var s = await Load();
        Assert.Equal(["fiji.jpg", "madrid_1.jpg", "madrid_2.jpg", "paris.jpg", "samoa.jpg"], Names(s));
        Assert.Equal(2, s.WithoutLocationCount);
        Assert.True(s.Bounds!.Value.CrossesAntimeridian); // Fiji + Samoa + Europe: the smallest box wraps the date line
    }

    [Fact]
    public async Task Filters_by_inclusive_date_range_type_favorites_and_album()
    {
        var day = await Load(new PlacesFilter { From = new DateTime(2024, 5, 3), To = new DateTime(2024, 5, 3) });
        Assert.Equal(["madrid_2.jpg"], Names(day));

        var inverted = await Load(new PlacesFilter { From = new DateTime(2024, 5, 3), To = new DateTime(2024, 5, 2) });
        Assert.Equal(["madrid_1.jpg", "madrid_2.jpg"], Names(inverted));
        Assert.Equal(1, inverted.WithoutLocationCount);

        Assert.Empty((await Load(new PlacesFilter { MediaType = MediaType.Video })).Points);
        Assert.Equal(5, (await Load(new PlacesFilter { MediaType = MediaType.Photo })).Points.Count);

        await _host.Organization.SetFavoriteAsync([_ids["paris.jpg"], _ids["nogps_2.jpg"]], true, CancellationToken.None);
        var fav = await Load(new PlacesFilter { FavoritesOnly = true });
        Assert.Equal(["paris.jpg"], Names(fav));
        Assert.Equal(1, fav.WithoutLocationCount);

        var album = await _host.Organization.CreateAlbumAsync("Pacific", null, [_ids["fiji.jpg"], _ids["samoa.jpg"], _ids["nogps_1.jpg"]], CancellationToken.None);
        var inAlbum = await Load(new PlacesFilter { AlbumId = album });
        Assert.Equal(["fiji.jpg", "samoa.jpg"], Names(inAlbum));
        Assert.Equal(1, inAlbum.WithoutLocationCount);
    }

    [Fact]
    public async Task Hidden_and_deleted_photos_never_appear()
    {
        await _host.Organization.SetHiddenAsync([_ids["paris.jpg"]], true, CancellationToken.None);
        await _host.Organization.MoveToTrashAsync([_ids["fiji.jpg"]], CancellationToken.None);
        Assert.Equal(["madrid_1.jpg", "madrid_2.jpg", "samoa.jpg"], Names(await Load()));
    }

    [Fact]
    public async Task Clusters_group_nearby_photos_and_list_groups_are_newest_first()
    {
        var s = await Load();
        var world = s.ClustersAt(0);
        Assert.Equal(5, world.Sum(c => c.Count));
        var groups = s.ListGroups();
        Assert.Equal(4, groups.Count); // Madrid (2), Paris, Fiji, Samoa
        Assert.Equal(2, groups[0].Count);
        Assert.Equal(_ids["madrid_2.jpg"], groups[0].CoverAssetId);
        Assert.True(groups.Zip(groups.Skip(1)).All(p => p.First.Last >= p.Second.Last));
        Assert.Same(world, s.ClustersAt(0)); // cached per level
    }

    [Fact]
    public async Task Removing_and_setting_locations_updates_the_catalog_but_never_the_original_files()
    {
        var before = Directory.GetFiles(_dir).ToDictionary(f => f, Fixtures.Sha256);

        await Places.RemoveLocationAsync([_ids["madrid_1.jpg"], _ids["madrid_2.jpg"]], CancellationToken.None);
        var s = await Load();
        Assert.Equal(["fiji.jpg", "paris.jpg", "samoa.jpg"], Names(s));
        Assert.Equal(4, s.WithoutLocationCount);

        await Places.SetLocationAsync([_ids["nogps_1.jpg"]], new GeoLocation(41.3874, 2.1686), CancellationToken.None);
        var asset = await _host.Assets.GetAsync(_ids["nogps_1.jpg"], CancellationToken.None);
        Assert.Equal(41.3874, asset!.Location!.Value.Latitude, 6);
        Assert.Contains("nogps_1.jpg", Names(await Load()));

        await Places.RemoveLocationAsync([], CancellationToken.None); // no-op, no exception
        Assert.Equal(before, Directory.GetFiles(_dir).ToDictionary(f => f, Fixtures.Sha256));
    }

    [Fact]
    public async Task Filters_round_trip_through_the_settings_file()
    {
        var store = _host.Get<ISettingsStore>();
        var filter = new PlacesFilter { From = new DateTime(2023, 1, 1), To = new DateTime(2023, 12, 31), MediaType = MediaType.Photo, FavoritesOnly = true, AlbumId = 7 };
        var settings = store.Load();
        store.Save(settings with { Places = filter.ApplyTo(settings.Places) with { ViewMode = PlacesViewMode.List } });

        var reloaded = new PhotosWindows.Infrastructure.Settings.JsonSettingsStore(_host.Get<PhotosWindows.Application.Abstractions.IAppPaths>(),
            Microsoft.Extensions.Logging.Abstractions.NullLogger<PhotosWindows.Infrastructure.Settings.JsonSettingsStore>.Instance).Load();
        Assert.Equal(PlacesViewMode.List, reloaded.Places.ViewMode);
        Assert.Equal(filter, PlacesFilter.FromPreferences(reloaded.Places));
        Assert.True(new PlacesFilter().IsEmpty);
        Assert.False(filter.IsEmpty);
    }

    [Fact]
    public async Task Zero_zero_placeholder_and_pole_limits_are_handled()
    {
        var dir = _host.SourceDir("limits");
        Fixtures.Jpeg(Path.Combine(dir, "null_island.jpg"), new Fixtures.PhotoSpec { Seed = 900, Location = new GeoLocation(0, 0) });
        Fixtures.Jpeg(Path.Combine(dir, "north.jpg"), new Fixtures.PhotoSpec { Seed = 901, Location = new GeoLocation(89.5, 180) });
        Fixtures.Jpeg(Path.Combine(dir, "south.jpg"), new Fixtures.PhotoSpec { Seed = 902, Location = new GeoLocation(-89.5, -180) });
        await _host.ImportAsync(dir, ImportMode.AddInPlace);
        var ids = (await _host.AllAsync()).ToDictionary(a => a.FileName, a => a.Id);

        var s = await Load();
        var located = s.Points.Select(p => p.AssetId).ToHashSet();
        Assert.DoesNotContain(ids["null_island.jpg"], located); // (0,0) is a device placeholder, not a place
        Assert.Contains(ids["north.jpg"], located);
        Assert.Contains(ids["south.jpg"], located);
        Assert.Equal(3, s.WithoutLocationCount);
        // Every point lands in exactly one cluster even at the projection's latitude limit and at ±180°.
        Assert.Equal(s.Points.Count, s.ClustersAt(3).Sum(c => c.Count));
    }

    [Fact]
    public void Coordinates_are_formatted_without_geocoding()
    {
        Assert.Equal("40.42° N, 3.70° W", PlacesService.FormatCoordinate(40.4168, -3.7038));
        Assert.Equal("17.71° S, 178.07° E", PlacesService.FormatCoordinate(-17.7134, 178.065));
    }
}

/// <summary>Measured (not asserted against a target): loading and clustering many located assets.</summary>
public sealed class PlacesScaleTests
{
    [Fact]
    public void Clustering_100k_points_is_fast_enough_for_interactive_zoom()
    {
        var rnd = new Random(1);
        var points = Enumerable.Range(1, 100_000)
            .Select(i => new GeoPoint(i, rnd.NextDouble() * 140 - 60, rnd.NextDouble() * 360 - 180, new DateTime(2020, 1, 1).AddMinutes(i)))
            .ToArray();
        var snapshot = new PlacesSnapshot(new PlacesFilter(), points, 0);
        foreach (var level in new[] { 2, 6, 10 })
        {
            var sw = Stopwatch.StartNew();
            var clusters = PlaceClustering.Cluster(points, level);
            sw.Stop();
            Console.WriteLine($"[perf] cluster 100k points level {level}: {sw.Elapsed.TotalMilliseconds:0} ms, {clusters.Count} clusters");
            Assert.Equal(100_000, clusters.Sum(c => c.Count));
        }

        Assert.NotNull(snapshot.Bounds);
    }
}
