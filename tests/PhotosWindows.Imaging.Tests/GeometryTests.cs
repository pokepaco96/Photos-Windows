namespace PhotosWindows.Imaging.Tests;

public class GeometryTests
{
    private static (double, double) Round((double X, double Y) p) => (Math.Round(p.X, 9), Math.Round(p.Y, 9));

    [Theory]
    // (orientation, raw top-left pixel corner maps to upright corner)
    [InlineData(1, 0, 0)]
    [InlineData(2, 40, 0)]
    [InlineData(3, 40, 30)]
    [InlineData(4, 0, 30)]
    [InlineData(5, 0, 0)]
    [InlineData(6, 30, 0)]
    [InlineData(7, 30, 40)]
    [InlineData(8, 0, 40)]
    public void Exif_orientation_maps_raw_origin_to_expected_corner(int orientation, double ex, double ey)
    {
        var (t, w, h) = Orientation.FromExif(orientation, 40, 30);
        Assert.Equal((ex, ey), Round(t.Map(0, 0)));
        Assert.Equal(orientation >= 5 ? (30, 40) : (40, 30), (w, h));
    }

    [Fact]
    public void Exif_transforms_keep_the_image_inside_its_bounds()
    {
        for (var o = 1; o <= 8; o++)
        {
            var (t, w, h) = Orientation.FromExif(o, 40, 30);
            foreach (var (x, y) in new[] { (0.0, 0.0), (40.0, 0.0), (0.0, 30.0), (40.0, 30.0) })
            {
                var (mx, my) = t.Map(x, y);
                Assert.InRange(Math.Round(mx, 9), 0, w);
                Assert.InRange(Math.Round(my, 9), 0, h);
            }
        }
    }

    [Fact]
    public void Four_quarter_turns_are_identity()
    {
        var (t, w, h) = Orientation.FromRecipe(4, false, false, 40, 30);
        Assert.Equal((40, 30), (w, h));
        Assert.Equal((5.0, 7.0), Round(t.Map(5, 7)));
    }

    [Fact]
    public void Straighten_cover_scale_is_one_without_rotation_and_grows_with_angle()
    {
        Assert.Equal(1, Orientation.StraightenCoverScale(0, 400, 300));
        var s5 = Orientation.StraightenCoverScale(5, 400, 300);
        var s15 = Orientation.StraightenCoverScale(-15, 400, 300);
        Assert.True(s5 > 1);
        Assert.True(s15 > s5);
    }

    [Fact]
    public void Affine_composition_applies_first_then_second()
    {
        var t = Affine.Translate(10, 0).After(Affine.Scale(2));
        Assert.Equal((12.0, 2.0), Round(t.Map(1, 1)));
    }
}
