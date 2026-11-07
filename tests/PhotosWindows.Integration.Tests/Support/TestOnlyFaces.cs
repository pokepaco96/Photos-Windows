using PhotosWindows.Application.Abstractions;
using PhotosWindows.Application.Export;
using PhotosWindows.Domain;
using SkiaSharp;

namespace PhotosWindows.Integration.Tests.Support;

/// <summary>A synthetic "face": a magenta frame filled with the colour of an identity, in display coordinates (0..1).</summary>
public sealed record SyntheticFace(int Identity, float X, float Y, float Size);

/// <summary>
/// Synthetic photos for People tests. They contain no real faces: the TEST-ONLY detector below recognises the magenta
/// frames. Real-model inference is tested separately (see <c>FaceInferenceTests</c>).
/// </summary>
public static class FaceFixtures
{
    /// <summary>Distinct, saturated colours: one per identity.</summary>
    public static readonly SKColor[] IdentityColors =
    [
        new(220, 40, 40), new(40, 200, 40), new(40, 60, 220), new(230, 210, 30), new(30, 210, 220), new(240, 140, 20),
    ];

    /// <param name="orientation">1, or 6 (stored rotated: a viewer must turn it 90° clockwise to show it as described).</param>
    public static string Photo(string path, IReadOnlyList<SyntheticFace> faces, int width = 400, int height = 300, int orientation = 1, int seed = 0, DateTime? taken = null)
    {
        using var display = new SKBitmap(new SKImageInfo(width, height, SKColorType.Bgra8888, SKAlphaType.Opaque));
        using (var canvas = new SKCanvas(display))
        {
            var rng = new Random(seed);
            using var bg = new SKPaint();
            var g = (byte)rng.Next(90, 150);
            bg.Shader = SKShader.CreateLinearGradient(new SKPoint(0, 0), new SKPoint(width, height), [new SKColor(g, g, g), new SKColor((byte)(g + 60), (byte)(g + 50), (byte)(g + 40))], SKShaderTileMode.Clamp);
            canvas.DrawRect(0, 0, width, height, bg);
            foreach (var face in faces)
            {
                var side = face.Size * Math.Min(width, height);
                var rect = SKRect.Create(face.X * width, face.Y * height, side, side);
                using var frame = new SKPaint { Color = new SKColor(255, 0, 255) };
                canvas.DrawRect(rect, frame);
                var inset = Math.Max(3, side * 0.15f);
                using var fill = new SKPaint { Color = IdentityColors[face.Identity % IdentityColors.Length] };
                canvas.DrawRect(SKRect.Create(rect.Left + inset, rect.Top + inset, rect.Width - 2 * inset, rect.Height - 2 * inset), fill);
            }
        }

        SKBitmap stored = display;
        if (orientation == 6)
        {
            // Stored pixels are the display image turned 90° counter-clockwise.
            stored = new SKBitmap(new SKImageInfo(height, width, SKColorType.Bgra8888, SKAlphaType.Opaque));
            using var c = new SKCanvas(stored);
            c.Translate(0, width);
            c.RotateDegrees(-90);
            using var source = SKImage.FromBitmap(display);
            c.DrawImage(source, 0, 0, new SKSamplingOptions(SKFilterMode.Nearest));
        }

        using var image = SKImage.FromBitmap(stored);
        using var data = image.Encode(SKEncodedImageFormat.Jpeg, 100);
        var bytes = data.ToArray();
        var metadata = new ExportMetadata(new CaptureDate(taken ?? new DateTime(2024, 5, 1, 12, 0, 0), null, DateSource.ExifOriginal),
            new CameraInfo("QA", "Synthetic", null, null, null, null, null), null, stored.Width, stored.Height, null, null, [], 0);
        bytes = JpegMetadataWriter.Inject(bytes, metadata, includeLocation: false, includeDescriptive: true, orientation: orientation);
        if (!ReferenceEquals(stored, display)) stored.Dispose();
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, bytes);
        return path;
    }
}

/// <summary>
/// TEST-ONLY detector: finds magenta frames drawn by <see cref="FaceFixtures"/>. It is never registered by the app;
/// it exists so People logic (grouping, corrections, deletion, resume) can be tested deterministically.
/// </summary>
public sealed class TestOnlyFaceDetector : IFaceDetector
{
    public string ModelId { get; set; } = "test-only-detector-1";

    public bool IsReady { get; set; } = true;

    /// <summary>Delay per photo, to test cancellation and resume.</summary>
    public TimeSpan Delay { get; set; }

    public int Calls;

    public async Task<IReadOnlyList<FaceDetection>> DetectAsync(RasterImage image, CancellationToken cancellationToken)
    {
        Interlocked.Increment(ref Calls);
        if (Delay > TimeSpan.Zero) await Task.Delay(Delay, cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        int w = image.Width, h = image.Height;
        var mask = new bool[w * h];
        for (var i = 0; i < w * h; i++)
        {
            var b = image.Pixels[i * 4];
            var g = image.Pixels[i * 4 + 1];
            var r = image.Pixels[i * 4 + 2];
            mask[i] = r > 200 && g < 90 && b > 200;
        }

        var seen = new bool[w * h];
        var result = new List<FaceDetection>();
        var queue = new Queue<int>();
        for (var start = 0; start < w * h; start++)
        {
            if (!mask[start] || seen[start]) continue;
            int minX = w, minY = h, maxX = 0, maxY = 0, count = 0;
            queue.Enqueue(start);
            seen[start] = true;
            while (queue.Count > 0)
            {
                var p = queue.Dequeue();
                int x = p % w, y = p / w;
                count++;
                minX = Math.Min(minX, x); maxX = Math.Max(maxX, x); minY = Math.Min(minY, y); maxY = Math.Max(maxY, y);
                foreach (var n in new[] { p - 1, p + 1, p - w, p + w })
                {
                    if (n < 0 || n >= w * h || seen[n] || !mask[n]) continue;
                    if (Math.Abs(n % w - x) > 1) continue;
                    seen[n] = true;
                    queue.Enqueue(n);
                }
            }

            if (count < 40) continue; // JPEG noise
            var box = new FaceBox((float)minX / w, (float)minY / h, (float)(maxX - minX + 1) / w, (float)(maxY - minY + 1) / h);
            result.Add(new FaceDetection(box, 0.99f, [new(box.X + box.Width * 0.3f, box.Y + box.Height * 0.4f), new(box.X + box.Width * 0.7f, box.Y + box.Height * 0.4f),
                new(box.X + box.Width * 0.5f, box.Y + box.Height * 0.55f), new(box.X + box.Width * 0.35f, box.Y + box.Height * 0.75f), new(box.X + box.Width * 0.65f, box.Y + box.Height * 0.75f)]));
        }

        return result;
    }
}

/// <summary>TEST-ONLY embedder: the identity is the fill colour inside the frame (one dimension per palette colour).</summary>
public sealed class TestOnlyFaceEmbedder : IFaceEmbedder
{
    public string ModelId { get; set; } = "test-only-embedder-1";

    public bool IsReady { get; set; } = true;

    public int Dimensions => FaceFixtures.IdentityColors.Length + 2;

    public Task<float[]> EmbedAsync(RasterImage image, FaceDetection face, CancellationToken cancellationToken)
    {
        var cx = (int)((face.Box.X + face.Box.Width / 2) * image.Width);
        var cy = (int)((face.Box.Y + face.Box.Height / 2) * image.Height);
        var i = (Math.Clamp(cy, 0, image.Height - 1) * image.Width + Math.Clamp(cx, 0, image.Width - 1)) * 4;
        var color = new SKColor(image.Pixels[i + 2], image.Pixels[i + 1], image.Pixels[i]);
        var nearest = 0;
        var best = int.MaxValue;
        for (var k = 0; k < FaceFixtures.IdentityColors.Length; k++)
        {
            var p = FaceFixtures.IdentityColors[k];
            var d = Sq(p.Red - color.Red) + Sq(p.Green - color.Green) + Sq(p.Blue - color.Blue);
            if (d < best) { best = d; nearest = k; }
        }

        var vector = new float[Dimensions];
        vector[nearest] = 1f;
        // A small, position-dependent component: same identity ≈ 0.99 similarity, never exactly equal.
        vector[^2] = face.Box.X * 0.1f;
        vector[^1] = face.Box.Y * 0.1f;
        return Task.FromResult(vector);
    }

    private static int Sq(int v) => v * v;
}
