using System.Globalization;
using System.Text.RegularExpressions;
using PhotosWindows.Domain;

namespace PhotosWindows.Application.Common;

/// <summary>Tolerant parsing of the many date formats found in EXIF, XMP and file names.</summary>
public static partial class DateParsing
{
    private static readonly DateTime MinPlausible = new(1826, 1, 1); // first known photograph
    private static readonly string[] ExifFormats =
    [
        "yyyy:MM:dd HH:mm:ss", "yyyy:MM:dd HH:mm", "yyyy-MM-dd HH:mm:ss", "yyyy-MM-dd'T'HH:mm:ss", "yyyy-MM-dd'T'HH:mm",
        "yyyy:MM:dd'T'HH:mm:ss", "yyyy/MM/dd HH:mm:ss", "yyyy:MM:dd", "yyyy-MM-dd",
    ];

    /// <summary>Parses an EXIF-style wall-clock timestamp. Rejects zeroed/placeholder values.</summary>
    public static bool TryParseExifDateTime(string? value, out DateTime result)
    {
        result = default;
        if (string.IsNullOrWhiteSpace(value)) return false;
        var text = value.Trim().TrimEnd('\0').Trim();
        if (text.StartsWith("0000", StringComparison.Ordinal)) return false;

        // Drop fractional seconds and trailing zone designators; offsets are handled separately.
        var core = text.Length >= 19 ? text[..19] : text;
        if (!DateTime.TryParseExact(core, ExifFormats, CultureInfo.InvariantCulture, DateTimeStyles.AllowWhiteSpaces, out var parsed))
        {
            return false;
        }

        if (text.Length > 20 && text[19] == '.' && int.TryParse(new string(text[20..].TakeWhile(char.IsDigit).Take(3).ToArray()).PadRight(3, '0'), NumberStyles.None, CultureInfo.InvariantCulture, out var ms))
        {
            parsed = parsed.AddMilliseconds(ms);
        }

        if (parsed < MinPlausible || parsed > DateTime.Now.AddYears(2)) return false;
        result = DateTime.SpecifyKind(parsed, DateTimeKind.Unspecified);
        return true;
    }

    /// <summary>Adds EXIF SubSecTime* ("123" = 0.123 s) to a parsed timestamp.</summary>
    public static DateTime WithSubSeconds(DateTime value, string? subSec)
    {
        if (string.IsNullOrWhiteSpace(subSec) || value.Millisecond != 0) return value;
        var digits = new string(subSec.Trim().TakeWhile(char.IsDigit).Take(3).ToArray());
        return digits.Length > 0 && int.TryParse(digits.PadRight(3, '0'), NumberStyles.None, CultureInfo.InvariantCulture, out var ms)
            ? value.AddMilliseconds(ms)
            : value;
    }

    /// <summary>Parses "+02:00", "-0530", "Z" (EXIF OffsetTime*, ISO 8601 suffixes).</summary>
    public static bool TryParseOffset(string? value, out TimeSpan offset)
    {
        offset = default;
        if (string.IsNullOrWhiteSpace(value)) return false;
        var text = value.Trim().TrimEnd('\0');
        if (text is "Z" or "z")
        {
            offset = TimeSpan.Zero;
            return true;
        }

        var m = OffsetRegex().Match(text);
        if (!m.Success) return false;
        var hours = int.Parse(m.Groups["h"].Value, CultureInfo.InvariantCulture);
        var minutes = m.Groups["m"].Success ? int.Parse(m.Groups["m"].Value, CultureInfo.InvariantCulture) : 0;
        if (hours > 14 || minutes > 59) return false;
        offset = new TimeSpan(hours, minutes, 0);
        if (m.Groups["s"].Value == "-") offset = -offset;
        return true;
    }

    /// <summary>
    /// Infers the UTC offset from a local wall-clock time and the GPS UTC timestamp of the same shot,
    /// rounded to the nearest quarter hour. Returns null when the result is not a plausible zone offset.
    /// </summary>
    public static TimeSpan? InferOffset(DateTime localWallClock, DateTime gpsUtc)
    {
        var diff = localWallClock - DateTime.SpecifyKind(gpsUtc, DateTimeKind.Unspecified);
        var quarters = Math.Round(diff.TotalMinutes / 15.0);
        var rounded = TimeSpan.FromMinutes(quarters * 15);
        // Camera clocks drift; accept up to 5 minutes of disagreement with the GPS clock
        // (must stay below 7.5 minutes, otherwise every difference would round to some quarter hour).
        if (Math.Abs((diff - rounded).TotalMinutes) > 5 || rounded > TimeSpan.FromHours(14) || rounded < TimeSpan.FromHours(-12))
        {
            return null;
        }

        return rounded;
    }

    /// <summary>
    /// Extracts a capture date from common camera/phone file name patterns, e.g.
    /// IMG_20230415_142233.jpg, PXL_20230415_142233123.jpg, VID-20230415-WA0003.mp4, 2023-04-15 14.22.33.jpg.
    /// </summary>
    public static bool TryParseFromFileName(string fileName, out DateTime result)
    {
        result = default;
        var name = Path.GetFileNameWithoutExtension(fileName);
        var m = FileNameDateTimeRegex().Match(name);
        if (m.Success)
        {
            var text = $"{m.Groups["y"].Value}-{m.Groups["mo"].Value}-{m.Groups["d"].Value} {m.Groups["h"].Value}:{m.Groups["mi"].Value}:{m.Groups["s"].Value}";
            if (DateTime.TryParseExact(text, "yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture, DateTimeStyles.None, out var dt) && dt >= new DateTime(1990, 1, 1) && dt <= DateTime.Now.AddDays(2))
            {
                result = DateTime.SpecifyKind(dt, DateTimeKind.Unspecified);
                return true;
            }
        }

        m = FileNameDateRegex().Match(name);
        if (m.Success)
        {
            var text = $"{m.Groups["y"].Value}-{m.Groups["mo"].Value}-{m.Groups["d"].Value}";
            if (DateTime.TryParseExact(text, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var d) && d >= new DateTime(1990, 1, 1) && d <= DateTime.Now.AddDays(2))
            {
                result = DateTime.SpecifyKind(d.AddHours(12), DateTimeKind.Unspecified);
                return true;
            }
        }

        return false;
    }

    /// <summary>Best-effort capture date when no metadata date exists.</summary>
    public static CaptureDate Fallback(string fileName, DateTime fileModifiedUtc, TimeZoneInfo zone)
    {
        if (TryParseFromFileName(fileName, out var fromName))
        {
            return new CaptureDate(fromName, null, DateSource.FileName);
        }

        return CaptureDate.FromUtc(fileModifiedUtc, zone, DateSource.FileModified);
    }

    [GeneratedRegex(@"^(?<s>[+-])(?<h>\d{1,2})(?::?(?<m>\d{2}))?$")]
    private static partial Regex OffsetRegex();

    [GeneratedRegex(@"(?<!\d)(?<y>(19|20)\d{2})[-_.]?(?<mo>0[1-9]|1[0-2])[-_.]?(?<d>0[1-9]|[12]\d|3[01])[ _T-]+(?<h>[01]\d|2[0-3])[-_.:]?(?<mi>[0-5]\d)[-_.:]?(?<s>[0-5]\d)")]
    private static partial Regex FileNameDateTimeRegex();

    [GeneratedRegex(@"(?<!\d)(?<y>(19|20)\d{2})[-_.]?(?<mo>0[1-9]|1[0-2])[-_.]?(?<d>0[1-9]|[12]\d|3[01])(?!\d)")]
    private static partial Regex FileNameDateRegex();
}
