using Microsoft.Extensions.Logging;
using PhotosWindows.Application.Abstractions;
using PhotosWindows.Application.Library;
using PhotosWindows.Domain;

namespace PhotosWindows.Application.People;

public sealed record FaceAnalysisProgress(int Done, int Total, int FacesFound, int Failed);

public sealed record FaceAnalysisResult(int Analyzed, int Failed, int FacesFound, bool Cancelled);

/// <summary>
/// People: opt-in, local face detection and grouping. Nothing runs until the user enables it for the library and
/// the models are installed. Photos are decoded at a reduced size, never modified; descriptors stay in the catalog.
/// Logs record counts and error types only: never faces, descriptors, boxes or file contents.
/// </summary>
public sealed class PeopleService
{
    public const string EnabledKey = "people.enabled";
    public const string ConsentKey = "people.consent_utc";

    /// <summary>Long side used to decode photos for analysis (enough for faces down to ~25 px in the photo).</summary>
    public const int AnalysisLongSide = 1600;

    private const int FaceThumbnailSize = 192;

    private readonly IPeopleRepository _people;
    private readonly IAssetRepository _assets;
    private readonly IImageRenderer _renderer;
    private readonly IFaceDetector? _detector;
    private readonly IFaceEmbedder? _embedder;
    private readonly ILibraryContext _context;
    private readonly IClock _clock;
    private readonly ISettingsStore _settings;
    private readonly ILogger<PeopleService> _logger;
    private readonly SemaphoreSlim _thumbnailGate = new(2);

    public PeopleService(IPeopleRepository people, IAssetRepository assets, IImageRenderer renderer, IEnumerable<IFaceDetector> detectors, IEnumerable<IFaceEmbedder> embedders,
        ILibraryContext context, IClock clock, ISettingsStore settings, ILogger<PeopleService> logger)
    {
        _people = people;
        _assets = assets;
        _renderer = renderer;
        _detector = detectors.FirstOrDefault();
        _embedder = embedders.FirstOrDefault();
        _context = context;
        _clock = clock;
        _settings = settings;
        _logger = logger;
    }

    /// <summary>Raised after groups, names or analysis results change.</summary>
    public event EventHandler? Changed;

    public ClusteringOptions Clustering { get; set; } = new();

    /// <summary>True when a detector and an embedder are installed and loadable.</summary>
    public bool ModelsReady => _detector?.IsReady == true && _embedder?.IsReady == true;

    /// <summary>Stored with every result: a different value means the photo must be analysed again.</summary>
    public string ModelVersion => _detector is null || _embedder is null ? "none" : $"{_detector.ModelId}+{_embedder.ModelId}";

    public async Task<bool> IsEnabledAsync(CancellationToken cancellationToken) =>
        _context.Current is not null && await _people.GetSettingAsync(EnabledKey, cancellationToken).ConfigureAwait(false) == "1";

    /// <summary>Enables People for the open library; the caller has shown the explanation and the user accepted it.</summary>
    public async Task EnableAsync(CancellationToken cancellationToken)
    {
        await _people.SetSettingAsync(EnabledKey, "1", cancellationToken).ConfigureAwait(false);
        await _people.SetSettingAsync(ConsentKey, _clock.UtcNow.ToString("O", System.Globalization.CultureInfo.InvariantCulture), cancellationToken).ConfigureAwait(false);
        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Stops using People for the library. Existing data is kept until the user deletes it.</summary>
    public async Task DisableAsync(CancellationToken cancellationToken)
    {
        await _people.SetSettingAsync(EnabledKey, null, cancellationToken).ConfigureAwait(false);
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public Task<FaceAnalysisStats> GetStatsAsync(CancellationToken cancellationToken) => _people.GetStatsAsync(ModelVersion, cancellationToken);

    /// <summary>
    /// Analyses photos without a result for the current models (all, or <paramref name="onlyAssets"/>), then groups the
    /// faces. Resumable: each photo's result is stored as soon as it is known, so a later call continues where this one
    /// stopped. Cancelling keeps what was done and still groups it.
    /// </summary>
    public async Task<FaceAnalysisResult> AnalyzeAsync(IReadOnlyCollection<long>? onlyAssets, IProgress<FaceAnalysisProgress>? progress, CancellationToken cancellationToken)
    {
        if (!ModelsReady) throw new InvalidOperationException("The face models are not installed.");
        if (!await IsEnabledAsync(cancellationToken).ConfigureAwait(false)) throw new InvalidOperationException("People is not enabled for this library.");

        var library = _context.RequireCurrent();
        var version = ModelVersion;
        var todo = await _people.GetAssetsToAnalyzeAsync(version, int.MaxValue, onlyAssets, cancellationToken).ConfigureAwait(false);
        var workers = Math.Clamp(_settings.Load().MaxBackgroundWorkers, 1, 2);
        int done = 0, failed = 0, faces = 0;
        var cancelled = false;
        progress?.Report(new FaceAnalysisProgress(0, todo.Count, 0, 0));
        _logger.LogInformation("Face analysis started: {Count} photo(s), {Workers} worker(s).", todo.Count, workers);

        try
        {
            await Parallel.ForEachAsync(todo, new ParallelOptions { MaxDegreeOfParallelism = workers, CancellationToken = cancellationToken }, async (assetId, ct) =>
            {
                var found = await AnalyzeOneAsync(library, assetId, version, ct).ConfigureAwait(false);
                if (found < 0) Interlocked.Increment(ref failed);
                else Interlocked.Add(ref faces, found);
                var d = Interlocked.Increment(ref done);
                progress?.Report(new FaceAnalysisProgress(d, todo.Count, Volatile.Read(ref faces), Volatile.Read(ref failed)));
            }).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            cancelled = true;
        }

        await RegroupAsync(CancellationToken.None).ConfigureAwait(false);
        _logger.LogInformation("Face analysis {Outcome}: {Done}/{Total} photo(s), {Faces} face(s), {Failed} failed.",
            cancelled ? "cancelled" : "finished", done, todo.Count, faces, failed);
        Changed?.Invoke(this, EventArgs.Empty);
        if (cancelled) cancellationToken.ThrowIfCancellationRequested();
        return new FaceAnalysisResult(done - failed, failed, faces, cancelled);
    }

    /// <summary>Returns the number of faces found, or -1 when the photo could not be analysed.</summary>
    private async Task<int> AnalyzeOneAsync(LibraryInfo library, long assetId, string version, CancellationToken cancellationToken)
    {
        try
        {
            var asset = await _assets.GetAsync(assetId, cancellationToken).ConfigureAwait(false);
            var file = await _assets.GetOriginalFileAsync(assetId, cancellationToken).ConfigureAwait(false);
            if (asset is null || file is null) return -1;
            var family = MediaFormats.FromExtension(file.StoredPath)?.Family ?? FormatFamily.Unknown;
            var image = await _renderer.DecodeOrientedAsync(library.Resolve(file), family, AnalysisLongSide, cancellationToken).ConfigureAwait(false);
            var detections = await _detector!.DetectAsync(image, cancellationToken).ConfigureAwait(false);
            var stored = new List<NewFace>(detections.Count);
            foreach (var detection in detections)
            {
                var embedding = await _embedder!.EmbedAsync(image, detection, cancellationToken).ConfigureAwait(false);
                stored.Add(new NewFace(detection.Box.Clamp(), detection.Score, FaceClustering.Normalize(embedding)));
            }

            await _people.SaveAnalysisAsync(assetId, version, FaceAnalysisState.Analyzed, stored, _clock.UtcNow, cancellationToken).ConfigureAwait(false);
            return stored.Count;
        }
        catch (Exception ex) when (ex is MediaDecodeException or IOException or UnauthorizedAccessException or InvalidDataException or NotSupportedException)
        {
            // The error type only: the message could contain a path.
            _logger.LogWarning("Face analysis failed for asset {AssetId}: {Error}.", assetId, ex.GetType().Name);
            await _people.SaveAnalysisAsync(assetId, version, FaceAnalysisState.Failed, [], _clock.UtcNow, CancellationToken.None).ConfigureAwait(false);
            return -1;
        }
    }

    /// <summary>Groups faces that are not in a group yet. User decisions are never changed.</summary>
    public async Task RegroupAsync(CancellationToken cancellationToken)
    {
        var vectors = await _people.GetVectorsAsync(ModelVersion, cancellationToken).ConfigureAwait(false);
        var plan = FaceClustering.Plan(vectors, Clustering);
        await _people.ApplyAssignmentsAsync(plan, _clock.UtcNow, cancellationToken).ConfigureAwait(false);
    }

    public Task<IReadOnlyList<Person>> GetPeopleAsync(CancellationToken cancellationToken) => _people.GetPeopleAsync(cancellationToken);

    public Task<Person?> GetPersonAsync(long personId, CancellationToken cancellationToken) => _people.GetPersonAsync(personId, cancellationToken);

    public Task<IReadOnlyList<Face>> GetFacesOfPersonAsync(long personId, CancellationToken cancellationToken) => _people.GetFacesOfPersonAsync(personId, cancellationToken);

    public Task<IReadOnlyList<Face>> GetUngroupedFacesAsync(int limit, CancellationToken cancellationToken) => _people.GetUngroupedFacesAsync(limit, cancellationToken);

    public Task<IReadOnlyList<Face>> GetFacesOfAssetAsync(long assetId, CancellationToken cancellationToken) => _people.GetFacesOfAssetAsync(assetId, cancellationToken);

    public Task<bool> IsExcludedAsync(long assetId, CancellationToken cancellationToken) => _people.IsExcludedAsync(assetId, cancellationToken);

    public Task RenameAsync(long personId, string? name, CancellationToken cancellationToken) =>
        NotifyAsync(_people.RenameAsync(personId, name, _clock.UtcNow, cancellationToken));

    public Task MergeAsync(IReadOnlyCollection<long> sourcePersonIds, long targetPersonId, CancellationToken cancellationToken) =>
        NotifyAsync(_people.MergeAsync(sourcePersonIds, targetPersonId, _clock.UtcNow, cancellationToken));

    /// <summary>Splits faces out of their group into another group (or a new one when <paramref name="targetPersonId"/> is null).</summary>
    public async Task<long> MoveFacesAsync(IReadOnlyCollection<long> faceIds, long? targetPersonId, CancellationToken cancellationToken)
    {
        var id = await _people.MoveFacesAsync(faceIds, targetPersonId, _clock.UtcNow, cancellationToken).ConfigureAwait(false);
        Changed?.Invoke(this, EventArgs.Empty);
        return id;
    }

    public Task IgnoreFacesAsync(IReadOnlyCollection<long> faceIds, CancellationToken cancellationToken) =>
        NotifyAsync(_people.IgnoreFacesAsync(faceIds, cancellationToken));

    public Task SetCoverAsync(long personId, long faceId, CancellationToken cancellationToken) =>
        NotifyAsync(_people.SetCoverAsync(personId, faceId, cancellationToken));

    /// <summary>Deletes the group with its faces and descriptors; photos are kept.</summary>
    public async Task DeletePersonAsync(long personId, CancellationToken cancellationToken)
    {
        var faces = await _people.GetFacesOfPersonAsync(personId, cancellationToken).ConfigureAwait(false);
        await _people.DeletePersonAsync(personId, cancellationToken).ConfigureAwait(false);
        DeleteFaceThumbnails(faces.Select(f => f.Id));
        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Excludes photos from People: their faces and descriptors are deleted and they are not analysed again.</summary>
    public async Task ExcludeAsync(IReadOnlyCollection<long> assetIds, CancellationToken cancellationToken)
    {
        var faceIds = new List<long>();
        foreach (var assetId in assetIds)
        {
            faceIds.AddRange((await _people.GetFacesOfAssetAsync(assetId, cancellationToken).ConfigureAwait(false)).Select(f => f.Id));
        }

        await _people.ExcludeAssetsAsync(assetIds, _clock.UtcNow, cancellationToken).ConfigureAwait(false);
        DeleteFaceThumbnails(faceIds);
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public Task IncludeAsync(IReadOnlyCollection<long> assetIds, CancellationToken cancellationToken) =>
        NotifyAsync(_people.IncludeAssetsAsync(assetIds, cancellationToken));

    /// <summary>Marks photos (all when null) to be analysed again; names and corrections are kept for the same faces.</summary>
    public Task ResetAnalysisAsync(IReadOnlyCollection<long>? assetIds, CancellationToken cancellationToken) =>
        NotifyAsync(_people.ResetAnalysisAsync(assetIds, cancellationToken));

    /// <summary>
    /// Deletes every face, descriptor, group, name and analysis result of the library, and the face thumbnails.
    /// Photos, albums and the rest of the catalog are not touched. People is disabled afterwards.
    /// </summary>
    public async Task DeleteAllDataAsync(CancellationToken cancellationToken)
    {
        await _people.DeleteAllAsync(cancellationToken).ConfigureAwait(false);
        await _people.SetSettingAsync(EnabledKey, null, cancellationToken).ConfigureAwait(false);
        var folder = FaceThumbnailFolder();
        if (folder is not null && Directory.Exists(folder)) Directory.Delete(folder, recursive: true);
        _logger.LogInformation("All face data of the library was deleted.");
        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>A square crop around the face, cached in the library's (disposable) cache folder.</summary>
    public async Task<string?> GetFaceThumbnailAsync(long faceId, CancellationToken cancellationToken)
    {
        var folder = FaceThumbnailFolder();
        if (folder is null) return null;
        var path = Path.Combine(folder, $"{faceId}.jpg");
        if (File.Exists(path)) return path;

        await _thumbnailGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (File.Exists(path)) return path;
            var face = await _people.GetFaceAsync(faceId, cancellationToken).ConfigureAwait(false);
            if (face is null) return null;
            var file = await _assets.GetOriginalFileAsync(face.AssetId, cancellationToken).ConfigureAwait(false);
            if (file is null) return null;
            var family = MediaFormats.FromExtension(file.StoredPath)?.Family ?? FormatFamily.Unknown;
            var image = await _renderer.DecodeOrientedAsync(_context.RequireCurrent().Resolve(file), family, 1024, cancellationToken).ConfigureAwait(false);
            var crop = CropSquare(image, face.Box, margin: 0.35f);
            var rendered = await _renderer.RenderAsync(crop, EditRecipe.Identity, FaceThumbnailSize, EncodeOptions.ThumbnailJpeg, cancellationToken).ConfigureAwait(false);
            Directory.CreateDirectory(folder);
            var temp = path + ".tmp";
            await File.WriteAllBytesAsync(temp, rendered.Encoded, cancellationToken).ConfigureAwait(false);
            File.Move(temp, path, overwrite: true);
            return path;
        }
        catch (Exception ex) when (ex is MediaDecodeException or IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning("Face thumbnail failed for face {FaceId}: {Error}.", faceId, ex.GetType().Name);
            return null;
        }
        finally
        {
            _thumbnailGate.Release();
        }
    }

    /// <summary>Copies a square region around <paramref name="box"/> (plus a margin), clipped to the image.</summary>
    public static RasterImage CropSquare(RasterImage image, FaceBox box, float margin)
    {
        var cx = (box.X + box.Width / 2) * image.Width;
        var cy = (box.Y + box.Height / 2) * image.Height;
        var side = Math.Max(box.Width * image.Width, box.Height * image.Height) * (1 + 2 * margin);
        side = Math.Clamp(side, 8, Math.Min(image.Width, image.Height));
        var x = (int)Math.Clamp(Math.Round(cx - side / 2), 0, image.Width - side);
        var y = (int)Math.Clamp(Math.Round(cy - side / 2), 0, image.Height - side);
        var size = (int)side;
        var pixels = new byte[size * size * 4];
        for (var row = 0; row < size; row++)
        {
            Buffer.BlockCopy(image.Pixels, ((y + row) * image.Width + x) * 4, pixels, row * size * 4, size * 4);
        }

        return new RasterImage(size, size, pixels);
    }

    private string? FaceThumbnailFolder() => _context.Current is { } library ? Path.Combine(library.CachePath, "faces") : null;

    private void DeleteFaceThumbnails(IEnumerable<long> faceIds)
    {
        var folder = FaceThumbnailFolder();
        if (folder is null) return;
        foreach (var id in faceIds)
        {
            var path = Path.Combine(folder, $"{id}.jpg");
            try
            {
                if (File.Exists(path)) File.Delete(path);
            }
            catch (IOException ex)
            {
                _logger.LogWarning("Could not delete a face thumbnail: {Error}.", ex.GetType().Name);
            }
        }
    }

    private async Task NotifyAsync(Task work)
    {
        await work.ConfigureAwait(false);
        Changed?.Invoke(this, EventArgs.Empty);
    }
}
