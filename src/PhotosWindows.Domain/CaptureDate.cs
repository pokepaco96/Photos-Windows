namespace PhotosWindows.Domain;

/// <summary>
/// Capture moment of an asset. Cameras usually record a wall-clock time without a time zone,
/// so the local wall-clock value is the primary data and the UTC offset is optional.
/// Grouping by day uses the wall-clock value (the day the photographer experienced).
/// </summary>
public readonly record struct CaptureDate
{
    public CaptureDate(DateTime localDateTime, TimeSpan? utcOffset, DateSource source)
    {
        LocalDateTime = DateTime.SpecifyKind(localDateTime, DateTimeKind.Unspecified);
        UtcOffset = utcOffset;
        Source = source;
    }

    /// <summary>Wall-clock time at the place of capture (Kind = Unspecified).</summary>
    public DateTime LocalDateTime { get; }

    /// <summary>Offset from UTC when known (EXIF OffsetTime*, QuickTime UTC, manual).</summary>
    public TimeSpan? UtcOffset { get; }

    public DateSource Source { get; }

    public bool HasKnownOffset => UtcOffset.HasValue;

    /// <summary>The absolute instant when the offset is known.</summary>
    public DateTimeOffset? Instant => UtcOffset is { } offset ? new DateTimeOffset(LocalDateTime, offset) : null;

    /// <summary>Builds a capture date from an absolute UTC instant, expressed in the given zone.</summary>
    public static CaptureDate FromUtc(DateTime utc, TimeZoneInfo zone, DateSource source)
    {
        var utcValue = DateTime.SpecifyKind(utc, DateTimeKind.Utc);
        var offset = zone.GetUtcOffset(utcValue);
        return new CaptureDate(utcValue + offset, offset, source);
    }

    /// <summary>Stable sortable representation (ISO 8601 wall clock, no zone).</summary>
    public string ToSortKey() => LocalDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.fff", System.Globalization.CultureInfo.InvariantCulture);
}
