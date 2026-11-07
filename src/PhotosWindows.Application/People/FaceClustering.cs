using PhotosWindows.Application.Abstractions;
using PhotosWindows.Domain;

namespace PhotosWindows.Application.People;

/// <summary>
/// Thresholds for grouping. Descriptors are L2-normalized, so similarity is the cosine (dot product), from -1 to 1.
/// The defaults are stricter than the model's verification threshold (0.363 for SFace): it is easier for the user to
/// merge two groups of the same person than to clean a group that mixes two people.
/// </summary>
public sealed record ClusteringOptions(float JoinExistingThreshold = 0.42f, float NewGroupThreshold = 0.45f, int MinNewGroupSize = 2);

/// <summary>
/// Deterministic grouping of faces. Only faces that are suggestions and not in a group are moved: confirmed or ignored
/// faces, and faces already in a group, are never changed here.
/// </summary>
public static class FaceClustering
{
    public static float Similarity(ReadOnlySpan<float> a, ReadOnlySpan<float> b)
    {
        if (a.Length != b.Length) throw new ArgumentException("Descriptors have different sizes.");
        var dot = 0f;
        for (var i = 0; i < a.Length; i++) dot += a[i] * b[i];
        return dot;
    }

    public static float[] Normalize(float[] vector)
    {
        var norm = MathF.Sqrt(Similarity(vector, vector));
        if (norm <= 0 || float.IsNaN(norm)) return vector;
        var result = new float[vector.Length];
        for (var i = 0; i < vector.Length; i++) result[i] = vector[i] / norm;
        return result;
    }

    public static IReadOnlyList<FaceAssignment> Plan(IReadOnlyList<FaceVector> faces, ClusteringOptions? options = null)
    {
        options ??= new ClusteringOptions();
        var assignments = new List<FaceAssignment>();
        if (faces.Count == 0) return assignments;

        // 1. Centroids of existing groups (every face in the group counts, suggested or confirmed).
        var groups = faces
            .Where(f => f.PersonId is not null && f.Review != FaceReview.Ignored)
            .GroupBy(f => f.PersonId!.Value)
            .OrderBy(g => g.Key)
            .Select(g => new Centroid(g.Key, g.Select(f => f.Embedding)))
            .ToList();

        var pending = faces.Where(f => f.PersonId is null && f.Review == FaceReview.Suggested).OrderBy(f => f.FaceId).ToList();
        var remaining = new List<FaceVector>();

        // 2. Join the most similar existing group when similar enough.
        foreach (var face in pending)
        {
            var best = Best(groups, face.Embedding);
            if (best.Centroid is not null && best.Similarity >= options.JoinExistingThreshold)
            {
                assignments.Add(new FaceAssignment(face.FaceId, best.Centroid.PersonId, null));
                best.Centroid.Add(face.Embedding);
            }
            else
            {
                remaining.Add(face);
            }
        }

        // 3. Group the rest among themselves; small clusters stay ungrouped until more photos arrive.
        var clusters = new List<Centroid>();
        var members = new Dictionary<Centroid, List<long>>();
        foreach (var face in remaining)
        {
            var best = Best(clusters, face.Embedding);
            if (best.Centroid is not null && best.Similarity >= options.NewGroupThreshold)
            {
                best.Centroid.Add(face.Embedding);
                members[best.Centroid].Add(face.FaceId);
            }
            else
            {
                var cluster = new Centroid(null, [face.Embedding]);
                clusters.Add(cluster);
                members[cluster] = [face.FaceId];
            }
        }

        var key = 0;
        foreach (var cluster in clusters.Where(c => members[c].Count >= Math.Max(1, options.MinNewGroupSize)))
        {
            key++;
            assignments.AddRange(members[cluster].Select(id => new FaceAssignment(id, null, key)));
        }

        return assignments;
    }

    private static (Centroid? Centroid, float Similarity) Best(List<Centroid> centroids, float[] embedding)
    {
        Centroid? best = null;
        var bestSimilarity = float.MinValue;
        foreach (var centroid in centroids)
        {
            var similarity = Similarity(centroid.Vector, embedding);
            if (similarity > bestSimilarity)
            {
                best = centroid;
                bestSimilarity = similarity;
            }
        }

        return (best, bestSimilarity);
    }

    /// <summary>Running mean of the members, kept normalized.</summary>
    private sealed class Centroid
    {
        private readonly float[] _sum;
        private int _count;

        public Centroid(long? personId, IEnumerable<float[]> embeddings)
        {
            PersonId = personId;
            foreach (var e in embeddings)
            {
                _sum ??= new float[e.Length];
                Add(e);
            }

            _sum ??= [];
        }

        public long? PersonId { get; }

        public float[] Vector { get; private set; } = [];

        public void Add(float[] embedding)
        {
            for (var i = 0; i < embedding.Length; i++) _sum[i] += embedding[i];
            _count++;
            Vector = Normalize(_sum);
        }

        public override string ToString() => $"{PersonId?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "new"} ({_count})";
    }
}
