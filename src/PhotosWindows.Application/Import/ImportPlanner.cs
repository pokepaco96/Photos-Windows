using PhotosWindows.Domain;

namespace PhotosWindows.Application.Import;

/// <summary>A primary file plus the companion files that belong to the same shot.</summary>
public sealed record ImportUnit(string Primary, string? RawPair, string? LiveVideoCandidate, string? Sidecar)
{
    public IEnumerable<string> AllFiles()
    {
        yield return Primary;
        if (RawPair is not null) yield return RawPair;
        if (LiveVideoCandidate is not null) yield return LiveVideoCandidate;
        if (Sidecar is not null) yield return Sidecar;
    }
}

/// <summary>
/// Groups files of the same shot by folder + base name:
/// JPEG/HEIC + RAW → one asset (the processed image is primary, the RAW is attached);
/// still + short .MOV → candidate live photo (confirmed later by duration/content identifier);
/// IMG_1.xmp or IMG_1.CR2.xmp → sidecar of the matching file.
/// </summary>
public static class ImportPlanner
{
    private static readonly string[] PreferredPrimary = [".heic", ".heif", ".hif", ".jpg", ".jpeg", ".avif", ".png", ".tif", ".tiff", ".webp"];

    public static IReadOnlyList<ImportUnit> Plan(IEnumerable<string> files)
    {
        var units = new List<ImportUnit>();
        var groups = files
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .GroupBy(KeyOf, StringComparer.OrdinalIgnoreCase);

        foreach (var group in groups)
        {
            var members = group.ToList();
            var sidecars = members.Where(f => MediaFormats.FromExtension(f)?.Family == FormatFamily.Sidecar).ToList();
            var raws = members.Where(MediaFormats.IsRawExtension).ToList();
            var videos = members.Where(f => MediaFormats.FromExtension(f)?.MediaType == MediaType.Video).ToList();
            var stills = members.Where(f => MediaFormats.FromExtension(f) is { MediaType: MediaType.Photo } fmt && fmt.Family != FormatFamily.Raw).ToList();

            var processed = stills
                .OrderBy(f => Array.IndexOf(PreferredPrimary, Path.GetExtension(f).ToLowerInvariant()) is var i && i < 0 ? int.MaxValue : i)
                .ThenBy(f => f, StringComparer.OrdinalIgnoreCase)
                .ToList();

            string? primary;
            string? rawPair = null;
            var leftovers = new List<string>();
            if (processed.Count > 0)
            {
                primary = processed[0];
                leftovers.AddRange(processed.Skip(1));
                if (raws.Count > 0)
                {
                    rawPair = raws[0];
                    leftovers.AddRange(raws.Skip(1));
                }
            }
            else if (raws.Count > 0)
            {
                primary = raws[0];
                leftovers.AddRange(raws.Skip(1));
            }
            else
            {
                primary = null;
            }

            string? live = null;
            if (primary is not null && videos.Count > 0)
            {
                live = videos.FirstOrDefault(v => Path.GetExtension(v).Equals(".mov", StringComparison.OrdinalIgnoreCase));
                leftovers.AddRange(videos.Where(v => !ReferenceEquals(v, live)));
            }
            else
            {
                leftovers.AddRange(videos);
            }

            var sidecar = sidecars.FirstOrDefault();
            if (primary is not null)
            {
                units.Add(new ImportUnit(primary, rawPair, live, sidecar));
            }
            else if (sidecar is not null && leftovers.Count == 0)
            {
                // A lone sidecar is not importable on its own; it is reported as skipped by the caller.
            }

            foreach (var other in leftovers)
            {
                units.Add(new ImportUnit(other, null, null, null));
            }
        }

        return units;
    }

    /// <summary>Folder + base name; "IMG_1.CR2.xmp" and "IMG_1.xmp" both map to "IMG_1".</summary>
    internal static string KeyOf(string path)
    {
        var dir = Path.GetDirectoryName(path) ?? string.Empty;
        var name = Path.GetFileNameWithoutExtension(path);
        if (MediaFormats.FromExtension(path)?.Family == FormatFamily.Sidecar && MediaFormats.FromExtension(name) is not null)
        {
            name = Path.GetFileNameWithoutExtension(name);
        }

        return Path.Combine(dir, name);
    }
}
