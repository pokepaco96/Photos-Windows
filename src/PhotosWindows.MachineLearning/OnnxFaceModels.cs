using Microsoft.Extensions.Logging;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using PhotosWindows.Application.Abstractions;
using PhotosWindows.Domain;

namespace PhotosWindows.MachineLearning;

/// <summary>
/// Loads an ONNX model only when its file is installed and verified, and reloads it after an install/uninstall.
/// CPU only, a fixed small thread count, no profiling. Sessions are thread-safe for Run.
/// </summary>
internal sealed class ModelSession : IDisposable
{
    private readonly FaceModelManager _models;
    private readonly ModelDescriptor _descriptor;
    private readonly ILogger _logger;
    private readonly Lock _gate = new();
    private InferenceSession? _session;

    public ModelSession(FaceModelManager models, ModelDescriptor descriptor, ILogger logger)
    {
        _models = models;
        _descriptor = descriptor;
        _logger = logger;
        _models.Changed += (_, _) => Reset();
    }

    public bool IsAvailable => _models.GetStatus(_descriptor).State == ModelState.Installed;

    public InferenceSession Get()
    {
        lock (_gate)
        {
            if (_session is not null) return _session;
            var status = _models.GetStatus(_descriptor);
            if (status.State != ModelState.Installed || status.Path is null)
            {
                throw new InvalidOperationException($"The model {_descriptor.Id} is not installed.");
            }

            using var options = new SessionOptions
            {
                GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL,
                IntraOpNumThreads = Math.Clamp(Environment.ProcessorCount / 2, 1, 4),
                InterOpNumThreads = 1,
                LogSeverityLevel = OrtLoggingLevel.ORT_LOGGING_LEVEL_ERROR,
            };
            _session = new InferenceSession(status.Path, options);
            _logger.LogInformation("Model {Model} loaded.", _descriptor.Id);
            return _session;
        }
    }

    public void Reset()
    {
        lock (_gate)
        {
            _session?.Dispose();
            _session = null;
        }
    }

    public void Dispose() => Reset();
}

/// <summary>
/// YuNet face detector (MIT, OpenCV Model Zoo). The model has a fixed 640×640 input; the photo is analysed at two
/// scales (longest side 640 and 320) so both small and very large faces are found, and the results are merged.
/// </summary>
public sealed class YuNetFaceDetector : IFaceDetector, IDisposable
{
    private const int Input = 640;
    private static readonly int[] Strides = [8, 16, 32];
    private readonly ModelSession _session;

    public YuNetFaceDetector(FaceModelManager models, ILogger<YuNetFaceDetector> logger) =>
        _session = new ModelSession(models, FaceModelCatalog.YuNet, logger);

    public string ModelId => FaceModelCatalog.YuNet.Id;

    public bool IsReady => _session.IsAvailable;

    /// <summary>Faces below this confidence are dropped (the model's own demo uses 0.9; real photos benefit from a little lower).</summary>
    public float ScoreThreshold { get; set; } = 0.8f;

    /// <summary>Smallest face kept, in pixels of the analysed image.</summary>
    public int MinFaceSize { get; set; } = 16;

    public Task<IReadOnlyList<FaceDetection>> DetectAsync(RasterImage image, CancellationToken cancellationToken) =>
        Task.Run<IReadOnlyList<FaceDetection>>(() =>
        {
            var candidates = new List<Candidate>();
            foreach (var longSide in new[] { Input, Input / 2 })
            {
                cancellationToken.ThrowIfCancellationRequested();
                candidates.AddRange(Run(image, longSide));
            }

            return Nms(candidates, 0.3f)
                .Where(c => c.Score >= ScoreThreshold && Math.Min(c.W, c.H) >= MinFaceSize)
                .Select(c => new FaceDetection(
                    // Faces cut by the photo's edge extend beyond it: keep the visible part. Landmarks may stay outside
                    // (alignment needs them where the model put them).
                    new FaceBox(c.X / image.Width, c.Y / image.Height, c.W / image.Width, c.H / image.Height).Clamp(),
                    c.Score,
                    c.Landmarks.Select(p => new FacePoint(p.X / image.Width, p.Y / image.Height)).ToList()))
                .ToList();
        }, cancellationToken);

    public void Dispose() => _session.Dispose();

    private List<Candidate> Run(RasterImage image, int longSide)
    {
        var (tensor, scale) = ImageTensors.Letterbox(image, longSide, Input);
        var session = _session.Get();
        var input = new DenseTensor<float>(tensor, [1, 3, Input, Input]);
        using var results = session.Run([NamedOnnxValue.CreateFromTensor(session.InputMetadata.Keys.First(), input)]);
        var outputs = results.ToDictionary(r => r.Name, r => r.AsTensor<float>().ToArray());
        var found = new List<Candidate>();
        foreach (var stride in Strides)
        {
            var cols = Input / stride;
            var cls = outputs[$"cls_{stride}"];
            var obj = outputs[$"obj_{stride}"];
            var box = outputs[$"bbox_{stride}"];
            var kps = outputs[$"kps_{stride}"];
            for (var i = 0; i < cls.Length; i++)
            {
                var score = MathF.Sqrt(Math.Clamp(cls[i], 0, 1) * Math.Clamp(obj[i], 0, 1));
                if (score < 0.6f) continue;
                int row = i / cols, col = i % cols;
                var cx = (col + box[i * 4]) * stride;
                var cy = (row + box[i * 4 + 1]) * stride;
                var w = MathF.Exp(box[i * 4 + 2]) * stride;
                var h = MathF.Exp(box[i * 4 + 3]) * stride;
                var landmarks = new (float X, float Y)[5];
                for (var k = 0; k < 5; k++)
                {
                    landmarks[k] = ((kps[i * 10 + 2 * k] + col) * stride / scale, (kps[i * 10 + 2 * k + 1] + row) * stride / scale);
                }

                found.Add(new Candidate((cx - w / 2) / scale, (cy - h / 2) / scale, w / scale, h / scale, score, landmarks));
            }
        }

        return found;
    }

    private static List<Candidate> Nms(List<Candidate> candidates, float threshold)
    {
        var kept = new List<Candidate>();
        foreach (var c in candidates.OrderByDescending(c => c.Score))
        {
            if (kept.All(k => IoU(k, c) <= threshold)) kept.Add(c);
        }

        return kept;
    }

    private static float IoU(Candidate a, Candidate b)
    {
        var x1 = Math.Max(a.X, b.X);
        var y1 = Math.Max(a.Y, b.Y);
        var x2 = Math.Min(a.X + a.W, b.X + b.W);
        var y2 = Math.Min(a.Y + a.H, b.Y + b.H);
        var inter = Math.Max(0, x2 - x1) * Math.Max(0, y2 - y1);
        var union = a.W * a.H + b.W * b.H - inter;
        return union <= 0 ? 0 : inter / union;
    }

    private sealed record Candidate(float X, float Y, float W, float H, float Score, (float X, float Y)[] Landmarks);
}

/// <summary>
/// SFace descriptor (Apache-2.0, OpenCV Model Zoo): the face is aligned on five points to a 112×112 crop (the
/// reference template of the model) and turned into 128 values used only to group faces.
/// </summary>
public sealed class SFaceEmbedder : IFaceEmbedder, IDisposable
{
    private const int Size = 112;

    /// <summary>Reference positions of eyes, nose and mouth corners in the 112×112 crop the model was trained on.</summary>
    private static readonly (double X, double Y)[] Template =
        [(38.2946, 51.6963), (73.5318, 51.5014), (56.0252, 71.7366), (41.5493, 92.3655), (70.7299, 92.2041)];

    private readonly ModelSession _session;

    public SFaceEmbedder(FaceModelManager models, ILogger<SFaceEmbedder> logger) =>
        _session = new ModelSession(models, FaceModelCatalog.SFace, logger);

    public string ModelId => FaceModelCatalog.SFace.Id;

    public bool IsReady => _session.IsAvailable;

    public int Dimensions => 128;

    public Task<float[]> EmbedAsync(RasterImage image, FaceDetection face, CancellationToken cancellationToken) =>
        Task.Run(() =>
        {
            if (face.Landmarks.Count != 5) throw new ArgumentException("SFace needs the five face landmarks.", nameof(face));
            var points = face.Landmarks.Select(p => ((double)p.X * image.Width, (double)p.Y * image.Height)).ToList();
            var transform = ImageTensors.Similarity(points, Template);
            var tensor = ImageTensors.WarpRgb(image, transform, Size);
            cancellationToken.ThrowIfCancellationRequested();
            var session = _session.Get();
            var input = new DenseTensor<float>(tensor, [1, 3, Size, Size]);
            using var results = session.Run([NamedOnnxValue.CreateFromTensor(session.InputMetadata.Keys.First(), input)]);
            var vector = results.First().AsTensor<float>().ToArray();
            var norm = MathF.Sqrt(vector.Sum(v => v * v));
            if (norm > 0)
            {
                for (var i = 0; i < vector.Length; i++) vector[i] /= norm;
            }

            return vector;
        }, cancellationToken);

    public void Dispose() => _session.Dispose();
}
