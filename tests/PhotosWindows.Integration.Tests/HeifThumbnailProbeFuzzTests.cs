using System.Buffers.Binary;
using System.Diagnostics;
using System.Text;
using PhotosWindows.Application.Common;

namespace PhotosWindows.Integration.Tests;

/// <summary>
/// Malformed, truncated and lying HEIF containers: the probe never throws, never reads more than its limit, finishes
/// quickly, and answers true/false only for a well-formed structure (null otherwise).
/// </summary>
public class HeifThumbnailProbeFuzzTests
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

    private static byte[] Reference16(string type, ushort from, ushort to) => Box(type, [(byte)(from >> 8), (byte)from, 0, 1, (byte)(to >> 8), (byte)to]);

    private static byte[] Reference32(string type, uint from, uint to)
    {
        var b = new byte[10];
        BinaryPrimitives.WriteUInt32BigEndian(b, from);
        BinaryPrimitives.WriteUInt16BigEndian(b.AsSpan(4), 1);
        BinaryPrimitives.WriteUInt32BigEndian(b.AsSpan(6), to);
        return Box(type, b);
    }

    private static byte[] Heic(byte[] iref) =>
    [
        .. Box("ftyp", Encoding.ASCII.GetBytes("heic"), [0, 0, 0, 0], Encoding.ASCII.GetBytes("mif1heicmiaf")),
        .. FullBox("meta", 0, FullBox("hdlr", 0, new byte[20]), FullBox("pitm", 0, [0, 1]), Box("iinf", new byte[30]), iref, Box("iloc", new byte[40])),
        .. Box("mdat", new byte[256]),
    ];

    /// <summary>Counts what the probe reads.</summary>
    private sealed class CountingStream(byte[] data, bool seekable = true) : MemoryStream(data)
    {
        public long BytesRead { get; private set; }

        public override bool CanSeek => seekable && base.CanSeek;

        public override int Read(byte[] buffer, int offset, int count)
        {
            var n = base.Read(buffer, offset, count);
            BytesRead += n;
            return n;
        }

        public override int Read(Span<byte> buffer)
        {
            var n = base.Read(buffer);
            BytesRead += n;
            return n;
        }
    }

    /// <summary>8 top-level headers of at most 16 bytes, then one 'meta' body of at most MaxMetaBytes.</summary>
    private const long MaxRead = (8 * 16) + HeifThumbnailProbe.MaxMetaBytes;

    private static bool? ProbeChecked(byte[] file, bool seekable = true)
    {
        using var stream = new CountingStream(file, seekable);
        var result = HeifThumbnailProbe.HasThumbnail(stream);
        Assert.True(stream.BytesRead <= MaxRead, $"read {stream.BytesRead} bytes");
        return result;
    }

    [Fact]
    public void Thmb_is_found_with_16_and_32_bit_item_ids_and_when_repeated()
    {
        Assert.True(ProbeChecked(Heic(FullBox("iref", 0, Reference16("thmb", 2, 1)))));
        Assert.True(ProbeChecked(Heic(FullBox("iref", 1, Reference32("cdsc", 70000, 1), Reference32("thmb", 70001, 1)))));
        Assert.True(ProbeChecked(Heic(FullBox("iref", 0, Reference16("thmb", 2, 1), Reference16("thmb", 3, 1)))));
        Assert.False(ProbeChecked(Heic(FullBox("iref", 1, Reference32("dimg", 1, 2)))));
    }

    [Fact]
    public void A_meta_box_after_eight_others_or_larger_than_the_limit_is_unknown()
    {
        var free = Box("free", new byte[4]);
        byte[] late = [.. Enumerable.Repeat(free, 8).SelectMany(b => b), .. Heic(FullBox("iref", 0, Reference16("thmb", 2, 1)))];
        Assert.Null(ProbeChecked(late));

        var huge = new byte[16];
        BinaryPrimitives.WriteUInt32BigEndian(huge, (uint)(HeifThumbnailProbe.MaxMetaBytes + 64));
        Encoding.ASCII.GetBytes("meta").CopyTo(huge, 4);
        Assert.Null(ProbeChecked(huge));
    }

    [Theory]
    [InlineData(0u)]
    [InlineData(1u)]
    [InlineData(7u)]
    [InlineData(0xFFFFFFFFu)]
    public void Odd_top_level_sizes_are_unknown_or_skipped_never_an_error(uint size)
    {
        var file = Heic(FullBox("iref", 0, Reference16("thmb", 2, 1)));
        BinaryPrimitives.WriteUInt32BigEndian(file, size); // the 'ftyp' size
        var result = ProbeChecked(file);
        Assert.True(result is null or true);
    }

    [Fact]
    public void A_64_bit_size_beyond_the_file_is_unknown()
    {
        var b = new byte[24];
        BinaryPrimitives.WriteUInt32BigEndian(b, 1);
        Encoding.ASCII.GetBytes("free").CopyTo(b, 4);
        BinaryPrimitives.WriteUInt64BigEndian(b.AsSpan(8), ulong.MaxValue);
        Assert.Null(ProbeChecked([.. b, .. Heic(FullBox("iref", 0, Reference16("thmb", 2, 1)))]));
    }

    [Fact]
    public void A_stream_that_cannot_seek_answers_only_when_meta_comes_first()
    {
        byte[] metaFirst = [.. FullBox("meta", 0, FullBox("iref", 0, Reference16("thmb", 2, 1)))];
        Assert.True(ProbeChecked(metaFirst, seekable: false));
        Assert.Null(ProbeChecked(Heic(FullBox("iref", 0, Reference16("thmb", 2, 1))), seekable: false));
    }

    [Fact]
    public void Every_truncation_of_a_valid_file_is_handled()
    {
        var file = Heic(FullBox("iref", 0, Reference16("dimg", 1, 2), Reference16("thmb", 3, 1)));
        for (var length = 0; length < file.Length; length++)
        {
            var result = ProbeChecked(file[..length]);
            // Cut inside the boxes before 'mdat': unknown. Once 'meta' is complete the answer is known.
            Assert.True(result is null or true, $"length {length}: {result}");
        }
    }

    [Fact]
    public void Twenty_thousand_random_mutations_never_throw_never_overread_and_finish_quickly()
    {
        var rnd = new Random(20261121);
        byte[][] seeds =
        [
            Heic(FullBox("iref", 0, Reference16("thmb", 2, 1))),
            Heic(FullBox("iref", 1, Reference32("dimg", 1, 2), Reference32("thmb", 3, 1))),
            Heic(FullBox("iref", 0, Reference16("cdsc", 5, 1))),
        ];
        var counts = new int[3];
        var sw = Stopwatch.StartNew();
        for (var i = 0; i < 20_000; i++)
        {
            var file = (byte[])seeds[i % seeds.Length].Clone();
            var mutations = 1 + rnd.Next(4);
            for (var m = 0; m < mutations; m++)
            {
                switch (rnd.Next(4))
                {
                    case 0: file[rnd.Next(file.Length)] = (byte)rnd.Next(256); break;
                    case 1: // a size field somewhere gets an extreme value
                        var at = rnd.Next(file.Length - 4);
                        BinaryPrimitives.WriteUInt32BigEndian(file.AsSpan(at), rnd.Next(5) switch { 0 => 0u, 1 => 1u, 2 => 0xFFFFFFFFu, 3 => (uint)rnd.Next(8), _ => (uint)rnd.Next() });
                        break;
                    case 2: file = file[..rnd.Next(file.Length)]; break;
                    default: file = [.. file, .. Enumerable.Range(0, rnd.Next(64)).Select(_ => (byte)rnd.Next(256))]; break;
                }

                if (file.Length < 5) break;
            }

            var result = ProbeChecked(file);
            counts[result switch { true => 0, false => 1, null => 2 }]++;
        }

        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(20), $"took {sw.Elapsed}");
        Assert.True(counts.All(c => c > 0), $"true/false/null: {string.Join('/', counts)}");
    }

    [Fact]
    public void Random_garbage_is_never_a_thumbnail_claim_without_a_thmb_box()
    {
        var rnd = new Random(7);
        for (var i = 0; i < 5_000; i++)
        {
            var file = new byte[rnd.Next(0, 4096)];
            rnd.NextBytes(file);
            var result = ProbeChecked(file);
            if (result == true) Assert.Contains("thmb", Encoding.ASCII.GetString(file), StringComparison.Ordinal);
        }
    }
}
