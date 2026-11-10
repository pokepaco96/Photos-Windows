using PhotosWindows.Application.Abstractions;

namespace PhotosWindows.MachineLearning;

/// <summary>
/// Pixel preprocessing for the face models, in plain C# (no imaging dependency). Bilinear sampling uses pixel-centre
/// coordinates like OpenCV's INTER_LINEAR, so results match the reference implementation of the models.
/// Input images are BGRA 8-bit (see <see cref="RasterImage"/>), with EXIF orientation already applied.
/// </summary>
internal static class ImageTensors
{
    /// <summary>
    /// Scales the image so its longest side is <paramref name="longSide"/>, places it at the top-left of a
    /// <paramref name="canvas"/>×<paramref name="canvas"/> black square and returns it as planar BGR floats (0..255).
    /// </summary>
    public static (float[] Tensor, float Scale) Letterbox(RasterImage image, int longSide, int canvas)
    {
        var scale = (float)longSide / Math.Max(image.Width, image.Height);
        var w = Math.Min(canvas, (int)MathF.Round(image.Width * scale));
        var h = Math.Min(canvas, (int)MathF.Round(image.Height * scale));
        var plane = canvas * canvas;
        var tensor = new float[3 * plane];
        var sx = (float)image.Width / w;
        var sy = (float)image.Height / h;
        for (var y = 0; y < h; y++)
        {
            var srcY = (y + 0.5f) * sy - 0.5f;
            for (var x = 0; x < w; x++)
            {
                var srcX = (x + 0.5f) * sx - 0.5f;
                Sample(image, srcX, srcY, out var b, out var g, out var r);
                var i = y * canvas + x;
                tensor[i] = b;
                tensor[plane + i] = g;
                tensor[2 * plane + i] = r;
            }
        }

        return (tensor, scale);
    }

    /// <summary>
    /// Warps the face to a <paramref name="size"/>×<paramref name="size"/> crop with the affine transform
    /// <paramref name="m"/> (source → destination), returned as planar RGB floats (0..255).
    /// </summary>
    public static float[] WarpRgb(RasterImage image, double[] m, int size)
    {
        // Invert the 2×3 similarity transform to sample the source for each destination pixel.
        var det = m[0] * m[4] - m[1] * m[3];
        var a = m[4] / det;
        var b = -m[1] / det;
        var d = -m[3] / det;
        var e = m[0] / det;
        var c = -(a * m[2] + b * m[5]);
        var f = -(d * m[2] + e * m[5]);
        var plane = size * size;
        var tensor = new float[3 * plane];
        for (var y = 0; y < size; y++)
        {
            for (var x = 0; x < size; x++)
            {
                var srcX = (float)(a * x + b * y + c);
                var srcY = (float)(d * x + e * y + f);
                var i = y * size + x;
                if (srcX < -1 || srcY < -1 || srcX > image.Width || srcY > image.Height) continue; // black border
                Sample(image, srcX, srcY, out var bl, out var g, out var r);
                tensor[i] = r;
                tensor[plane + i] = g;
                tensor[2 * plane + i] = bl;
            }
        }

        return tensor;
    }

    /// <summary>
    /// Least-squares similarity transform (rotation, uniform scale, translation; no reflection) mapping
    /// <paramref name="source"/> onto <paramref name="target"/>: the 2-D Umeyama solution, written with complex numbers.
    /// Returns the 2×3 matrix [a -b tx; b a ty] as a row-major array of 6.
    /// </summary>
    public static double[] Similarity(IReadOnlyList<(double X, double Y)> source, IReadOnlyList<(double X, double Y)> target)
    {
        var n = source.Count;
        double msx = 0, msy = 0, mtx = 0, mty = 0;
        for (var i = 0; i < n; i++)
        {
            msx += source[i].X; msy += source[i].Y; mtx += target[i].X; mty += target[i].Y;
        }

        msx /= n; msy /= n; mtx /= n; mty /= n;
        double re = 0, im = 0, norm = 0;
        for (var i = 0; i < n; i++)
        {
            var sx = source[i].X - msx; var sy = source[i].Y - msy;
            var tx = target[i].X - mtx; var ty = target[i].Y - mty;
            // (t) * conj(s)
            re += tx * sx + ty * sy;
            im += ty * sx - tx * sy;
            norm += sx * sx + sy * sy;
        }

        var ar = re / norm;
        var ai = im / norm;
        return [ar, -ai, mtx - (ar * msx - ai * msy), ai, ar, mty - (ai * msx + ar * msy)];
    }

    private static void Sample(RasterImage image, float x, float y, out float b, out float g, out float r)
    {
        var x0 = (int)MathF.Floor(x);
        var y0 = (int)MathF.Floor(y);
        var fx = x - x0;
        var fy = y - y0;
        var x1 = Math.Clamp(x0 + 1, 0, image.Width - 1);
        var y1 = Math.Clamp(y0 + 1, 0, image.Height - 1);
        x0 = Math.Clamp(x0, 0, image.Width - 1);
        y0 = Math.Clamp(y0, 0, image.Height - 1);
        var p = image.Pixels;
        var w = image.Width;
        int i00 = (y0 * w + x0) * 4, i01 = (y0 * w + x1) * 4, i10 = (y1 * w + x0) * 4, i11 = (y1 * w + x1) * 4;
        float Lerp(int c) => (p[i00 + c] * (1 - fx) + p[i01 + c] * fx) * (1 - fy) + (p[i10 + c] * (1 - fx) + p[i11 + c] * fx) * fy;
        b = Lerp(0);
        g = Lerp(1);
        r = Lerp(2);
    }
}
