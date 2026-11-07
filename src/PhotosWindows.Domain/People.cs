namespace PhotosWindows.Domain;

/// <summary>
/// Face rectangle in normalized coordinates (0..1) of the displayed photo: EXIF orientation applied, no edits.
/// Normalized coordinates survive thumbnail sizes and re-decoding at another resolution.
/// </summary>
public readonly record struct FaceBox(float X, float Y, float Width, float Height)
{
    public bool IsValid => Width > 0 && Height > 0 && X >= -0.01f && Y >= -0.01f && X + Width <= 1.01f && Y + Height <= 1.01f;

    /// <summary>Clips the rectangle to the image (detectors may return boxes slightly outside it).</summary>
    public FaceBox Clamp()
    {
        var x1 = Math.Clamp(X, 0f, 1f);
        var y1 = Math.Clamp(Y, 0f, 1f);
        var x2 = Math.Clamp(X + Width, 0f, 1f);
        var y2 = Math.Clamp(Y + Height, 0f, 1f);
        return new FaceBox(x1, y1, Math.Max(0f, x2 - x1), Math.Max(0f, y2 - y1));
    }

    /// <summary>Intersection over union, used to match faces of the same photo across re-analyses.</summary>
    public float IntersectionOverUnion(FaceBox other)
    {
        var x1 = Math.Max(X, other.X);
        var y1 = Math.Max(Y, other.Y);
        var x2 = Math.Min(X + Width, other.X + other.Width);
        var y2 = Math.Min(Y + Height, other.Y + other.Height);
        var intersection = Math.Max(0f, x2 - x1) * Math.Max(0f, y2 - y1);
        var union = Width * Height + other.Width * other.Height - intersection;
        return union <= 0 ? 0 : intersection / union;
    }
}

/// <summary>How a face got its group. Only <see cref="Suggested"/> faces are ever moved by automatic grouping.</summary>
public enum FaceReview
{
    /// <summary>Grouped (or left ungrouped) automatically; a suggestion the user can correct.</summary>
    Suggested = 0,

    /// <summary>The user put the face in this group (named it, moved it, split it). Never changed automatically.</summary>
    Confirmed = 1,

    /// <summary>The user marked the face as not relevant / unknown. It belongs to no group.</summary>
    Ignored = 2,
}

/// <summary>Result of analysing one photo with one model version.</summary>
public enum FaceAnalysisState
{
    Analyzed = 1,
    /// <summary>The photo could not be decoded or analysed (corrupt, unavailable); retried on the next "Analyse again".</summary>
    Failed = 2,
}

/// <summary>A detected face. The descriptor (embedding) is stored separately and never leaves the catalog.</summary>
public sealed record Face(
    long Id,
    long AssetId,
    FaceBox Box,
    float Score,
    long? PersonId,
    FaceReview Review,
    string ModelVersion);

/// <summary>
/// A group of faces that probably belong to the same person. <see cref="Name"/> is only ever set by the user;
/// a group without a name is an "unidentified person". Counts only include photos that are neither hidden nor deleted.
/// </summary>
public sealed record Person(long Id, string? Name, long? CoverFaceId, int FaceCount, int AssetCount)
{
    public bool IsNamed => !string.IsNullOrWhiteSpace(Name);
}

/// <summary>What has been analysed with the current model, for the "what was analysed and what was not" view.</summary>
public sealed record FaceAnalysisStats(int Photos, int Analyzed, int Failed, int Excluded, int Faces, int People, int Ungrouped)
{
    public int Pending => Math.Max(0, Photos - Analyzed - Failed - Excluded);
}
