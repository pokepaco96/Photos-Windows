using PhotosWindows.Application.Abstractions;
using PhotosWindows.Domain;

namespace PhotosWindows.Imaging.Tests;

public class PerceptualHashTests
{
    private readonly SkiaImageRenderer _renderer = new([]);
    private readonly PerceptualHasher _hasher = new();

    private ulong HashOf(string path, FormatFamily family) => _hasher.Compute(_renderer.DecodeForAnalysis(path, family, 64, CancellationToken.None));

    [Fact]
    public void Resized_and_recompressed_copies_are_close_while_different_images_are_far()
    {
        using var dir = new TempDir();
        using var a = TestImages.Gradient(400, 300, seed: 1);
        using var aSmall = a.Resize(new SkiaSharp.SKImageInfo(200, 150), new SkiaSharp.SKSamplingOptions(SkiaSharp.SKFilterMode.Linear));
        using var b = TestImages.Quadrants(400, 300);
        var original = HashOf(TestImages.SavePng(a, dir.File("a.png")), FormatFamily.Png);
        var copy = HashOf(TestImages.SaveJpeg(aSmall, dir.File("a_small.jpg"), 70), FormatFamily.Jpeg);
        var other = HashOf(TestImages.SavePng(b, dir.File("b.png")), FormatFamily.Png);
        Assert.InRange(PerceptualHasher.HammingDistance(original, copy), 0, 6);
        Assert.True(PerceptualHasher.HammingDistance(original, other) > 10);
    }

    [Fact]
    public void Hamming_distance_counts_differing_bits()
    {
        Assert.Equal(0, PerceptualHasher.HammingDistance(0xFFUL, 0xFFUL));
        Assert.Equal(64, PerceptualHasher.HammingDistance(0UL, ulong.MaxValue));
        Assert.Equal(1, PerceptualHasher.HammingDistance(0b1000, 0));
    }

    [Fact]
    public void Hash_requires_an_image()
    {
        Assert.Throws<ArgumentNullException>(() => _hasher.Compute(null!));
        var img = new RasterImage(1, 1, new byte[4]);
        _ = _hasher.Compute(img);
    }
}
