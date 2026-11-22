using System.Buffers.Binary;
using System.Text;
using PhotosWindows.Application.Common;

namespace PhotosWindows.Integration.Tests;

/// <summary>Synthetic HEIF containers: the probe only reads box headers, so no image data is needed.</summary>
public class HeifThumbnailProbeTests
{
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

    /// <summary>A SingleItemTypeReference: from_item_ID, reference_count, to_item_ID (16-bit ids, iref version 0).</summary>
    private static byte[] Reference(string type, ushort from, ushort to) => Box(type, [(byte)(from >> 8), (byte)from, 0, 1, (byte)(to >> 8), (byte)to]);

    private static byte[] File(params byte[][] metaChildren) =>
    [
        .. Box("ftyp", Encoding.ASCII.GetBytes("heic"), [0, 0, 0, 0], Encoding.ASCII.GetBytes("mif1heic")),
        .. FullBox("meta", 0, metaChildren),
        .. Box("mdat", new byte[64]),
    ];

    private static bool? Probe(byte[] file) => HeifThumbnailProbe.HasThumbnail(new MemoryStream(file));

    [Fact]
    public void A_thmb_reference_means_a_stored_thumbnail()
    {
        var file = File(FullBox("hdlr", 0, new byte[20]), FullBox("iref", 0, Reference("dimg", 1, 2), Reference("thmb", 50, 1), Reference("cdsc", 60, 1)));
        Assert.True(Probe(file));
    }

    [Fact]
    public void References_without_thmb_or_no_iref_mean_no_thumbnail()
    {
        Assert.False(Probe(File(FullBox("hdlr", 0, new byte[20]), FullBox("iref", 0, Reference("dimg", 1, 2), Reference("cdsc", 60, 1)))));
        Assert.False(Probe(File(FullBox("hdlr", 0, new byte[20]), Box("iloc", new byte[12]))));
    }

    [Fact]
    public void Unreadable_or_odd_containers_are_unknown()
    {
        Assert.Null(Probe([]));
        Assert.Null(Probe(Encoding.ASCII.GetBytes("not a heif file at all")));
        Assert.Null(Probe([.. Box("ftyp", new byte[8]), .. Box("mdat", new byte[16])])); // image data before any 'meta'

        var truncated = File(FullBox("iref", 0, Reference("thmb", 50, 1)));
        Assert.Null(Probe(truncated[..(truncated.Length - 64 - 8 - 10)])); // 'meta' cut short

        var lying = File(FullBox("iref", 0, Reference("thmb", 50, 1)));
        BinaryPrimitives.WriteUInt32BigEndian(lying.AsSpan(24 + 12), 4000); // a child box larger than its parent
        Assert.Null(Probe(lying));
    }

    [Fact]
    public void A_box_with_a_64_bit_size_is_skipped_correctly()
    {
        var ftyp = Box("ftyp", Encoding.ASCII.GetBytes("heic"), [0, 0, 0, 0]);
        var large = new byte[16 + 8];
        BinaryPrimitives.WriteUInt32BigEndian(large, 1);
        Encoding.ASCII.GetBytes("free").CopyTo(large, 4);
        BinaryPrimitives.WriteUInt64BigEndian(large.AsSpan(8), (ulong)large.Length);
        byte[] file = [.. ftyp, .. large, .. FullBox("meta", 0, FullBox("iref", 0, Reference("thmb", 2, 1)))];
        Assert.True(Probe(file));
    }
}
