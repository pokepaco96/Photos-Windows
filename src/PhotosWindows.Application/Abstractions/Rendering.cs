using PhotosWindows.Domain;

namespace PhotosWindows.Application.Abstractions;

public enum OutputFormat
{
    Jpeg = 0,
    Png = 1,
    WebP = 2,
}

public sealed record EncodeOptions(OutputFormat Format = OutputFormat.Jpeg, int Quality = 90)
{
    public static EncodeOptions PreviewJpeg { get; } = new(OutputFormat.Jpeg, 88);
    public static EncodeOptions ThumbnailJpeg { get; } = new(OutputFormat.Jpeg, 82);

    public string FileExtension => Format switch
    {
        OutputFormat.Png => ".png",
        OutputFormat.WebP => ".webp",
        _ => ".jpg",
    };
}

public sealed record RenderedImage(byte[] Encoded, int Width, int Height, OutputFormat Format);

/// <summary>
/// Applies EXIF orientation + an <see cref="EditRecipe"/> and encodes the result.
/// The same pipeline is used for thumbnails, the viewer, the editor preview and export,
/// so what the user sees is what gets exported.
/// </summary>
public interface IImageRenderer
{
    /// <param name="maxLongSide">Downscale so the longest side of the result is at most this value; null = full resolution.</param>
    RenderedImage Render(string sourcePath, FormatFamily family, EditRecipe recipe, int? maxLongSide, EncodeOptions encode, CancellationToken cancellationToken);

    RenderedImage Render(RasterImage source, EditRecipe recipe, int? maxLongSide, EncodeOptions encode, CancellationToken cancellationToken);

    /// <summary>Decodes (orientation applied) and returns raw pixels, downscaled; used for analysis such as perceptual hashing.</summary>
    RasterImage DecodeForAnalysis(string sourcePath, FormatFamily family, int maxLongSide, CancellationToken cancellationToken);

    bool CanDecode(FormatFamily family);

    /// <summary>Stored pixel dimensions read from the header only (no full decode); null when unknown.</summary>
    (int Width, int Height)? ProbeSize(string sourcePath, FormatFamily family);
}

public interface IPerceptualHasher
{
    /// <summary>64-bit perceptual hash (dHash). Similar images have a small Hamming distance.</summary>
    ulong Compute(RasterImage image);
}
