namespace PhotosWindows.Domain;

/// <summary>
/// Logical photo or video in the library. One asset may own several physical files
/// (original, RAW pair, live video, sidecar) — see <see cref="AssetFile"/>.
/// </summary>
public sealed class Asset
{
    public long Id { get; set; }
    public Guid Uid { get; set; } = Guid.NewGuid();
    public MediaType MediaType { get; set; }

    public CaptureDate CaptureDate { get; set; }
    public DateTime ImportedUtc { get; set; }
    public DateTime FileModifiedUtc { get; set; }

    public int PixelWidth { get; set; }
    public int PixelHeight { get; set; }

    /// <summary>EXIF orientation 1..8 of the original. Display code must apply it.</summary>
    public int Orientation { get; set; } = 1;

    public TimeSpan? Duration { get; set; }

    public string FileName { get; set; } = string.Empty;
    public string Format { get; set; } = string.Empty;
    public long FileSize { get; set; }

    public CameraInfo Camera { get; set; } = CameraInfo.Empty;
    public GeoLocation? Location { get; set; }

    // User-organisational metadata (never written back to originals).
    public string? Title { get; set; }
    public string? Description { get; set; }
    public int Rating { get; set; }
    public bool IsFavorite { get; set; }
    public bool IsHidden { get; set; }
    public DateTime? DeletedUtc { get; set; }
    public bool HasEdits { get; set; }
    public bool HasLiveVideo { get; set; }
    public bool HasRawPair { get; set; }

    public ThumbnailState ThumbnailState { get; set; }
    public ulong? PerceptualHash { get; set; }

    public bool IsInTrash => DeletedUtc.HasValue;

    /// <summary>Width/height as displayed after applying EXIF orientation.</summary>
    public (int Width, int Height) DisplaySize =>
        Orientation is >= 5 and <= 8 ? (PixelHeight, PixelWidth) : (PixelWidth, PixelHeight);
}

/// <summary>Physical file belonging to an asset.</summary>
public sealed class AssetFile
{
    public long Id { get; set; }
    public long AssetId { get; set; }
    public FileRole Role { get; set; }
    public StorageMode StorageMode { get; set; }

    /// <summary>Absolute path for referenced files; path relative to the library root for managed files.</summary>
    public string StoredPath { get; set; } = string.Empty;

    public long SizeBytes { get; set; }
    public string Sha256 { get; set; } = string.Empty;
    public DateTime FileModifiedUtc { get; set; }
    public string Format { get; set; } = string.Empty;
    public bool IsAvailable { get; set; } = true;
    public DateTime LastVerifiedUtc { get; set; }
}

public sealed record CameraInfo(
    string? Make,
    string? Model,
    string? Lens,
    double? FNumber,
    double? ExposureTimeSeconds,
    int? Iso,
    double? FocalLengthMm)
{
    public static CameraInfo Empty { get; } = new(null, null, null, null, null, null, null);

    public string? DisplayName
    {
        get
        {
            if (string.IsNullOrWhiteSpace(Model))
            {
                return string.IsNullOrWhiteSpace(Make) ? null : Make!.Trim();
            }

            var model = Model!.Trim();
            var make = Make?.Trim();
            // Many cameras already prefix the model with the make ("Canon EOS R6").
            return string.IsNullOrEmpty(make) || model.StartsWith(make, StringComparison.OrdinalIgnoreCase)
                ? model
                : $"{make} {model}";
        }
    }
}
