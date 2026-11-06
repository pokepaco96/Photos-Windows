using PhotosWindows.Application.Abstractions;

namespace PhotosWindows.Imaging;

/// <summary>
/// Difference hash (dHash): the image is reduced to 9×8 grey samples and each bit records whether a sample
/// is brighter than its right neighbour. Robust to scaling, mild compression and colour changes; it is a
/// similarity hint, never proof of duplication.
/// </summary>
public sealed class PerceptualHasher : IPerceptualHasher
{
    public ulong Compute(RasterImage image)
    {
        ArgumentNullException.ThrowIfNull(image);
        Span<double> grid = stackalloc double[9 * 8];
        var w = image.Width;
        var h = image.Height;
        var px = image.Pixels;

        // Area-average into a 9×8 grid.
        for (var gy = 0; gy < 8; gy++)
        {
            var y0 = gy * h / 8;
            var y1 = Math.Max(y0 + 1, (gy + 1) * h / 8);
            for (var gx = 0; gx < 9; gx++)
            {
                var x0 = gx * w / 9;
                var x1 = Math.Max(x0 + 1, (gx + 1) * w / 9);
                double sum = 0;
                var count = 0;
                for (var y = y0; y < y1 && y < h; y++)
                {
                    for (var x = x0; x < x1 && x < w; x++)
                    {
                        var i = (y * w + x) * 4;
                        sum += 0.0722 * px[i] + 0.7152 * px[i + 1] + 0.2126 * px[i + 2];
                        count++;
                    }
                }

                grid[gy * 9 + gx] = count == 0 ? 0 : sum / count;
            }
        }

        ulong hash = 0;
        var bit = 0;
        for (var gy = 0; gy < 8; gy++)
        {
            for (var gx = 0; gx < 8; gx++)
            {
                if (grid[gy * 9 + gx] > grid[gy * 9 + gx + 1])
                {
                    hash |= 1UL << bit;
                }

                bit++;
            }
        }

        return hash;
    }

    public static int HammingDistance(ulong a, ulong b) => System.Numerics.BitOperations.PopCount(a ^ b);
}
