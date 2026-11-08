namespace PhotosWindows.Domain;

/// <summary>WGS84 coordinate. Invalid values are rejected rather than silently clamped.</summary>
public readonly record struct GeoLocation
{
    public GeoLocation(double latitude, double longitude, double? altitude = null)
    {
        if (double.IsNaN(latitude) || latitude < -90 || latitude > 90)
        {
            throw new ArgumentOutOfRangeException(nameof(latitude), latitude, "Latitude must be within [-90, 90].");
        }

        if (double.IsNaN(longitude) || longitude < -180 || longitude > 180)
        {
            throw new ArgumentOutOfRangeException(nameof(longitude), longitude, "Longitude must be within [-180, 180].");
        }

        Latitude = latitude;
        Longitude = longitude;
        Altitude = altitude is { } a && (double.IsNaN(a) || double.IsInfinity(a)) ? null : altitude;
    }

    public double Latitude { get; }
    public double Longitude { get; }
    public double? Altitude { get; }

    public static bool TryCreate(double latitude, double longitude, double? altitude, out GeoLocation location)
    {
        location = default;
        if (double.IsNaN(latitude) || double.IsNaN(longitude) || latitude is < -90 or > 90 || longitude is < -180 or > 180)
        {
            return false;
        }

        // (0,0) is almost always a placeholder written by devices without a fix.
        if (latitude == 0 && longitude == 0)
        {
            return false;
        }

        location = new GeoLocation(latitude, longitude, altitude);
        return true;
    }

    /// <summary>Great-circle distance in kilometres (haversine).</summary>
    public double DistanceKmTo(GeoLocation other)
    {
        const double earthRadiusKm = 6371.0088;
        static double Rad(double d) => d * Math.PI / 180.0;
        var dLat = Rad(other.Latitude - Latitude);
        var dLon = Rad(other.Longitude - Longitude);
        var a = Math.Sin(dLat / 2) * Math.Sin(dLat / 2) +
                Math.Cos(Rad(Latitude)) * Math.Cos(Rad(other.Latitude)) * Math.Sin(dLon / 2) * Math.Sin(dLon / 2);
        return 2 * earthRadiusKm * Math.Asin(Math.Min(1, Math.Sqrt(a)));
    }
}

/// <summary>
/// A latitude/longitude box. When <see cref="MinLon"/> &gt; <see cref="MaxLon"/> the box crosses the antimeridian
/// (180°): it spans from MinLon eastwards through 180° to MaxLon. Latitudes cannot wrap, so an inverted pair is
/// swapped. Edges are inclusive.
/// </summary>
public readonly record struct GeoBounds(double MinLat, double MinLon, double MaxLat, double MaxLon)
{
    public bool CrossesAntimeridian => MinLon > MaxLon;

    public static GeoBounds From(double minLat, double minLon, double maxLat, double maxLon)
    {
        Check(minLat, 90, nameof(minLat));
        Check(maxLat, 90, nameof(maxLat));
        Check(minLon, 180, nameof(minLon));
        Check(maxLon, 180, nameof(maxLon));
        return minLat <= maxLat ? new GeoBounds(minLat, minLon, maxLat, maxLon) : new GeoBounds(maxLat, minLon, minLat, maxLon);
    }

    public bool Contains(GeoLocation location) =>
        location.Latitude >= MinLat && location.Latitude <= MaxLat
        && (CrossesAntimeridian
            ? location.Longitude >= MinLon || location.Longitude <= MaxLon
            : location.Longitude >= MinLon && location.Longitude <= MaxLon);

    private static void Check(double value, double limit, string name)
    {
        if (double.IsNaN(value) || value < -limit || value > limit) throw new ArgumentOutOfRangeException(name, value, $"Must be within [-{limit}, {limit}].");
    }
}
