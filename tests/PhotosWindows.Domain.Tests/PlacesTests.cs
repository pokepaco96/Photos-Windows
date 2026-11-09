using PhotosWindows.Domain;

namespace PhotosWindows.Domain.Tests;

public sealed class WebMercatorTests
{
    [Theory]
    [InlineData(0, 0, 0.5, 0.5)]
    [InlineData(0, -180, 0, 0.5)]
    [InlineData(0, 180, 1, 0.5)]
    [InlineData(85.05112878, 0, 0.5, 0)]
    [InlineData(-85.05112878, 0, 0.5, 1)]
    public void Projects_known_points(double lat, double lon, double x, double y)
    {
        var (px, py) = WebMercator.ToWorld(lat, lon);
        Assert.Equal(x, px, 6);
        Assert.Equal(y, py, 6);
    }

    [Theory]
    [InlineData(40.4168, -3.7038)]
    [InlineData(-33.8688, 151.2093)]
    [InlineData(-16.5, 179.9)]
    [InlineData(64.1466, -21.9426)]
    public void Round_trips(double lat, double lon)
    {
        var (x, y) = WebMercator.ToWorld(lat, lon);
        var (lat2, lon2) = WebMercator.ToGeo(x, y);
        Assert.Equal(lat, lat2, 6);
        Assert.Equal(lon, lon2, 6);
    }

    [Fact]
    public void Poles_are_clamped_not_infinite()
    {
        Assert.Equal(0, WebMercator.ToWorld(90, 0).Y);
        Assert.Equal(1, WebMercator.ToWorld(-90, 0).Y);
    }

    [Theory]
    [InlineData(190, -170)]
    [InlineData(-190, 170)]
    [InlineData(540, -180)] // same meridian; out-of-range input maps into [-180, 180)
    [InlineData(180, 180)]
    [InlineData(-180, -180)]
    public void Wraps_longitudes(double input, double expected) => Assert.Equal(expected, WebMercator.WrapLongitude(input), 9);
}

public sealed class MapViewTests
{
    [Fact]
    public void World_view_shows_the_whole_world()
    {
        var view = MapView.World(1024, 768);
        var b = view.VisibleBounds;
        Assert.Equal(-180, b.MinLon, 6);
        Assert.Equal(180, b.MaxLon, 6);
    }

    [Fact]
    public void Panning_across_the_antimeridian_is_continuous_and_bounds_cross_it()
    {
        var fiji = MapView.Fit(GeoBounds.From(-19, 177, -16, 179), 800, 600);
        var view = fiji.PanBy(-400, 0); // move the view east by half its width
        Assert.InRange(view.CenterX, 0, 1);
        var b = view.VisibleBounds;
        Assert.True(b.CrossesAntimeridian, $"{b}");
        var (x, _) = view.Project(-17, -179.5);
        Assert.InRange(x, 0, 800);
    }

    [Fact]
    public void Zoom_keeps_the_point_under_the_cursor_fixed()
    {
        var view = new MapView(0.3, 0.4, 3, 800, 600);
        var before = view.ToGeo(200, 150);
        var zoomed = view.ZoomAt(1.5, 200, 150);
        var after = zoomed.ToGeo(200, 150);
        Assert.Equal(before.Latitude, after.Latitude, 6);
        Assert.Equal(before.Longitude, after.Longitude, 6);
        Assert.Equal(4.5, zoomed.Zoom);
    }

    [Fact]
    public void Zoom_is_clamped_and_invalid_input_is_sanitised()
    {
        Assert.Equal(MapView.MaxZoom, new MapView(0.5, 0.5, 99, 10, 10).Zoom);
        Assert.Equal(MapView.MinZoom, new MapView(0.5, 0.5, -3, 10, 10).Zoom);
        var bad = new MapView(double.NaN, double.PositiveInfinity, double.NaN, 0, -5);
        Assert.Equal((0.5, 0.5, 0d, 1, 1), (bad.CenterX, bad.CenterY, bad.Zoom, bad.Width, bad.Height));
    }

    [Fact]
    public void Fit_contains_every_corner_including_across_the_antimeridian()
    {
        foreach (var bounds in new[] { GeoBounds.From(40, -4, 41, -3), GeoBounds.From(-25, 170, -10, -175), GeoBounds.From(-60, -170, 70, 170) })
        {
            var view = MapView.Fit(bounds, 900, 600);
            foreach (var (lat, lon) in new[] { (bounds.MinLat, bounds.MinLon), (bounds.MaxLat, bounds.MaxLon), (bounds.MinLat, bounds.MaxLon), (bounds.MaxLat, bounds.MinLon) })
            {
                var (x, y) = view.Project(lat, lon);
                Assert.InRange(x, -0.5, 900.5);
                Assert.InRange(y, -0.5, 600.5);
            }
        }
    }

    [Fact]
    public void Interactive_zoom_stops_at_the_basemap_detail_limit_and_scaling_keeps_the_same_area()
    {
        var view = new MapView(0.5, 0.5, 9.5, 800, 600).ZoomAt(3, 400, 300);
        Assert.Equal(MapView.MaxInteractiveZoom, view.Zoom);
        var hi = view.Scaled(1.5);
        Assert.Equal((1200, 900), (hi.Width, hi.Height));
        Assert.Equal(view.VisibleBounds.MinLon, hi.VisibleBounds.MinLon, 9);
        Assert.Equal(view.VisibleBounds.MaxLat, hi.VisibleBounds.MaxLat, 9);
    }

    [Fact]
    public void Fit_of_a_single_point_uses_the_max_zoom_cap()
    {
        var view = MapView.Fit(GeoBounds.From(40, -3, 40, -3), 800, 600, maxZoom: 8);
        Assert.Equal(8, view.Zoom);
    }
}

public sealed class GeoBoundsEnclosingTests
{
    [Fact]
    public void Empty_is_null() => Assert.Null(GeoBounds.Enclosing([]));

    [Fact]
    public void Points_on_both_sides_of_the_antimeridian_give_a_narrow_crossing_box()
    {
        var b = GeoBounds.Enclosing([(-16.5, 179.9), (-14, -179.5), (-17.7, 178.1)])!.Value;
        Assert.True(b.CrossesAntimeridian);
        Assert.Equal(178.1, b.MinLon, 9);
        Assert.Equal(-179.5, b.MaxLon, 9);
        Assert.Equal((-17.7, -14), (b.MinLat, b.MaxLat));
    }

    [Fact]
    public void Ordinary_points_give_an_ordinary_box()
    {
        var b = GeoBounds.Enclosing([(40.4, -3.7), (41.4, 2.2), (37.4, -6)])!.Value;
        Assert.False(b.CrossesAntimeridian);
        Assert.Equal((-6d, 2.2), (b.MinLon, b.MaxLon));
    }
}

public sealed class PlaceClusteringTests
{
    private static readonly DateTime T0 = new(2024, 1, 1);

    private static GeoPoint P(long id, double lat, double lon, int day = 0) => new(id, lat, lon, T0.AddDays(day));

    [Fact]
    public void Nearby_points_merge_and_far_points_do_not()
    {
        var points = new[] { P(1, 40.4168, -3.7038), P(2, 40.4170, -3.7040, 3), P(3, 48.8566, 2.3522) };
        var clusters = PlaceClustering.Cluster(points, level: 6);
        Assert.Equal(2, clusters.Count);
        var madrid = clusters[0];
        Assert.Equal(2, madrid.Count);
        Assert.Equal(2, madrid.CoverAssetId); // the most recent photo is the cover
        Assert.Equal((T0, T0.AddDays(3)), (madrid.First, madrid.Last));
        Assert.Equal([1L, 2L], madrid.AssetIds.Order());
    }

    [Fact]
    public void Every_point_is_in_exactly_one_cluster_at_every_level()
    {
        var rnd = new Random(42);
        var points = Enumerable.Range(1, 5000).Select(i => P(i, rnd.NextDouble() * 170 - 85, rnd.NextDouble() * 360 - 180, i % 365)).ToArray();
        for (var level = 0; level <= 10; level++)
        {
            var clusters = PlaceClustering.Cluster(points, level);
            var ids = clusters.SelectMany(c => c.AssetIds).ToArray();
            Assert.Equal(points.Length, ids.Length);
            Assert.Equal(points.Length, ids.Distinct().Count());
            Assert.All(clusters, c => Assert.All(c.AssetIds, id =>
            {
                var p = points[id - 1];
                Assert.True(c.Bounds.Contains(new GeoLocation(p.Latitude, p.Longitude)), $"{c.Key} {c.Bounds} misses {p}");
            }));
        }

        Assert.Single(PlaceClustering.Cluster(points, 0).Where(c => c.Count > 0).Take(1));
        Assert.True(PlaceClustering.Cluster(points, 10).Count > PlaceClustering.Cluster(points, 3).Count);
    }

    [Fact]
    public void Output_is_deterministic_regardless_of_input_order()
    {
        var rnd = new Random(7);
        var points = Enumerable.Range(1, 2000).Select(i => P(i, rnd.NextDouble() * 100 - 50, rnd.NextDouble() * 360 - 180)).ToArray();
        var a = PlaceClustering.Cluster(points, 4).Select(c => (c.Key, c.Count, c.CoverAssetId)).ToArray();
        var b = PlaceClustering.Cluster(points.Reverse().ToArray(), 4).Select(c => (c.Key, c.Count, c.CoverAssetId)).ToArray();
        Assert.Equal(a, b);
    }

    [Fact]
    public void Cluster_position_is_inside_its_members_and_never_wraps_to_the_wrong_side()
    {
        // Two points in the same level-0 cell near 180°: the centroid must stay near ±180, not jump to 0°.
        var clusters = PlaceClustering.Cluster([P(1, -16.5, 179.9), P(2, -17.7, 178.1)], level: 0);
        var c = Assert.Single(clusters);
        Assert.InRange(c.Longitude, 178, 180);
        Assert.InRange(c.Latitude, -17.8, -16.4);
    }

    [Fact]
    public void Level_follows_zoom_floor()
    {
        Assert.Equal(3, PlaceClustering.LevelFor(3.99));
        Assert.Equal(4, PlaceClustering.LevelFor(4));
        Assert.Equal(0, PlaceClustering.LevelFor(-2));
        Assert.Equal((int)MapView.MaxZoom, PlaceClustering.LevelFor(50));
    }
}
