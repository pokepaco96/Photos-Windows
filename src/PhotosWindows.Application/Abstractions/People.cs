using PhotosWindows.Domain;

namespace PhotosWindows.Application.Abstractions;

/// <summary>A point in normalized coordinates of the analysed image.</summary>
public readonly record struct FacePoint(float X, float Y);

/// <summary>
/// A face found by a detector: box, confidence and five reference points (right eye, left eye, nose tip,
/// right and left mouth corners, as seen in the image) used to align the face before computing its descriptor.
/// </summary>
public sealed record FaceDetection(FaceBox Box, float Score, IReadOnlyList<FacePoint> Landmarks);

/// <summary>Finds faces in a decoded photo. Runs locally; implementations must not use the network.</summary>
public interface IFaceDetector
{
    /// <summary>Identifies the model (name and version); stored with every result so a model change can be detected.</summary>
    string ModelId { get; }

    /// <summary>False when the model is not installed (the feature is then unavailable, never faked).</summary>
    bool IsReady { get; }

    /// <param name="image">Oriented pixels (EXIF orientation already applied).</param>
    Task<IReadOnlyList<FaceDetection>> DetectAsync(RasterImage image, CancellationToken cancellationToken);
}

/// <summary>Computes a face descriptor used only to group similar faces of the same library.</summary>
public interface IFaceEmbedder
{
    string ModelId { get; }

    bool IsReady { get; }

    int Dimensions { get; }

    /// <summary>Returns an L2-normalized descriptor for <paramref name="face"/> in <paramref name="image"/>.</summary>
    Task<float[]> EmbedAsync(RasterImage image, FaceDetection face, CancellationToken cancellationToken);
}

/// <summary>A downloadable model file with a fixed source, size, checksum and licence.</summary>
public sealed record ModelDescriptor(
    string Id,
    string DisplayName,
    string FileName,
    Uri Source,
    long SizeBytes,
    string Sha256,
    string License,
    Uri LicenseUrl,
    string Provenance);

public enum ModelState
{
    NotInstalled = 0,
    Installed = 1,
    /// <summary>The file exists but its SHA-256 does not match: it is never loaded.</summary>
    Invalid = 2,
}

public sealed record ModelStatus(ModelDescriptor Model, ModelState State, string? Path);

/// <summary>
/// Installs the optional face models. Nothing is downloaded unless the user asks for it; every file is checked
/// against the SHA-256 fixed in the code before it is used.
/// </summary>
public interface IFaceModelManager
{
    IReadOnlyList<ModelDescriptor> Models { get; }

    IReadOnlyList<ModelStatus> GetStatus();

    bool AllInstalled { get; }

    /// <summary>Downloads the models from their fixed sources. Only called after the user confirmed it.</summary>
    Task InstallAsync(IProgress<(long Done, long Total)>? progress, CancellationToken cancellationToken);

    /// <summary>Installs model files the user already has (offline); each must match its checksum.</summary>
    Task<IReadOnlyList<string>> InstallFromFilesAsync(IEnumerable<string> paths, CancellationToken cancellationToken);

    void Uninstall();

    /// <summary>Raised after models are installed or removed.</summary>
    event EventHandler? Changed;
}

/// <summary>A face with its descriptor, as needed by grouping.</summary>
public sealed record FaceVector(long FaceId, long AssetId, long? PersonId, FaceReview Review, float[] Embedding);

/// <summary>A face ready to be stored after analysing a photo.</summary>
public sealed record NewFace(FaceBox Box, float Score, float[] Embedding);

/// <summary>Result of grouping: existing faces moved to a person, or to a new group (by key) to be created.</summary>
public sealed record FaceAssignment(long FaceId, long? PersonId, int? NewGroupKey);

public interface IPeopleRepository
{
    /// <summary>Photos (not deleted, not excluded) without a result for <paramref name="modelVersion"/>.</summary>
    Task<IReadOnlyList<long>> GetAssetsToAnalyzeAsync(string modelVersion, int limit, IReadOnlyCollection<long>? onlyAssets, CancellationToken cancellationToken);

    /// <summary>
    /// Stores the result for one photo, replacing earlier faces of that photo. A new face that overlaps an earlier one
    /// (same place in the photo) keeps the user's decision (its person and review state), so re-analysing or changing
    /// the model does not lose names.
    /// </summary>
    Task SaveAnalysisAsync(long assetId, string modelVersion, FaceAnalysisState state, IReadOnlyList<NewFace> faces, DateTime utcNow, CancellationToken cancellationToken);

    Task<FaceAnalysisStats> GetStatsAsync(string modelVersion, CancellationToken cancellationToken);

    Task<IReadOnlyList<FaceVector>> GetVectorsAsync(string modelVersion, CancellationToken cancellationToken);

    /// <summary>Applies grouping in one transaction; creates one unnamed person per new group key.</summary>
    Task ApplyAssignmentsAsync(IReadOnlyList<FaceAssignment> assignments, DateTime utcNow, CancellationToken cancellationToken);

    Task<IReadOnlyList<Person>> GetPeopleAsync(CancellationToken cancellationToken);

    Task<Person?> GetPersonAsync(long personId, CancellationToken cancellationToken);

    Task<IReadOnlyList<Face>> GetFacesOfPersonAsync(long personId, CancellationToken cancellationToken);

    /// <summary>Faces not in any group and not ignored (singletons waiting for more photos or for the user).</summary>
    Task<IReadOnlyList<Face>> GetUngroupedFacesAsync(int limit, CancellationToken cancellationToken);

    Task<IReadOnlyList<Face>> GetFacesOfAssetAsync(long assetId, CancellationToken cancellationToken);

    Task<Face?> GetFaceAsync(long faceId, CancellationToken cancellationToken);

    Task RenameAsync(long personId, string? name, DateTime utcNow, CancellationToken cancellationToken);

    /// <summary>Moves every face of <paramref name="sourcePersonIds"/> into <paramref name="targetPersonId"/> (confirmed) and deletes the sources.</summary>
    Task MergeAsync(IReadOnlyCollection<long> sourcePersonIds, long targetPersonId, DateTime utcNow, CancellationToken cancellationToken);

    /// <summary>Moves faces to a person (or to a new unnamed person when null) and confirms them. Returns the person id.</summary>
    Task<long> MoveFacesAsync(IReadOnlyCollection<long> faceIds, long? targetPersonId, DateTime utcNow, CancellationToken cancellationToken);

    /// <summary>Marks faces as not relevant: they leave their group and are never grouped again.</summary>
    Task IgnoreFacesAsync(IReadOnlyCollection<long> faceIds, CancellationToken cancellationToken);

    Task SetCoverAsync(long personId, long faceId, CancellationToken cancellationToken);

    /// <summary>Deletes the group, its faces and their descriptors. The photos are not touched.</summary>
    Task DeletePersonAsync(long personId, CancellationToken cancellationToken);

    /// <summary>Excludes photos from face analysis and deletes their faces and descriptors.</summary>
    Task ExcludeAssetsAsync(IReadOnlyCollection<long> assetIds, DateTime utcNow, CancellationToken cancellationToken);

    Task IncludeAssetsAsync(IReadOnlyCollection<long> assetIds, CancellationToken cancellationToken);

    Task<bool> IsExcludedAsync(long assetId, CancellationToken cancellationToken);

    /// <summary>Marks photos (all when null) for analysis again; their faces stay until the new result replaces them.</summary>
    Task ResetAnalysisAsync(IReadOnlyCollection<long>? assetIds, CancellationToken cancellationToken);

    /// <summary>
    /// Physically deletes every face, descriptor, group and analysis result (exclusions are kept: they are the
    /// user's choice, not derived data). Uses SQLite secure delete, checkpoints the WAL and vacuums the file.
    /// </summary>
    Task DeleteAllAsync(CancellationToken cancellationToken);

    Task<string?> GetSettingAsync(string key, CancellationToken cancellationToken);

    Task SetSettingAsync(string key, string? value, CancellationToken cancellationToken);
}
