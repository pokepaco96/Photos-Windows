using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using PhotosWindows.Application.Export;
using PhotosWindows.Domain;
using SkiaSharp;

namespace PhotosWindows.UIAutomation.Tests;

/// <summary>
/// Reproducible synthetic dataset (fixed seeds, no personal photos). Everything is created under the run's work root.
/// <list type="bullet">
/// <item><c>copy/</c>: imported with "Copy to the library".</item>
/// <item><c>inplace/</c>: imported with "Add without moving"; contains duplicates, corrupt and disguised files.</item>
/// </list>
/// A manifest with the SHA-256 of every source file is written before the run so originals can be verified afterwards.
/// </summary>
public sealed class QaDataset
{
    public QaDataset(string root)
    {
        Root = root;
        CopyFolder = Path.Combine(root, "copy");
        InPlaceFolder = Path.Combine(root, "inplace");
    }

    public string Root { get; }
    public string CopyFolder { get; }
    public string InPlaceFolder { get; }
    public bool HasVideo { get; private set; }
    public string? VideoNote { get; private set; }
    public IReadOnlyDictionary<string, string> Hashes { get; private set; } = new Dictionary<string, string>();

    /// <summary>Files that must be imported (name → folder).</summary>
    public static readonly string[] CopyPhotos = ["IMG_0001.jpg", "IMG_0002.jpg", "IMG_0003.jpg", "GPS_Madrid.jpg", "Dibujo.png", "Fiesta ñandú #1 (copia).jpg", "RAW_0100.JPG"];

    public static readonly string[] InPlacePhotos = ["Playa 2021.jpg", "Nieve_2019.jpg"];

    public void Generate()
    {
        Directory.CreateDirectory(CopyFolder);
        Directory.CreateDirectory(InPlaceFolder);

        Jpeg(Path.Combine(CopyFolder, "IMG_0001.jpg"), 1, new DateTime(2023, 7, 14, 18, 30, 5), TimeSpan.FromHours(2), null, "Atardecer en la costa", ["beach", "sunset"]);
        Jpeg(Path.Combine(CopyFolder, "IMG_0002.jpg"), 2, new DateTime(2022, 12, 24, 21, 0, 0), TimeSpan.FromHours(1), null, null, ["family"]);
        Jpeg(Path.Combine(CopyFolder, "IMG_0003.jpg"), 3, new DateTime(2020, 3, 1, 9, 15, 0), null, null, null, []);
        Jpeg(Path.Combine(CopyFolder, "GPS_Madrid.jpg"), 4, new DateTime(2024, 5, 2, 12, 0, 0), TimeSpan.FromHours(2), new GeoLocation(40.4168, -3.7038, 650), "Plaza", ["city"]);
        Png(Path.Combine(CopyFolder, "Dibujo.png"), 5);
        Jpeg(Path.Combine(CopyFolder, "Fiesta ñandú #1 (copia).jpg"), 6, new DateTime(2021, 8, 3, 23, 59, 59), null, null, null, ["party"]);
        // RAW + JPEG pair (the RAW is a TIFF-structured stand-in: recognised by signature, not decodable).
        Jpeg(Path.Combine(CopyFolder, "RAW_0100.JPG"), 7, new DateTime(2023, 1, 9, 10, 0, 0), null, null, null, []);
        var raw = new byte[4096];
        new Random(7).NextBytes(raw);
        "II*\0"u8.CopyTo(raw);
        File.WriteAllBytes(Path.Combine(CopyFolder, "RAW_0100.CR2"), raw);
        // Exact duplicate inside the same import.
        Directory.CreateDirectory(Path.Combine(CopyFolder, "sub"));
        File.Copy(Path.Combine(CopyFolder, "IMG_0001.jpg"), Path.Combine(CopyFolder, "sub", "IMG_0001 - copia.jpg"));

        Jpeg(Path.Combine(InPlaceFolder, "Playa 2021.jpg"), 11, new DateTime(2021, 7, 20, 13, 0, 0), TimeSpan.FromHours(2), new GeoLocation(36.7213, -4.4214), "Playa", ["beach"]);
        Jpeg(Path.Combine(InPlaceFolder, "Nieve_2019.jpg"), 12, new DateTime(2019, 2, 10, 11, 0, 0), null, null, null, ["snow"]);
        // Duplicate of a file imported in the first (copy) import.
        File.Copy(Path.Combine(CopyFolder, "IMG_0002.jpg"), Path.Combine(InPlaceFolder, "duplicado de IMG_0002.jpg"));
        // Incompatible files.
        File.WriteAllText(Path.Combine(InPlaceFolder, "corrupto.jpg"), "this is not a JPEG file");
        File.WriteAllBytes(Path.Combine(InPlaceFolder, "vacio.png"), []);
        Png(Path.Combine(InPlaceFolder, "png-disfrazado.jpg"), 13);
        File.WriteAllText(Path.Combine(InPlaceFolder, "notas.txt"), "not a media file");

        (HasVideo, VideoNote) = Video(Path.Combine(InPlaceFolder, "clip.mp4"));

        Hashes = Directory.EnumerateFiles(Root, "*", SearchOption.AllDirectories)
            .ToDictionary(f => Path.GetRelativePath(Root, f), Sha256);
        File.WriteAllText(Path.Combine(Root, "manifest.json"), JsonSerializer.Serialize(Hashes, new JsonSerializerOptions { WriteIndented = true }));
    }

    /// <summary>Returns the files whose content or timestamps changed since generation (must be empty).</summary>
    public IReadOnlyList<string> ChangedOriginals() =>
        Hashes.Where(kv => !File.Exists(Path.Combine(Root, kv.Key)) || Sha256(Path.Combine(Root, kv.Key)) != kv.Value).Select(kv => kv.Key).ToList();

    public static string Sha256(string path) => Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(path)));

    internal static void Jpeg(string path, int seed, DateTime taken, TimeSpan? offset, GeoLocation? location, string? title, IReadOnlyList<string> keywords)
    {
        using var bmp = Pattern(1600, 1067, seed);
        using var image = SKImage.FromBitmap(bmp);
        using var data = image.Encode(SKEncodedImageFormat.Jpeg, 90);
        var metadata = new ExportMetadata(new CaptureDate(taken, offset, DateSource.ExifOriginal),
            new CameraInfo("QA Labs", "QA Cam 1", "QA 35mm", 2.8, 1 / 250.0, 200, 35), location, 1600, 1067, title, null, keywords, 0);
        File.WriteAllBytes(path, JpegMetadataWriter.Inject(data.ToArray(), metadata, includeLocation: location is not null, includeDescriptive: true));
    }

    private static void Png(string path, int seed)
    {
        using var bmp = Pattern(800, 600, seed);
        using var image = SKImage.FromBitmap(bmp);
        using var data = image.Encode(SKEncodedImageFormat.Png, 100);
        File.WriteAllBytes(path, data.ToArray());
    }

    private static SKBitmap Pattern(int w, int h, int seed)
    {
        var rng = new Random(seed);
        var bmp = new SKBitmap(new SKImageInfo(w, h, SKColorType.Bgra8888, SKAlphaType.Opaque));
        using var canvas = new SKCanvas(bmp);
        using (var bg = new SKPaint())
        {
            bg.Shader = SKShader.CreateLinearGradient(new SKPoint(0, 0), new SKPoint(w, h),
                [new SKColor((byte)rng.Next(256), (byte)rng.Next(256), (byte)rng.Next(256)), new SKColor((byte)rng.Next(256), (byte)rng.Next(256), (byte)rng.Next(256))],
                SKShaderTileMode.Clamp);
            canvas.DrawRect(0, 0, w, h, bg);
        }

        using var paint = new SKPaint { IsAntialias = true };
        for (var i = 0; i < 8; i++)
        {
            paint.Color = new SKColor((byte)rng.Next(256), (byte)rng.Next(256), (byte)rng.Next(256), 220);
            canvas.DrawCircle(rng.Next(w), rng.Next(h), rng.Next(40, h / 3), paint);
        }

        using var font = new SKFont { Size = h / 8f };
        paint.Color = SKColors.White;
        canvas.DrawText($"QA #{seed}", w * 0.05f, h * 0.9f, SKTextAlign.Left, font, paint);
        return bmp;
    }

    /// <summary>Creates a short H.264/AAC MP4 with ffmpeg when available (the workflow installs it).</summary>
    private static (bool, string?) Video(string path)
    {
        var ffmpeg = Environment.GetEnvironmentVariable("PW_FFMPEG") ?? "ffmpeg";
        try
        {
            var psi = new ProcessStartInfo(ffmpeg)
            {
                RedirectStandardError = true,
                RedirectStandardOutput = true,
                UseShellExecute = false,
            };
            foreach (var a in new[] { "-y", "-loglevel", "error", "-f", "lavfi", "-i", "testsrc=duration=6:size=640x360:rate=25", "-f", "lavfi", "-i", "sine=frequency=440:duration=6",
                         "-c:v", "libx264", "-pix_fmt", "yuv420p", "-c:a", "aac", "-shortest", "-movflags", "+faststart", path })
            {
                psi.ArgumentList.Add(a);
            }

            using var p = Process.Start(psi)!;
            var err = p.StandardError.ReadToEnd();
            p.WaitForExit(60_000);
            return p.ExitCode == 0 && File.Exists(path) ? (true, null) : (false, "ffmpeg failed: " + err);
        }
        catch (System.ComponentModel.Win32Exception ex)
        {
            return (false, "ffmpeg not available: " + ex.Message);
        }
    }
}
