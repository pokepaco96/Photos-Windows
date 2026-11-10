using System.Diagnostics;
using PhotosWindows.Application.Places;
using PhotosWindows.Domain;
using PhotosWindows.Imaging.Maps;

namespace PhotosWindows.Imaging.Tests;

public sealed class MapRendererTests
{
    private static readonly Lazy<SkiaMapRenderer> Renderer = new(() => new SkiaMapRenderer());

    [Fact]
    public void Embedded_basemap_loads_and_matches_the_generator_manifest()
    {
        var map = WorldBasemap.LoadEmbedded();
        Assert.Equal(["land110", "land50", "lakes50", "borders50"], map.Layers.Select(l => l.Name));
        // Point counts recorded by tools/basemap/build_basemap.py in world.manifest.json.
        Assert.Equal([5133, 60669, 18614, 19764], map.Layers.Select(l => l.PointCount));
        Assert.All(map.Layers.SelectMany(l => l.Rings).SelectMany(r => r), v => Assert.InRange(v, 0f, 1f));
    }

    [Fact]
    public void Corrupt_or_tampered_map_is_rejected_and_the_renderer_degrades_to_water_only()
    {
        using var stream = typeof(WorldBasemap).Assembly.GetManifestResourceStream(WorldBasemap.ResourceName)!;
        var bytes = new byte[stream.Length];
        stream.ReadExactly(bytes);

        var tampered = (byte[])bytes.Clone();
        tampered[^10] ^= 0xFF;
        Assert.Throws<InvalidDataException>(() => WorldBasemap.Load(tampered, WorldBasemap.EmbeddedSha256));
        Assert.Throws<InvalidDataException>(() => WorldBasemap.Load(bytes[..2000]));
        Assert.Throws<InvalidDataException>(() => WorldBasemap.Load([1, 2, 3]));
        Assert.Throws<InvalidDataException>(() => WorldBasemap.Load([]));

        using var broken = new SkiaMapRenderer(() => WorldBasemap.Load(tampered, WorldBasemap.EmbeddedSha256));
        Assert.False(broken.IsAvailable);
        Assert.Contains("SHA-256", broken.UnavailableReason);
        var image = broken.Render(MapView.World(64, 64), MapTheme.Light);
        Assert.Equal(64 * 64 * 4, image.Pixels.Length);
    }

    [Fact]
    public void Missing_map_makes_the_renderer_unavailable_without_throwing()
    {
        using var missing = new SkiaMapRenderer(() => throw new InvalidDataException("The embedded world map is missing."));
        Assert.False(missing.IsAvailable);
        Assert.Contains("missing", missing.UnavailableReason);
        var image = missing.Render(MapView.World(32, 32), MapTheme.Dark);
        Assert.Equal(32 * 32 * 4, image.Pixels.Length);
    }

    [Theory]
    [InlineData(MapTheme.Light)]
    [InlineData(MapTheme.Dark)]
    public void Land_and_sea_are_where_they_should_be(MapTheme theme)
    {
        Assert.True(Renderer.Value.IsAvailable, Renderer.Value.UnavailableReason);
        var view = MapView.World(1024, 1024);
        var image = Renderer.Value.Render(view, theme);
        var water = Pixel(image, view, 0, -30);       // mid-Atlantic
        var water2 = Pixel(image, view, -10, -140);   // South Pacific
        var land = Pixel(image, view, 15, 20);        // Chad (Sahara)
        var land2 = Pixel(image, view, 60, 100);      // Siberia
        Assert.Equal(water, water2);
        Assert.Equal(land, land2);
        Assert.NotEqual(water, land);
        var lum = (int)(0.3 * land.R + 0.59 * land.G + 0.11 * land.B);
        Assert.True(theme == MapTheme.Dark ? lum < 80 : lum > 180, $"{theme} land luminance {lum}");
    }

    [Fact]
    public void Rendering_across_the_antimeridian_draws_both_sides()
    {
        // Centred on 180°: Fiji/NZ land must appear on the left half and Pacific water on the right.
        var view = MapView.Fit(GeoBounds.From(-48, 165, -10, -170), 800, 600);
        var image = Renderer.Value.Render(view, MapTheme.Light);
        var nz = Pixel(image, view, -43.5, 171.5);       // South Island, west of 180°
        var ocean = Pixel(image, view, -30, -175);        // east of 180°, open sea
        Assert.NotEqual(nz, ocean);
    }

    [Fact]
    public void Lakes_are_cut_out_of_land_at_regional_zoom()
    {
        var view = MapView.Fit(GeoBounds.From(41, -93, 49, -76), 900, 600); // Great Lakes
        Assert.True(view.Zoom >= 3);
        var image = Renderer.Value.Render(view, MapTheme.Light);
        var superior = Pixel(image, view, 47.7, -87.5);
        var land = Pixel(image, view, 44.5, -90);   // Wisconsin
        Assert.NotEqual(superior, land);
        Assert.Equal(Pixel(image, view, 42, -77.5), Pixel(image, view, 45.5, -91)); // Lake-free land is uniform
    }

    [Fact]
    public void Render_time_is_measured_for_typical_views()
    {
        var renderer = Renderer.Value;
        renderer.Render(MapView.World(256, 256), MapTheme.Light); // warm-up (path building)
        var views = new[] { MapView.World(1600, 1000), MapView.Fit(GeoBounds.From(35, -10, 44, 4), 1600, 1000) };
        foreach (var view in views)
        {
            var sw = Stopwatch.StartNew();
            const int n = 5;
            for (var i = 0; i < n; i++) renderer.Render(view, MapTheme.Light);
            var ms = sw.Elapsed.TotalMilliseconds / n;
            Console.WriteLine($"[perf] map render {view.Width}x{view.Height} zoom {view.Zoom:0.0}: {ms:0.0} ms/frame");
            Assert.True(ms < 2000, $"render took {ms} ms"); // generous ceiling: only catches pathological regressions
        }
    }

    private static (byte B, byte G, byte R) Pixel(PhotosWindows.Application.Abstractions.RasterImage image, MapView view, double lat, double lon)
    {
        var (x, y) = view.Project(lat, lon);
        var i = ((int)y * image.Width + (int)x) * 4;
        return (image.Pixels[i], image.Pixels[i + 1], image.Pixels[i + 2]);
    }
}
