namespace PhotosWindows.Domain;

/// <summary>A located asset, as loaded for the Places map (only what clustering needs).</summary>
public readonly record struct GeoPoint(long AssetId, double Latitude, double Longitude, DateTime CaptureLocal);

/// <summary>
/// Spherical Web Mercator in normalised world units: x and y in [0, 1), x = 0 at 180°W, y = 0 at the north edge.
/// Latitudes beyond ±<see cref="MaxLatitude"/> are clamped to the edge (the projection is infinite at the poles).
/// </summary>
public static class WebMercator
{
    public const double MaxLatitude = 85.05112878;

    public static (double X, double Y) ToWorld(double latitude, double longitude)
    {
        var lat = Math.Clamp(latitude, -MaxLatitude, MaxLatitude) * Math.PI / 180;
        var x = (longitude + 180) / 360;
        var y = (1 - Math.Log(Math.Tan(lat) + 1 / Math.Cos(lat)) / Math.PI) / 2;
        return (x, Math.Clamp(y, 0, 1));
    }

    public static (double Latitude, double Longitude) ToGeo(double x, double y)
    {
        var lon = WrapLongitude(x * 360 - 180);
        var n = Math.PI * (1 - 2 * Math.Clamp(y, 0, 1));
        var lat = Math.Atan(Math.Sinh(n)) * 180 / Math.PI;
        return (lat, lon);
    }

    /// <summary>Maps any longitude into [-180, 180].</summary>
    public static double WrapLongitude(double longitude)
    {
        if (longitude is >= -180 and <= 180) return longitude;
        var l = (longitude + 180) % 360;
        if (l < 0) l += 360;
        return l - 180;
    }

    /// <summary>Maps a world x into [0, 1).</summary>
    public static double WrapX(double x)
    {
        var w = x - Math.Floor(x);
        return w >= 1 ? 0 : w;
    }
}

/// <summary>
/// A map camera: the world point at the centre of the view, a zoom level (the world is 256·2^zoom pixels wide)
/// and the view size in pixels. The world repeats horizontally, so panning across the antimeridian is continuous.
/// </summary>
public sealed record MapView
{
    public const double MinZoom = 0;
    /// <summary>Highest zoom a view can hold (leaves room for high-DPI rendering of <see cref="MaxInteractiveZoom"/>).</summary>
    public const double MaxZoom = 12;

    /// <summary>Highest zoom the user can reach: the 1:50m basemap has no more detail beyond it.</summary>
    public const double MaxInteractiveZoom = 10;
    public const double TileSize = 256;

    public MapView(double centerX, double centerY, double zoom, int width, int height)
    {
        Zoom = Math.Clamp(double.IsFinite(zoom) ? zoom : 0, MinZoom, MaxZoom);
        Width = Math.Max(1, width);
        Height = Math.Max(1, height);
        CenterX = WebMercator.WrapX(double.IsFinite(centerX) ? centerX : 0.5);
        // Keep the world filling the view vertically whenever it is tall enough.
        var halfWorld = Height / 2.0 / WorldSize;
        CenterY = halfWorld >= 0.5 ? 0.5 : Math.Clamp(double.IsFinite(centerY) ? centerY : 0.5, halfWorld, 1 - halfWorld);
    }

    public double CenterX { get; }
    public double CenterY { get; }
    public double Zoom { get; }
    public int Width { get; }
    public int Height { get; }

    /// <summary>World width in pixels at this zoom.</summary>
    public double WorldSize => TileSize * Math.Pow(2, Zoom);

    public static MapView World(int width, int height) => new(0.5, 0.5, FitZoom(1, 1, width, height), width, height);

    /// <summary>Screen position of a world point, choosing the horizontal world copy nearest the centre.</summary>
    public (double X, double Y) ToScreen(double worldX, double worldY)
    {
        var dx = worldX - CenterX;
        dx -= Math.Round(dx); // nearest copy: dx in [-0.5, 0.5]
        return (Width / 2.0 + dx * WorldSize, Height / 2.0 + (worldY - CenterY) * WorldSize);
    }

    public (double X, double Y) Project(double latitude, double longitude)
    {
        var (x, y) = WebMercator.ToWorld(latitude, longitude);
        return ToScreen(x, y);
    }

    public (double WorldX, double WorldY) ToWorld(double screenX, double screenY) =>
        (CenterX + (screenX - Width / 2.0) / WorldSize, CenterY + (screenY - Height / 2.0) / WorldSize);

    public (double Latitude, double Longitude) ToGeo(double screenX, double screenY)
    {
        var (x, y) = ToWorld(screenX, screenY);
        return WebMercator.ToGeo(WebMercator.WrapX(x), y);
    }

    /// <summary>
    /// The smallest pan that brings a point at least <paramref name="margin"/> pixels inside the view; the same view
    /// when it already is. Zoom never changes. Used to keep the selected area visible when the map is resized.
    /// </summary>
    public MapView PanToInclude(double latitude, double longitude, double margin)
    {
        var (x, y) = Project(latitude, longitude);
        var mx = Math.Min(margin, Width / 2.0);
        var my = Math.Min(margin, Height / 2.0);
        var dx = x < mx ? mx - x : x > Width - mx ? Width - mx - x : 0;
        var dy = y < my ? my - y : y > Height - my ? Height - my - y : 0;
        return dx == 0 && dy == 0 ? this : PanBy(dx, dy);
    }

    public MapView PanBy(double dxPixels, double dyPixels) =>
        new(CenterX - dxPixels / WorldSize, CenterY - dyPixels / WorldSize, Zoom, Width, Height);

    public MapView Resize(int width, int height) => new(CenterX, CenterY, Zoom, width, height);

    /// <summary>The same view in device pixels (e.g. 1.5 for 150 % display scaling), for sharp rendering.</summary>
    public MapView Scaled(double factor)
    {
        if (!double.IsFinite(factor) || factor <= 0) factor = 1;
        return new MapView(CenterX, CenterY, Zoom + Math.Log2(factor), (int)Math.Round(Width * factor), (int)Math.Round(Height * factor));
    }

    /// <summary>Zooms keeping the world point under (<paramref name="screenX"/>, <paramref name="screenY"/>) fixed.</summary>
    public MapView ZoomAt(double delta, double screenX, double screenY)
    {
        var (wx, wy) = ToWorld(screenX, screenY);
        var zoom = Math.Clamp(Zoom + delta, MinZoom, Math.Max(Zoom, MaxInteractiveZoom));
        var size = TileSize * Math.Pow(2, zoom);
        return new MapView(wx - (screenX - Width / 2.0) / size, wy - (screenY - Height / 2.0) / size, zoom, Width, Height);
    }

    /// <summary>Geographic box currently visible. Its longitudes cross the antimeridian when the view does.</summary>
    public GeoBounds VisibleBounds
    {
        get
        {
            var (x0, y0) = ToWorld(0, 0);
            var (x1, y1) = ToWorld(Width, Height);
            var (north, _) = WebMercator.ToGeo(0, y0);
            var (south, _) = WebMercator.ToGeo(0, y1);
            if (x1 - x0 >= 1) return GeoBounds.From(south, -180, north, 180);
            var west = WebMercator.WrapX(x0) * 360 - 180;
            var east = WebMercator.WrapX(x1) * 360 - 180;
            if (east == -180 && x1 > x0) east = 180;
            return GeoBounds.From(south, west, north, east);
        }
    }

    /// <summary>The view that fits <paramref name="bounds"/> with a margin, or a world view for an empty set.</summary>
    public static MapView Fit(GeoBounds bounds, int width, int height, double paddingPixels = 48, double maxZoom = 8)
    {
        var (x0, y0) = WebMercator.ToWorld(bounds.MaxLat, bounds.MinLon);
        var (x1, y1) = WebMercator.ToWorld(bounds.MinLat, bounds.MaxLon);
        if (bounds.CrossesAntimeridian) x1 += 1;
        var w = Math.Max(1, width - 2 * paddingPixels);
        var h = Math.Max(1, height - 2 * paddingPixels);
        var zoom = Math.Min(FitZoom(x1 - x0, y1 - y0, w, h), maxZoom);
        return new MapView((x0 + x1) / 2, (y0 + y1) / 2, zoom, width, height);
    }

    private static double FitZoom(double worldWidth, double worldHeight, double pixelsWide, double pixelsHigh)
    {
        var sx = worldWidth <= 1e-12 ? double.PositiveInfinity : pixelsWide / (worldWidth * TileSize);
        var sy = worldHeight <= 1e-12 ? double.PositiveInfinity : pixelsHigh / (worldHeight * TileSize);
        var scale = Math.Min(sx, sy);
        return double.IsInfinity(scale) ? MaxZoom : Math.Clamp(Math.Log2(scale), MinZoom, MaxZoom);
    }
}

/// <summary>Photos grouped because they fall into the same screen cell at a zoom level.</summary>
public sealed record PlaceCluster(
    string Key,
    double Latitude,
    double Longitude,
    int Count,
    GeoBounds Bounds,
    long CoverAssetId,
    DateTime First,
    DateTime Last,
    IReadOnlyList<long> AssetIds);

/// <summary>
/// Grid clustering in projected (screen) space. Clusters are computed for an integer zoom level so they stay stable
/// while the user zooms smoothly between levels. O(n) in the number of points; deterministic output order.
/// </summary>
public static class PlaceClustering
{
    public const double DefaultCellPixels = 72;

    public static int LevelFor(double zoom) => (int)Math.Floor(Math.Clamp(zoom, MapView.MinZoom, MapView.MaxZoom) + 1e-9);

    public static IReadOnlyList<PlaceCluster> Cluster(IReadOnlyList<GeoPoint> points, int level, double cellPixels = DefaultCellPixels)
    {
        ArgumentNullException.ThrowIfNull(points);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(cellPixels);
        var cellsPerAxis = Math.Max(1L, (long)Math.Floor(MapView.TileSize * Math.Pow(2, Math.Clamp(level, 0, 30)) / cellPixels));
        var cells = new Dictionary<long, Accumulator>();
        foreach (var p in points)
        {
            var (x, y) = WebMercator.ToWorld(p.Latitude, p.Longitude);
            var cx = Math.Min(cellsPerAxis - 1, (long)Math.Floor(WebMercator.WrapX(x) * cellsPerAxis));
            var cy = Math.Min(cellsPerAxis - 1, (long)Math.Floor(y * cellsPerAxis));
            var key = cy * cellsPerAxis + cx;
            if (!cells.TryGetValue(key, out var acc))
            {
                acc = new Accumulator(level, cx, cy, x, p.Longitude);
                cells.Add(key, acc);
            }

            acc.Add(p, x, y);
        }

        var ordered = cells.ToArray();
        // Largest first, then by cell (row-major): deterministic whatever the input order.
        Array.Sort(ordered, static (a, b) => a.Value.Count != b.Value.Count ? b.Value.Count.CompareTo(a.Value.Count) : a.Key.CompareTo(b.Key));
        var result = new PlaceCluster[ordered.Length];
        for (var i = 0; i < ordered.Length; i++) result[i] = ordered[i].Value.ToCluster();
        return result;
    }

    private sealed class Accumulator(int level, long cx, long cy, double refX, double refLon)
    {
        private readonly List<long> _ids = new(1);

        public int Count => _ids.Count;
        private double _sumDx, _sumY;
        private double _minLat = double.MaxValue, _maxLat = double.MinValue;
        private double _minDLon = double.MaxValue, _maxDLon = double.MinValue;
        private DateTime _first = DateTime.MaxValue, _last = DateTime.MinValue;
        private long _cover;

        public void Add(GeoPoint p, double x, double y)
        {
            var dx = x - refX;
            dx -= Math.Round(dx);
            _sumDx += dx;
            _sumY += y;
            _minLat = Math.Min(_minLat, p.Latitude);
            _maxLat = Math.Max(_maxLat, p.Latitude);
            var dLon = p.Longitude - refLon;
            dLon -= 360 * Math.Round(dLon / 360);
            _minDLon = Math.Min(_minDLon, dLon);
            _maxDLon = Math.Max(_maxDLon, dLon);
            if (p.CaptureLocal < _first) _first = p.CaptureLocal;
            if (p.CaptureLocal > _last || (p.CaptureLocal == _last && p.AssetId > _cover))
            {
                _last = p.CaptureLocal;
                _cover = p.AssetId;
            }

            _ids.Add(p.AssetId);
        }

        public PlaceCluster ToCluster()
        {
            var n = _ids.Count;
            var (lat, lon) = WebMercator.ToGeo(WebMercator.WrapX(refX + _sumDx / n), _sumY / n);
            var west = WebMercator.WrapLongitude(refLon + _minDLon);
            var east = WebMercator.WrapLongitude(refLon + _maxDLon);
            var bounds = _maxDLon - _minDLon >= 360
                ? GeoBounds.From(_minLat, -180, _maxLat, 180)
                : GeoBounds.From(_minLat, west, _maxLat, east);
            return new PlaceCluster($"{level}/{cx}/{cy}", lat, lon, n, bounds, _cover, _first, _last, _ids);
        }
    }
}
