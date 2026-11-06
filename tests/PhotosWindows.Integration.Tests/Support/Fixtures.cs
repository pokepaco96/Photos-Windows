using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using PhotosWindows.Application.Export;
using PhotosWindows.Domain;
using SkiaSharp;

namespace PhotosWindows.Integration.Tests.Support;

/// <summary>Generates synthetic, license-free media files for tests.</summary>
public static class Fixtures
{
    public sealed record PhotoSpec
    {
        public int Width { get; init; } = 120;
        public int Height { get; init; } = 80;
        public int Seed { get; init; }
        public DateTime? Taken { get; init; } = new DateTime(2023, 7, 14, 18, 30, 5);
        public TimeSpan? Offset { get; init; } = TimeSpan.FromHours(2);
        public string? Make { get; init; } = "Acme";
        public string? Model { get; init; } = "Acme Z100";
        public string? Lens { get; init; } = "Acme 24-70mm";
        public double? FNumber { get; init; } = 2.8;
        public double? Exposure { get; init; } = 1 / 250.0;
        public int? Iso { get; init; } = 200;
        public double? Focal { get; init; } = 35;
        public GeoLocation? Location { get; init; }
        public int Orientation { get; init; } = 1;
        public string? Title { get; init; }
        public IReadOnlyList<string> Keywords { get; init; } = [];
    }

    public static string Jpeg(string path, PhotoSpec? spec = null)
    {
        spec ??= new PhotoSpec();
        using var bmp = Pattern(spec.Width, spec.Height, spec.Seed);
        using var image = SKImage.FromBitmap(bmp);
        using var data = image.Encode(SKEncodedImageFormat.Jpeg, 90);
        var bytes = data.ToArray();
        var hasMeta = spec.Taken is not null || spec.Make is not null || spec.Location is not null || spec.Orientation != 1 || spec.Title is not null || spec.Keywords.Count > 0;
        if (hasMeta)
        {
            var metadata = new ExportMetadata(
                spec.Taken is { } t ? new CaptureDate(t, spec.Offset, DateSource.ExifOriginal) : null,
                new CameraInfo(spec.Make, spec.Model, spec.Lens, spec.FNumber, spec.Exposure, spec.Iso, spec.Focal),
                spec.Location, spec.Width, spec.Height, spec.Title, null, spec.Keywords, 0);
            bytes = JpegMetadataWriter.Inject(bytes, metadata, includeLocation: spec.Location is not null, includeDescriptive: true, orientation: spec.Orientation);
        }

        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, bytes);
        return path;
    }

    public static string Png(string path, int w = 64, int h = 64, int seed = 0)
    {
        using var bmp = Pattern(w, h, seed);
        using var image = SKImage.FromBitmap(bmp);
        using var data = image.Encode(SKEncodedImageFormat.Png, 100);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, data.ToArray());
        return path;
    }

    public static SKBitmap Pattern(int w, int h, int seed)
    {
        var bmp = new SKBitmap(new SKImageInfo(w, h, SKColorType.Bgra8888, SKAlphaType.Opaque));
        var rng = new Random(seed);
        using var canvas = new SKCanvas(bmp);
        canvas.Clear(new SKColor((byte)rng.Next(256), (byte)rng.Next(256), (byte)rng.Next(256)));
        using var paint = new SKPaint();
        for (var i = 0; i < 6; i++)
        {
            paint.Color = new SKColor((byte)rng.Next(256), (byte)rng.Next(256), (byte)rng.Next(256));
            canvas.DrawRect(rng.Next(w), rng.Next(h), rng.Next(w / 2 + 1) + 4, rng.Next(h / 2 + 1) + 4, paint);
        }

        return bmp;
    }

    /// <summary>Minimal QuickTime file: ftyp + moov/mvhd with creation time and duration (no media data).</summary>
    public static string QuickTime(string path, TimeSpan duration, DateTime createdUtc)
    {
        var ftyp = Atom("ftyp", [.. "qt  "u8.ToArray(), 0, 0, 0, 0, .. "qt  "u8.ToArray()]);
        var mvhdBody = new byte[100];
        var created = (uint)(createdUtc - new DateTime(1904, 1, 1, 0, 0, 0, DateTimeKind.Utc)).TotalSeconds;
        BinaryPrimitives.WriteUInt32BigEndian(mvhdBody.AsSpan(4), created);
        BinaryPrimitives.WriteUInt32BigEndian(mvhdBody.AsSpan(8), created);
        BinaryPrimitives.WriteUInt32BigEndian(mvhdBody.AsSpan(12), 600);
        BinaryPrimitives.WriteUInt32BigEndian(mvhdBody.AsSpan(16), (uint)(duration.TotalSeconds * 600));
        BinaryPrimitives.WriteUInt32BigEndian(mvhdBody.AsSpan(20), 0x00010000);
        BinaryPrimitives.WriteUInt16BigEndian(mvhdBody.AsSpan(24), 0x0100);
        BinaryPrimitives.WriteUInt32BigEndian(mvhdBody.AsSpan(36), 0x00010000);
        BinaryPrimitives.WriteUInt32BigEndian(mvhdBody.AsSpan(52), 0x00010000);
        BinaryPrimitives.WriteUInt32BigEndian(mvhdBody.AsSpan(68), 0x40000000);
        BinaryPrimitives.WriteUInt32BigEndian(mvhdBody.AsSpan(96), 2);
        var moov = Atom("moov", Atom("mvhd", mvhdBody));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, [.. ftyp, .. moov]);
        return path;
    }

    /// <summary>Fake TIFF-structured RAW (not decodable, but recognised by signature + extension).</summary>
    public static string FakeRaw(string path, int seed = 0)
    {
        var bytes = new byte[2048];
        new Random(seed).NextBytes(bytes);
        "II*\0"u8.CopyTo(bytes);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, bytes);
        return path;
    }

    public static string Xmp(string path, string? title, IReadOnlyList<string> keywords, int rating = 0)
    {
        var sb = new StringBuilder();
        sb.Append("<?xpacket begin=\"\" id=\"W5M0MpCehiHzreSzNTczkc9d\"?><x:xmpmeta xmlns:x=\"adobe:ns:meta/\"><rdf:RDF xmlns:rdf=\"http://www.w3.org/1999/02/22-rdf-syntax-ns#\">");
        sb.Append($"<rdf:Description rdf:about=\"\" xmlns:dc=\"http://purl.org/dc/elements/1.1/\" xmlns:xmp=\"http://ns.adobe.com/xap/1.0/\" xmp:Rating=\"{rating}\">");
        if (title is not null) sb.Append($"<dc:title><rdf:Alt><rdf:li xml:lang=\"x-default\">{title}</rdf:li></rdf:Alt></dc:title>");
        sb.Append("<dc:subject><rdf:Bag>");
        foreach (var k in keywords) sb.Append($"<rdf:li>{k}</rdf:li>");
        sb.Append("</rdf:Bag></dc:subject></rdf:Description></rdf:RDF></x:xmpmeta><?xpacket end=\"w\"?>");
        File.WriteAllText(path, sb.ToString());
        return path;
    }

    public static string Sha256(string path) => Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(path)));

    private static byte[] Atom(string type, byte[] body)
    {
        var a = new byte[body.Length + 8];
        BinaryPrimitives.WriteUInt32BigEndian(a, (uint)a.Length);
        Encoding.ASCII.GetBytes(type).CopyTo(a, 4);
        body.CopyTo(a, 8);
        return a;
    }
}
