using PhotosWindows.Application.Import;
using PhotosWindows.Domain;
using PhotosWindows.Integration.Tests.Support;

namespace PhotosWindows.Integration.Tests;

/// <summary>Area (bounding box) queries, including boxes that cross the antimeridian (MinLon &gt; MaxLon).</summary>
public sealed class GeoAreaTests : IAsyncLifetime
{
    private TestHost _host = null!;

    /// <summary>name → (lat, lon, taken); null location = photo without GPS.</summary>
    private static readonly (string Name, double? Lat, double? Lon, DateTime Taken, bool Video)[] Points =
    [
        ("madrid.jpg", 40.4168, -3.7038, new DateTime(2024, 5, 2, 12, 0, 0), false),
        ("fiji_east.jpg", -17.7134, 178.065, new DateTime(2023, 8, 1, 9, 0, 0), false),
        ("fiji_plus179.jpg", -16.5, 179.9, new DateTime(2023, 8, 2, 9, 0, 0), false),
        ("samoa_minus179.jpg", -14.0, -179.5, new DateTime(2023, 8, 3, 9, 0, 0), false),
        ("tonga_minus175.jpg", -21.13, -175.2, new DateTime(2022, 1, 5, 9, 0, 0), false),
        ("edge_lon_170.jpg", -10.0, 170.0, new DateTime(2021, 3, 1, 9, 0, 0), false),
        ("edge_lat_minus30.jpg", -30.0, 175.0, new DateTime(2021, 3, 2, 9, 0, 0), false),
        ("north_pole_area.jpg", 89.9, 10.0, new DateTime(2020, 6, 1, 9, 0, 0), false),
        ("no_gps.jpg", null, null, new DateTime(2023, 8, 2, 10, 0, 0), false),
    ];

    public async Task InitializeAsync()
    {
        _host = new TestHost();
        await _host.CreateLibraryAsync();
        var dir = _host.SourceDir("geo");
        var seed = 1;
        foreach (var p in Points)
        {
            Fixtures.Jpeg(Path.Combine(dir, p.Name), new Fixtures.PhotoSpec
            {
                Seed = seed++,
                Taken = p.Taken,
                Location = p.Lat is { } lat ? new GeoLocation(lat, p.Lon!.Value) : null,
            });
        }

        await _host.ImportAsync(dir, ImportMode.AddInPlace);
    }

    public async Task DisposeAsync() => await _host.DisposeAsync();

    private async Task<string[]> Names(AssetQuery query) =>
        (await _host.Assets.QueryAsync(query with { Sort = AssetSort.FileNameAscending }, CancellationToken.None)).Select(a => a.FileName).ToArray();

    [Fact]
    public async Task Normal_box_returns_the_points_inside()
    {
        Assert.Equal(["madrid.jpg"], await Names(new AssetQuery { Area = (40.0, -4.0, 41.0, -3.0) }));
    }

    [Fact]
    public async Task Box_crossing_the_antimeridian_returns_both_sides()
    {
        // From 170°E eastwards across 180° to 175°W.
        var found = await Names(new AssetQuery { Area = (-25.0, 170.0, -10.0, -175.0) });
        Assert.Equal(["edge_lon_170.jpg", "fiji_east.jpg", "fiji_plus179.jpg", "samoa_minus179.jpg", "tonga_minus175.jpg"], found);
    }

    [Fact]
    public async Task Narrow_box_around_the_antimeridian_contains_plus_and_minus_179()
    {
        Assert.Equal(["fiji_plus179.jpg", "samoa_minus179.jpg"], await Names(new AssetQuery { Area = (-20.0, 179.0, -10.0, -179.0) }));
    }

    [Fact]
    public async Task Points_exactly_on_the_edges_are_included()
    {
        Assert.Equal(["edge_lat_minus30.jpg", "edge_lon_170.jpg"], await Names(new AssetQuery { Area = (-30.0, 170.0, -10.0, 175.0) }));
    }

    [Fact]
    public async Task Inverted_latitudes_are_treated_as_the_same_box()
    {
        Assert.Equal(await Names(new AssetQuery { Area = (40.0, -4.0, 41.0, -3.0) }), await Names(new AssetQuery { Area = (41.0, -4.0, 40.0, -3.0) }));
    }

    [Fact]
    public async Task Photos_without_gps_never_match_an_area_and_empty_areas_return_nothing()
    {
        Assert.DoesNotContain("no_gps.jpg", await Names(new AssetQuery { Area = (-90, -180, 90, 180) }));
        Assert.Equal(8, (await Names(new AssetQuery { Area = (-90, -180, 90, 180) })).Length);
        Assert.Empty(await Names(new AssetQuery { Area = (50.0, 50.0, 51.0, 51.0) }));
    }

    [Fact]
    public async Task Area_combines_with_dates_and_type_and_keeps_order_and_limit()
    {
        var q = new AssetQuery { Area = (-25.0, 170.0, -10.0, -175.0), From = new DateTime(2023, 1, 1), To = new DateTime(2023, 12, 31, 23, 59, 59), MediaType = MediaType.Photo };
        Assert.Equal(["fiji_east.jpg", "fiji_plus179.jpg", "samoa_minus179.jpg"], await Names(q));
        var newest = await _host.Assets.QueryAsync(q with { Sort = AssetSort.CaptureDateDescending, Limit = 2 }, CancellationToken.None);
        Assert.Equal(["samoa_minus179.jpg", "fiji_plus179.jpg"], newest.Select(a => a.FileName));
        Assert.Equal(3, await _host.Assets.CountAsync(q, CancellationToken.None));
    }

    [Fact]
    public void Bounds_contain_and_normalize()
    {
        var crossing = GeoBounds.From(-25, 170, -10, -175);
        Assert.True(crossing.CrossesAntimeridian);
        Assert.True(crossing.Contains(new GeoLocation(-15, 180)));
        Assert.True(crossing.Contains(new GeoLocation(-15, -180)));
        Assert.True(crossing.Contains(new GeoLocation(-25, 170)));
        Assert.False(crossing.Contains(new GeoLocation(-15, 0)));
        Assert.False(crossing.Contains(new GeoLocation(-26, 175)));
        var swapped = GeoBounds.From(41, -4, 40, -3);
        Assert.Equal((40d, 41d), (swapped.MinLat, swapped.MaxLat));
        Assert.Throws<ArgumentOutOfRangeException>(() => GeoBounds.From(-91, 0, 0, 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => GeoBounds.From(0, 0, 0, 181));
        Assert.Throws<ArgumentOutOfRangeException>(() => GeoBounds.From(double.NaN, 0, 0, 0));
    }
}
