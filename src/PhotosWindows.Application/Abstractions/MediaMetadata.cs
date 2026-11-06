using PhotosWindows.Domain;

namespace PhotosWindows.Application.Abstractions;

/// <summary>Normalised metadata extracted from a file. Every field is optional.</summary>
public sealed record MediaMetadata
{
    public CaptureDate? CaptureDate { get; init; }
    public int? Width { get; init; }
    public int? Height { get; init; }
    public int Orientation { get; init; } = 1;
    public TimeSpan? Duration { get; init; }
    public double? FrameRate { get; init; }
    public string? VideoCodec { get; init; }
    public CameraInfo Camera { get; init; } = CameraInfo.Empty;
    public GeoLocation? Location { get; init; }
    public string? Title { get; init; }
    public string? Description { get; init; }
    public IReadOnlyList<string> Keywords { get; init; } = [];
    public int? Rating { get; init; }
    /// <summary>Apple-style content identifier used to pair a still with its motion clip, when present.</summary>
    public string? ContentIdentifier { get; init; }
    /// <summary>Flat list of raw tags (directory/tag/value) for the inspector's "all metadata" view.</summary>
    public IReadOnlyList<MetadataEntry> Raw { get; init; } = [];
    public IReadOnlyList<string> Warnings { get; init; } = [];
}

public sealed record MetadataEntry(string Directory, string Tag, string Value);

public interface IMetadataReader
{
    /// <summary>Reads metadata. Must not throw for malformed files: problems are reported in <see cref="MediaMetadata.Warnings"/>.</summary>
    MediaMetadata Read(string path, FormatFamily family);

    /// <summary>Reads organisational fields (title, description, keywords, rating) from an XMP sidecar.</summary>
    MediaMetadata ReadSidecar(string xmpPath);
}
