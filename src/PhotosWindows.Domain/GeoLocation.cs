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
