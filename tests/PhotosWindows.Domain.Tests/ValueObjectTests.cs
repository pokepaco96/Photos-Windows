using PhotosWindows.Domain;

namespace PhotosWindows.Domain.Tests;

public class ValueObjectTests
{
    [Fact]
    public void GeoLocation_rejects_invalid_and_placeholder_coordinates()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new GeoLocation(91, 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => new GeoLocation(0, 181));
        Assert.False(GeoLocation.TryCreate(0, 0, null, out _));
        Assert.False(GeoLocation.TryCreate(double.NaN, 3, null, out _));
        Assert.True(GeoLocation.TryCreate(40.4168, -3.7038, 650, out var madrid));
        Assert.Equal(650, madrid.Altitude);
    }

    [Fact]
    public void Haversine_distance_is_reasonable()
    {
        var madrid = new GeoLocation(40.4168, -3.7038);
        var barcelona = new GeoLocation(41.3874, 2.1686);
        Assert.InRange(madrid.DistanceKmTo(barcelona), 495, 510);
    }

    [Fact]
    public void CaptureDate_from_utc_uses_zone_offset()
    {
        var zone = TimeZoneInfo.CreateCustomTimeZone("plus2", TimeSpan.FromHours(2), "plus2", "plus2");
        var d = CaptureDate.FromUtc(new DateTime(2024, 6, 1, 22, 30, 0, DateTimeKind.Utc), zone, DateSource.QuickTime);
        Assert.Equal(new DateTime(2024, 6, 2, 0, 30, 0), d.LocalDateTime);
        Assert.Equal(TimeSpan.FromHours(2), d.UtcOffset);
        Assert.Equal(new DateTimeOffset(2024, 6, 1, 22, 30, 0, TimeSpan.Zero), d.Instant);
        Assert.Equal("2024-06-02T00:30:00.000", d.ToSortKey());
    }

    [Fact]
    public void Display_size_swaps_for_rotated_orientations()
    {
        var a = new Asset { PixelWidth = 4000, PixelHeight = 3000, Orientation = 6 };
        Assert.Equal((3000, 4000), a.DisplaySize);
        a.Orientation = 1;
        Assert.Equal((4000, 3000), a.DisplaySize);
    }

    [Fact]
    public void Camera_display_name_avoids_repeating_the_make()
    {
        Assert.Equal("Canon EOS R6", new CameraInfo("Canon", "Canon EOS R6", null, null, null, null, null).DisplayName);
        Assert.Equal("NIKON CORPORATION NIKON Z 6", new CameraInfo("NIKON CORPORATION", "NIKON Z 6", null, null, null, null, null).DisplayName);
        Assert.Null(CameraInfo.Empty.DisplayName);
    }

    [Fact]
    public void Names_are_trimmed_and_validated()
    {
        Assert.Equal("Trip", LibraryRules.NormalizeName("  Trip ", "n"));
        Assert.Throws<ArgumentException>(() => LibraryRules.NormalizeName("   ", "n"));
        Assert.Throws<ArgumentException>(() => LibraryRules.NormalizeName(new string('x', 201), "n"));
    }
}
