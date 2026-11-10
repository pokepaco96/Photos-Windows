namespace PhotosWindows.Domain;

public enum MemoryKind
{
    OnThisDay = 0,
    Trip = 1,
    Event = 2,
    Month = 3,
    Season = 4,
    Year = 5,
    Person = 6,
}

public enum SeasonOfYear
{
    Winter = 0,
    Spring = 1,
    Summer = 2,
    Autumn = 3,
}

/// <summary>What the memory rules need to know about an asset. Comes from the catalog only; files are never read.</summary>
public readonly record struct MemoryItem(long AssetId, DateTime CaptureLocal, double? Latitude, double? Longitude, bool IsFavorite, bool IsEdited, bool IsVideo)
{
    public bool HasLocation => Latitude is not null && Longitude is not null;
}

/// <summary>A person the user named in People. Only supplied when People is enabled (with consent) for the library.</summary>
public sealed record MemoryPerson(long PersonId, string Name, IReadOnlyList<long> AssetIds);

/// <summary>Thresholds of the memory rules. Every value is documented in docs/MEMORIES.md.</summary>
public sealed record MemoryRules
{
    /// <summary>"On this day" also takes photos this many days before/after the date.</summary>
    public int OnThisDayWindowDays { get; init; }
    public int OnThisDayMinItems { get; init; } = 3;

    /// <summary>A pause longer than this starts a new event.</summary>
    public double EventGapHours { get; init; } = 4;
    /// <summary>Two consecutive located photos farther apart than this belong to different events.</summary>
    public double EventRadiusKm { get; init; } = 5;
    public int EventMinItems { get; init; } = 8;
    public double EventMaxHours { get; init; } = 36;

    /// <summary>Distance from the inferred usual area beyond which a day counts as "away".</summary>
    public double TripMinDistanceKm { get; init; } = 100;
    public int TripMinDays { get; init; } = 2;
    public int TripMinItems { get; init; } = 10;
    /// <summary>Days without located photos tolerated inside a trip.</summary>
    public int TripMaxGapDays { get; init; } = 2;
    /// <summary>Located days needed before a usual area is inferred at all.</summary>
    public int HomeMinDays { get; init; } = 10;
    /// <summary>Share of located days the usual area must hold; below it no trips are suggested.</summary>
    public double HomeMinShare { get; init; } = 0.4;

    public int MonthMinItems { get; init; } = 20;
    public int SeasonMinItems { get; init; } = 30;
    public int YearMinItems { get; init; } = 40;
    public int YearMinMonths { get; init; } = 3;
    public int PersonMinItems { get; init; } = 10;

    /// <summary>Largest number of items shown in one memory; bigger groups are sampled across their days.</summary>
    public int MaxItemsPerMemory { get; init; } = 80;
    /// <summary>Photos closer than this to the previously chosen one are treated as a burst and skipped when sampling.</summary>
    public double BurstSeconds { get; init; } = 2;
    /// <summary>Favourites and edited photos are chosen first for covers and samples. The user can turn it off.</summary>
    public bool PreferFavorites { get; init; } = true;

    public int MaxMemories { get; init; } = 24;
    /// <summary>Two memories sharing more than this share of the smaller one are considered the same.</summary>
    public double MaxOverlap { get; init; } = 0.5;
}

/// <summary>The facts behind a memory, so the interface can say exactly why it is shown. No place names: coordinates only.</summary>
public sealed record MemoryExplanation
{
    public required MemoryKind Kind { get; init; }
    /// <summary>Items shown in the memory.</summary>
    public required int Count { get; init; }
    /// <summary>Items that matched the rule (≥ <see cref="Count"/> when the memory is a sample).</summary>
    public required int Matched { get; init; }
    public required DateTime First { get; init; }
    public required DateTime Last { get; init; }
    public int? YearsAgo { get; init; }
    public int? Days { get; init; }
    /// <summary>Approximate centre of the located items, when at least half of them have a location.</summary>
    public double? Latitude { get; init; }
    public double? Longitude { get; init; }
    public double? DistanceFromUsualAreaKm { get; init; }
    /// <summary>Null when the hemisphere could not be established from the photos' own coordinates.</summary>
    public SeasonOfYear? Season { get; init; }
    public string? PersonName { get; init; }
    public bool IsSample => Matched > Count;
}

public sealed record MemoryCandidate(
    string Key,
    MemoryKind Kind,
    DateTime Start,
    DateTime End,
    IReadOnlyList<long> AssetIds,
    long CoverAssetId,
    MemoryExplanation Why)
{
    public double Score { get; init; }
    public double FavoriteShare { get; init; }
}

public static class Geo
{
    private const double EarthRadiusKm = 6371.0088;

    /// <summary>Great-circle distance (haversine). Correct across the antimeridian.</summary>
    public static double DistanceKm(double lat1, double lon1, double lat2, double lon2)
    {
        var p1 = lat1 * Math.PI / 180;
        var p2 = lat2 * Math.PI / 180;
        var dp = p2 - p1;
        var dl = (lon2 - lon1) * Math.PI / 180;
        var a = Math.Sin(dp / 2) * Math.Sin(dp / 2) + Math.Cos(p1) * Math.Cos(p2) * Math.Sin(dl / 2) * Math.Sin(dl / 2);
        return 2 * EarthRadiusKm * Math.Asin(Math.Min(1, Math.Sqrt(a)));
    }

    /// <summary>Mean position on the sphere (so 179°E and 179°W average to 180°, not 0°). Null for an empty set.</summary>
    public static (double Latitude, double Longitude)? Center(IEnumerable<(double Latitude, double Longitude)> points)
    {
        double x = 0, y = 0, z = 0;
        var n = 0;
        foreach (var (lat, lon) in points)
        {
            var p = lat * Math.PI / 180;
            var l = lon * Math.PI / 180;
            x += Math.Cos(p) * Math.Cos(l);
            y += Math.Cos(p) * Math.Sin(l);
            z += Math.Sin(p);
            n++;
        }

        if (n == 0) return null;
        var hyp = Math.Sqrt(x * x + y * y);
        if (hyp < 1e-12 && Math.Abs(z) < 1e-12) return null; // antipodal points: no meaningful centre
        return (Math.Atan2(z, hyp) * 180 / Math.PI, Math.Atan2(y, x) * 180 / Math.PI);
    }
}

/// <summary>A memory the user chose not to see again. Only its key and dates are kept, never its photos.</summary>
public sealed record HiddenMemory(string Key, MemoryKind Kind, DateTime Start, DateTime End, DateTime HiddenUtc);

/// <summary>Either a whole kind of memory, or a range of days (inclusive) whose photos must never be used.</summary>
public sealed record MemoryExclusion(long Id, MemoryKind? Kind, DateTime? From, DateTime? To)
{
    public bool Covers(DateTime captureLocal) => From is { } f && To is { } t && captureLocal >= f.Date && captureLocal < t.Date.AddDays(1);
}
