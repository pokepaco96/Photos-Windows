using System.Buffers.Binary;
using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using PhotosWindows.Application.Abstractions;
using PhotosWindows.Domain;
using PhotosWindows.Infrastructure.Metadata;

namespace PhotosWindows.Integration.Tests;

/// <summary>
/// HEICs saved by apps often carry no EXIF size, date or camera (260 of a 60 GB iPhone library). The size is in the
/// container ('ispe' of the primary item) and must be read from there, never from the stored thumbnail's; fields that
/// are not in the file stay unknown.
/// </summary>
public class HeifMetadataTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "pw-heif-meta-" + Guid.NewGuid().ToString("N"));

    public HeifMetadataTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        foreach (var f in Directory.EnumerateFiles(_dir)) File.Delete(f);
        Directory.Delete(_dir);
    }

    private static byte[] Box(string type, params byte[][] children)
    {
        var body = children.SelectMany(c => c).ToArray();
        var box = new byte[8 + body.Length];
        BinaryPrimitives.WriteUInt32BigEndian(box, (uint)box.Length);
        Encoding.ASCII.GetBytes(type).CopyTo(box, 4);
        body.CopyTo(box, 8);
        return box;
    }

    private static byte[] FullBox(string type, byte version, params byte[][] children) => Box(type, [[version, 0, 0, 0], .. children]);

    private static byte[] U16(int v) => [(byte)(v >> 8), (byte)v];

    private static byte[] U32(uint v)
    {
        var b = new byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(b, v);
        return b;
    }

    private static byte[] Infe(int id) => FullBox("infe", 2, U16(id), U16(0), Encoding.ASCII.GetBytes("hvc1"), [0]);

    /// <summary>A HEIF container with a primary image of <paramref name="w"/>×<paramref name="h"/> and, optionally, a 416×312 stored thumbnail. No EXIF, no pixels.</summary>
    private static byte[] Heif(uint w, uint h, bool withThumbnail)
    {
        var items = withThumbnail ? 2 : 1;
        byte[] ipco = withThumbnail
            ? Box("ipco", FullBox("ispe", 0, U32(w), U32(h)), FullBox("ispe", 0, U32(416), U32(312)))
            : Box("ipco", FullBox("ispe", 0, U32(w), U32(h)));
        byte[] ipma = withThumbnail
            ? FullBox("ipma", 0, U32(2), U16(1), [1, 0x81], U16(2), [1, 0x82])
            : FullBox("ipma", 0, U32(1), U16(1), [1, 0x81]);
        var ftyp = Box("ftyp", Encoding.ASCII.GetBytes("heic"), U32(0), Encoding.ASCII.GetBytes("mif1heic"));

        // iloc version 0, 4-byte offsets and lengths: each item one 16-byte extent inside 'mdat' (the reader looks items up there).
        byte[] Iloc(uint mdatData) => FullBox("iloc", 0, [0x44, 0x00], U16(items),
            U16(1), U16(0), U16(1), U32(mdatData), U32(16),
            withThumbnail ? [.. U16(2), .. U16(0), .. U16(1), .. U32(mdatData + 16), .. U32(16)] : []);

        byte[] Meta(uint mdatData) => FullBox("meta", 0,
            FullBox("hdlr", 0, U32(0), Encoding.ASCII.GetBytes("pict"), new byte[12], [0]),
            FullBox("pitm", 0, U16(1)),
            FullBox("iinf", 0, U16(items), Infe(1), withThumbnail ? Infe(2) : []),
            withThumbnail ? FullBox("iref", 0, Box("thmb", U16(2), U16(1), U16(1))) : [],
            Box("iprp", ipco, ipma),
            Iloc(mdatData));

        var mdatData = (uint)(ftyp.Length + Meta(0).Length + 8);
        return [.. ftyp, .. Meta(mdatData), .. Box("mdat", new byte[32])];
    }

    private MediaMetadata Read(byte[] file)
    {
        var path = Path.Combine(_dir, Guid.NewGuid().ToString("N") + ".heic");
        File.WriteAllBytes(path, file);
        return new MetadataExtractorReader(NullLogger<MetadataExtractorReader>.Instance, new SystemClock()).Read(path, FormatFamily.Heif);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void A_heic_without_exif_gets_its_size_from_the_primary_item_not_the_thumbnail(bool withThumbnail)
    {
        var meta = Read(Heif(5712, 4284, withThumbnail));

        Assert.Equal((5712, 4284), (meta.Width, meta.Height));
        // Nothing else is in the file: unknown, not invented.
        Assert.Null(meta.CaptureDate);
        Assert.Null(meta.Location);
        Assert.Null(meta.Camera?.Make);
        Assert.Null(meta.Camera?.Model);
        Assert.Equal(1, meta.Orientation);
    }

    [Fact]
    public void A_heic_whose_container_has_no_size_stays_unknown()
    {
        byte[] file =
        [
            .. Box("ftyp", Encoding.ASCII.GetBytes("heic"), U32(0), Encoding.ASCII.GetBytes("mif1heic")),
            .. FullBox("meta", 0, FullBox("hdlr", 0, U32(0), Encoding.ASCII.GetBytes("pict"), new byte[12], [0]), FullBox("pitm", 0, U16(1))),
        ];
        var meta = Read(file);
        Assert.Null(meta.Width);
        Assert.Null(meta.Height);
    }
}
