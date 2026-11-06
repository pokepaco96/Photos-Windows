using System.Text;
using PhotosWindows.Domain;

namespace PhotosWindows.Domain.Tests;

public class MediaFormatsTests
{
    private static byte[] Header(params byte[] bytes) => bytes.Concat(new byte[32]).Take(32).ToArray();

    private static byte[] Ftyp(string brand) => Header([0, 0, 0, 0x18, .. Encoding.ASCII.GetBytes("ftyp" + brand)]);

    [Theory]
    [InlineData("a.JPG", FormatFamily.Jpeg)]
    [InlineData("a.heic", FormatFamily.Heif)]
    [InlineData("a.CR3", FormatFamily.Raw)]
    [InlineData("a.mov", FormatFamily.Video)]
    [InlineData("a.xmp", FormatFamily.Sidecar)]
    public void Extension_lookup_is_case_insensitive(string file, FormatFamily family) =>
        Assert.Equal(family, MediaFormats.FromExtension(file)!.Family);

    [Fact]
    public void Unknown_extension_is_not_a_candidate()
    {
        Assert.False(MediaFormats.IsCandidate("notes.txt"));
        Assert.False(MediaFormats.IsCandidate("noextension"));
    }

    [Fact]
    public void Sniffs_common_signatures()
    {
        Assert.Equal(FormatFamily.Jpeg, MediaFormats.Sniff(Header(0xFF, 0xD8, 0xFF, 0xE0)));
        Assert.Equal(FormatFamily.Png, MediaFormats.Sniff(Header(0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A)));
        Assert.Equal(FormatFamily.Gif, MediaFormats.Sniff(Header("GIF89a"u8.ToArray())));
        Assert.Equal(FormatFamily.WebP, MediaFormats.Sniff(Header("RIFF\0\0\0\0WEBP"u8.ToArray())));
        Assert.Equal(FormatFamily.Heif, MediaFormats.Sniff(Ftyp("heic")));
        Assert.Equal(FormatFamily.Avif, MediaFormats.Sniff(Ftyp("avif")));
        Assert.Equal(FormatFamily.Video, MediaFormats.Sniff(Ftyp("isom")));
        Assert.Equal(FormatFamily.Video, MediaFormats.Sniff(Ftyp("qt  ")));
        Assert.Equal(FormatFamily.Raw, MediaFormats.Sniff(Ftyp("crx ")));
        Assert.Equal(FormatFamily.Tiff, MediaFormats.Sniff(Header((byte)'I', (byte)'I', 0x2A, 0)));
        Assert.Equal(FormatFamily.Raw, MediaFormats.Sniff(Header((byte)'I', (byte)'I', 0x2A, 0), ".nef"));
        Assert.Equal(FormatFamily.Sidecar, MediaFormats.Sniff(Header("<?xpacket begin"u8.ToArray())));
        Assert.Equal(FormatFamily.Unknown, MediaFormats.Sniff(Header("hello world"u8.ToArray())));
        Assert.Equal(FormatFamily.Unknown, MediaFormats.Sniff([]));
    }

    [Fact]
    public void Renamed_file_does_not_match_its_extension()
    {
        var pngHeader = Header(0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A);
        Assert.False(MediaFormats.SignatureMatchesExtension(MediaFormats.Sniff(pngHeader), "photo.jpg"));
        Assert.True(MediaFormats.SignatureMatchesExtension(MediaFormats.Sniff(pngHeader), "photo.png"));
        Assert.False(MediaFormats.SignatureMatchesExtension(FormatFamily.Unknown, "photo.jpg"));
    }
}
