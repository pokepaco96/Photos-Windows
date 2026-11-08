using PhotosWindows.Application.Abstractions;
using PhotosWindows.Application.Library;
using PhotosWindows.Domain;

namespace PhotosWindows.Application.Places;

/// <summary>Filters of the Places page. <see cref="To"/> is an inclusive calendar day.</summary>
public sealed record PlacesFilter
{
    public DateTime? From { get; init; }
    public DateTime? To { get; init; }
    public MediaType? MediaType { get; init; }
    public long? AlbumId { get; init; }
    public bool FavoritesOnly { get; init; }

    public bool IsEmpty => From is null && To is null && MediaType is null && AlbumId is null && !FavoritesOnly;

    public static PlacesFilter FromPreferences(PlacesPreferences p) =>
        new() { From = p.From, To = p.To, MediaType = p.MediaType, AlbumId = p.AlbumId, FavoritesOnly = p.FavoritesOnly };

    public PlacesPreferences ApplyTo(PlacesPreferences p) =>
        p with { From = From, To = To, MediaType = MediaType, AlbumId = AlbumId, FavoritesOnly = FavoritesOnly };

    /// <summary>The catalog query for these filters (visible assets only: hidden and deleted are excluded).</summary>
    public AssetQuery ToQuery()
    {
        DateTime? from = From?.Date, to = To?.Date.AddDays(1);
        if (from is { } f && to is { } t && f >= t)
        {
            // Inverted range typed by the user: treat it as the same range the other way round.
            (from, to) = (t.AddDays(-1), f.AddDays(1));
        }

        return new AssetQuery
        {
            Scope = LibraryScope.All,
            From = from,
            To = to,
            MediaType = MediaType,
            AlbumId = AlbumId,
            Favorite = FavoritesOnly ? true : null,
            Sort = AssetSort.CaptureDateDescending,
        };
    }
}

/// <summary>Everything the Places page shows for one filter set.</summary>
public sealed class PlacesSnapshot
{
    private readonly Dictionary<int, IReadOnlyList<PlaceCluster>> _byLevel = [];
    private readonly Lock _lock = new();

    public PlacesSnapshot(PlacesFilter filter, IReadOnlyList<GeoPoint> points, int withoutLocation)
    {
        Filter = filter;
        Points = points;
        WithoutLocationCount = withoutLocation;
        Bounds = GeoBounds.Enclosing(points.Select(p => (p.Latitude, p.Longitude)));
    }

    public PlacesFilter Filter { get; }
    public IReadOnlyList<GeoPoint> Points { get; }

    /// <summary>Assets matching the same filters that have no location (shown as a hint, never on the map).</summary>
    public int WithoutLocationCount { get; }

    public GeoBounds? Bounds { get; }

    /// <summary>Clusters for an integer zoom level, computed once per level and cached.</summary>
    public IReadOnlyList<PlaceCluster> ClustersAt(int level)
    {
        lock (_lock)
        {
            if (!_byLevel.TryGetValue(level, out var clusters))
            {
                clusters = PlaceClustering.Cluster(Points, level);
                _byLevel[level] = clusters;
            }

            return clusters;
        }
    }

    /// <summary>Groups for list mode: region-sized clusters (level <see cref="PlacesService.ListLevel"/>), newest first.</summary>
    public IReadOnlyList<PlaceCluster> ListGroups() =>
        ClustersAt(PlacesService.ListLevel).OrderByDescending(c => c.Last).ThenBy(c => c.Key, StringComparer.Ordinal).ToList();
}

/// <summary>
/// Places: loads located assets for the filters, clusters them and edits catalog locations.
/// Fully offline: no geocoding, no tiles, no network. Original files are never written.
/// </summary>
public sealed class PlacesService
{
    /// <summary>Clustering level for list mode (cells of ~70 px at zoom 6 ≈ 1.5° of longitude).</summary>
    public const int ListLevel = 6;

    private readonly IAssetRepository _assets;

    public PlacesService(IAssetRepository assets) => _assets = assets;

    public async Task<PlacesSnapshot> LoadAsync(PlacesFilter filter, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(filter);
        var query = filter.ToQuery();
        var points = await _assets.QueryGeoPointsAsync(query, cancellationToken).ConfigureAwait(false);
        var without = await _assets.CountAsync(query with { HasLocation = false }, cancellationToken).ConfigureAwait(false);
        return new PlacesSnapshot(filter, points, without);
    }

    public Task RemoveLocationAsync(IReadOnlyCollection<long> ids, CancellationToken cancellationToken) =>
        ids.Count == 0 ? Task.CompletedTask : _assets.SetLocationAsync(ids, null, cancellationToken);

    public Task SetLocationAsync(IReadOnlyCollection<long> ids, GeoLocation location, CancellationToken cancellationToken) =>
        ids.Count == 0 ? Task.CompletedTask : _assets.SetLocationAsync(ids, location, cancellationToken);

    /// <summary>"40.42° N, 3.70° W" — invariant, language-neutral label for a coordinate (no geocoding).</summary>
    public static string FormatCoordinate(double latitude, double longitude) =>
        string.Create(System.Globalization.CultureInfo.InvariantCulture,
            $"{Math.Abs(latitude):0.00}° {(latitude >= 0 ? 'N' : 'S')}, {Math.Abs(longitude):0.00}° {(longitude >= 0 ? 'E' : 'W')}");
}
