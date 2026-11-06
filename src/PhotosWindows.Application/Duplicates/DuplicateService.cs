using System.Numerics;
using PhotosWindows.Application.Abstractions;
using PhotosWindows.Application.Organization;
using PhotosWindows.Domain;

namespace PhotosWindows.Application.Duplicates;

public enum DuplicateKind
{
    /// <summary>Identical bytes (same SHA-256).</summary>
    Exact = 0,

    /// <summary>Visually similar (perceptual hash) — a suggestion only.</summary>
    Similar = 1,
}

public sealed record DuplicateGroup(DuplicateKind Kind, IReadOnlyList<Asset> Assets, int MaxDistance);

/// <summary>
/// Finds duplicates. Never deletes anything automatically: resolving a group moves the items the user did not keep
/// to Recently Deleted, from where they can still be recovered.
/// </summary>
public sealed class DuplicateService
{
    public const int DefaultSimilarityThreshold = 6;
    private readonly IAssetRepository _assets;
    private readonly IDuplicateDismissalRepository _dismissals;
    private readonly OrganizationService _organization;

    public DuplicateService(IAssetRepository assets, IDuplicateDismissalRepository dismissals, OrganizationService organization)
    {
        _assets = assets;
        _dismissals = dismissals;
        _organization = organization;
    }

    public async Task<IReadOnlyList<DuplicateGroup>> FindAsync(bool includeSimilar, int threshold, CancellationToken ct)
    {
        var dismissed = await _dismissals.GetDismissedAsync(ct).ConfigureAwait(false);
        var groups = new List<(DuplicateKind Kind, List<long> Ids, int Distance)>();
        var inExact = new HashSet<long>();

        foreach (var ids in await _assets.GetExactDuplicateGroupsAsync(ct).ConfigureAwait(false))
        {
            var filtered = RemoveDismissed(ids, dismissed);
            if (filtered.Count < 2) continue;
            groups.Add((DuplicateKind.Exact, filtered, 0));
            inExact.UnionWith(filtered);
        }

        if (includeSimilar)
        {
            var hashes = await _assets.GetPerceptualHashesAsync(ct).ConfigureAwait(false);
            foreach (var (ids, distance) in FindSimilarClusters(hashes, Math.Clamp(threshold, 0, 7), dismissed, ct))
            {
                if (ids.All(inExact.Contains)) continue;
                groups.Add((DuplicateKind.Similar, ids, distance));
            }
        }

        var result = new List<DuplicateGroup>();
        foreach (var g in groups)
        {
            var assets = await _assets.GetManyAsync(g.Ids, ct).ConfigureAwait(false);
            var visible = assets.Where(a => !a.IsInTrash).OrderBy(a => a.CaptureDate.LocalDateTime).ThenBy(a => a.Id).ToList();
            if (visible.Count >= 2) result.Add(new DuplicateGroup(g.Kind, visible, g.Distance));
        }

        return result;
    }

    /// <summary>
    /// Clusters 64-bit hashes whose Hamming distance is ≤ <paramref name="threshold"/> (≤ 7).
    /// Uses 8 bands of 8 bits: by the pigeonhole principle two hashes within distance 7 share at least one band,
    /// so only bucket-mates are compared instead of every pair.
    /// </summary>
    internal static IReadOnlyList<(List<long> Ids, int MaxDistance)> FindSimilarClusters(IReadOnlyList<(long Id, ulong Hash)> hashes, int threshold, IReadOnlySet<(long, long)> dismissed, CancellationToken ct)
    {
        var parent = hashes.ToDictionary(h => h.Id, h => h.Id);
        long Find(long x)
        {
            while (parent[x] != x)
            {
                parent[x] = parent[parent[x]];
                x = parent[x];
            }

            return x;
        }

        var maxDistance = new Dictionary<long, int>();
        var seen = new HashSet<(long, long)>();
        for (var band = 0; band < 8; band++)
        {
            ct.ThrowIfCancellationRequested();
            var buckets = hashes.GroupBy(h => (byte)(h.Hash >> (band * 8)));
            foreach (var bucket in buckets)
            {
                var items = bucket.ToList();
                if (items.Count < 2 || items.Count > 5000) continue; // degenerate buckets (e.g. blank images) are skipped
                for (var i = 0; i < items.Count; i++)
                {
                    for (var j = i + 1; j < items.Count; j++)
                    {
                        var a = Math.Min(items[i].Id, items[j].Id);
                        var b = Math.Max(items[i].Id, items[j].Id);
                        if (!seen.Add((a, b)) || dismissed.Contains((a, b))) continue;
                        var d = BitOperations.PopCount(items[i].Hash ^ items[j].Hash);
                        if (d > threshold) continue;
                        var ra = Find(a);
                        var rb = Find(b);
                        var md = Math.Max(d, Math.Max(maxDistance.GetValueOrDefault(ra), maxDistance.GetValueOrDefault(rb)));
                        if (ra != rb) parent[rb] = ra;
                        maxDistance[Find(a)] = md;
                    }
                }
            }
        }

        return hashes.GroupBy(h => Find(h.Id))
            .Where(g => g.Count() > 1)
            .Select(g => (g.Select(x => x.Id).OrderBy(x => x).ToList(), maxDistance.GetValueOrDefault(g.Key)))
            .ToList();
    }

    /// <summary>Keeps <paramref name="keepId"/> and moves the other members to Recently Deleted.</summary>
    public async Task ResolveAsync(DuplicateGroup group, long keepId, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(group);
        if (group.Assets.All(a => a.Id != keepId)) throw new ArgumentException("The item to keep must belong to the group.", nameof(keepId));
        var others = group.Assets.Where(a => a.Id != keepId).Select(a => a.Id).ToList();
        await _organization.MoveToTrashAsync(others, ct).ConfigureAwait(false);
    }

    /// <summary>Marks every pair in the group as "not duplicates" so it is not suggested again.</summary>
    public async Task DismissAsync(DuplicateGroup group, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(group);
        for (var i = 0; i < group.Assets.Count; i++)
        {
            for (var j = i + 1; j < group.Assets.Count; j++)
            {
                await _dismissals.DismissAsync(group.Assets[i].Id, group.Assets[j].Id, ct).ConfigureAwait(false);
            }
        }
    }

    private static List<long> RemoveDismissed(IReadOnlyList<long> ids, IReadOnlySet<(long, long)> dismissed)
    {
        // An exact group is dismissed when all its pairs were dismissed.
        var allDismissed = true;
        for (var i = 0; i < ids.Count && allDismissed; i++)
        {
            for (var j = i + 1; j < ids.Count; j++)
            {
                if (!dismissed.Contains((Math.Min(ids[i], ids[j]), Math.Max(ids[i], ids[j])))) { allDismissed = false; break; }
            }
        }

        return allDismissed ? [] : ids.ToList();
    }
}
