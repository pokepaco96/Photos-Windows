namespace PhotosWindows.Imaging;

/// <summary>
/// 2D affine transform: x' = M11·x + M12·y + Dx ; y' = M21·x + M22·y + Dy.
/// Kept independent from SkiaSharp so geometry is unit-testable and composed exactly once
/// (a single resampling pass preserves quality).
/// </summary>
public readonly record struct Affine(double M11, double M12, double Dx, double M21, double M22, double Dy)
{
    public static Affine Identity { get; } = new(1, 0, 0, 0, 1, 0);

    public static Affine Translate(double x, double y) => new(1, 0, x, 0, 1, y);

    public static Affine Scale(double s) => new(s, 0, 0, 0, s, 0);

    public static Affine RotateDegrees(double degrees, double cx, double cy)
    {
        var r = degrees * Math.PI / 180.0;
        var c = Math.Cos(r);
        var s = Math.Sin(r);
        // Rotate around (cx, cy).
        return new Affine(c, -s, cx - c * cx + s * cy, s, c, cy - s * cx - c * cy);
    }

    /// <summary>Applies <paramref name="first"/> and then this transform.</summary>
    public Affine After(Affine first) => new(
        M11 * first.M11 + M12 * first.M21,
        M11 * first.M12 + M12 * first.M22,
        M11 * first.Dx + M12 * first.Dy + Dx,
        M21 * first.M11 + M22 * first.M21,
        M21 * first.M12 + M22 * first.M22,
        M21 * first.Dx + M22 * first.Dy + Dy);

    public (double X, double Y) Map(double x, double y) => (M11 * x + M12 * y + Dx, M21 * x + M22 * y + Dy);
}

/// <summary>Builds the transforms for EXIF orientation, quarter turns and flips.</summary>
public static class Orientation
{
    /// <summary>Clockwise quarter turn of an image with size (w, h); result size is (h, w).</summary>
    public static Affine RotateClockwise(double w, double h) => new(0, -1, h, 1, 0, 0);

    public static Affine FlipHorizontal(double w) => new(-1, 0, w, 0, 1, 0);

    public static Affine FlipVertical(double h) => new(1, 0, 0, 0, -1, h);

    /// <summary>
    /// Transform from the stored (raw) pixel grid to the upright image for an EXIF orientation 1..8.
    /// Returns the upright size too.
    /// </summary>
    public static (Affine Transform, int Width, int Height) FromExif(int orientation, int w, int h) => orientation switch
    {
        2 => (FlipHorizontal(w), w, h),
        3 => (new Affine(-1, 0, w, 0, -1, h), w, h), // 180°
        4 => (FlipVertical(h), w, h),
        5 => (new Affine(0, 1, 0, 1, 0, 0), h, w), // transpose
        6 => (RotateClockwise(w, h), h, w),
        7 => (new Affine(0, -1, h, -1, 0, w), h, w), // transverse
        8 => (new Affine(0, 1, 0, -1, 0, w), h, w), // 90° counter-clockwise
        _ => (Affine.Identity, w, h),
    };

    /// <summary>User quarter turns (clockwise) then flips, applied to an upright image of size (w, h).</summary>
    public static (Affine Transform, int Width, int Height) FromRecipe(int quarterTurns, bool flipH, bool flipV, int w, int h)
    {
        var t = Affine.Identity;
        int cw = w, ch = h;
        for (var i = 0; i < ((quarterTurns % 4) + 4) % 4; i++)
        {
            t = RotateClockwise(cw, ch).After(t);
            (cw, ch) = (ch, cw);
        }

        if (flipH) t = FlipHorizontal(cw).After(t);
        if (flipV) t = FlipVertical(ch).After(t);
        return (t, cw, ch);
    }

    /// <summary>
    /// Zoom factor needed so a (w × h) frame stays fully covered after rotating the image by <paramref name="degrees"/>
    /// around its centre (no empty corners after straightening).
    /// </summary>
    public static double StraightenCoverScale(double degrees, double w, double h)
    {
        var r = Math.Abs(degrees) * Math.PI / 180.0;
        if (r < 1e-9) return 1;
        var c = Math.Cos(r);
        var s = Math.Sin(r);
        return Math.Max((w * c + h * s) / w, (w * s + h * c) / h);
    }
}
