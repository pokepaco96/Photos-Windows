using System.Globalization;
using System.Text.RegularExpressions;
using MetadataExtractor;
using MetadataExtractor.Formats.Exif;
using MetadataExtractor.Formats.Iptc;
using MetadataExtractor.Formats.Jpeg;
using MetadataExtractor.Formats.Png;
using MetadataExtractor.Formats.QuickTime;
using MetadataExtractor.Formats.Xmp;
using Microsoft.Extensions.Logging;
using PhotosWindows.Application.Abstractions;
using PhotosWindows.Application.Common;
using PhotosWindows.Domain;
using XmpCore;
using Directory = MetadataExtractor.Directory;
using MeGeo = MetadataExtractor.GeoLocation;

namespace PhotosWindows.Infrastructure.Metadata;

/// <summary>
/// Reads EXIF, IPTC, XMP, GPS and QuickTime/MP4 metadata with MetadataExtractor (Apache-2.0).
/// The reader only reads: originals are opened read-only and never written.
/// </summary>
public sealed partial class MetadataExtractorReader : IMetadataReader
{
    private const int MaxSidecarBytes = 1024 * 1024;
    private const int MaxRawEntries = 400;
    private static readonly DateTime QuickTimeEpoch = new(1904, 1, 1, 0, 0, 0, DateTimeKind.Utc);
    private readonly ILogger<MetadataExtractorReader> _logger;
    private readonly IClock _clock;

    public MetadataExtractorReader(ILogger<MetadataExtractorReader> logger, IClock clock)
    {
        _logger = logger;
        _clock = clock;
    }

    public MediaMetadata Read(string path, FormatFamily family)
    {
        IReadOnlyList<Directory> directories;
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 64 * 1024, FileOptions.SequentialScan);
            directories = ImageMetadataReader.ReadMetadata(stream, Path.GetFileName(path));
        }
        catch (Exception ex) when (ex is ImageProcessingException or IOException or UnauthorizedAccessException or ArgumentException or InvalidOperationException or OverflowException or IndexOutOfRangeException)
        {
            _logger.LogDebug(ex, "No readable metadata in {File}", Path.GetFileName(path));
            return new MediaMetadata { Warnings = [$"Metadata could not be read: {ex.Message}"] };
        }

        return family == FormatFamily.Video ? ReadVideo(directories) : ReadStill(directories);
    }

    private MediaMetadata ReadStill(IReadOnlyList<Directory> dirs)
    {
        var warnings = dirs.SelectMany(d => d.Errors).Take(10).ToList();
        var ifd0 = dirs.OfType<ExifIfd0Directory>().FirstOrDefault();
        var sub = dirs.OfType<ExifSubIfdDirectory>().FirstOrDefault();
        var gps = dirs.OfType<GpsDirectory>().FirstOrDefault();

        // ---- Capture date ----
        CaptureDate? capture = null;
        if (sub is not null && DateParsing.TryParseExifDateTime(sub.GetString(ExifDirectoryBase.TagDateTimeOriginal), out var original))
        {
            original = DateParsing.WithSubSeconds(original, sub.GetString(ExifDirectoryBase.TagSubsecondTimeOriginal));
            capture = new CaptureDate(original, ResolveOffset(sub.GetString(ExifDirectoryBase.TagTimeZoneOriginal), original, gps), DateSource.ExifOriginal);
        }
        else if (sub is not null && DateParsing.TryParseExifDateTime(sub.GetString(ExifDirectoryBase.TagDateTimeDigitized), out var digitized))
        {
            digitized = DateParsing.WithSubSeconds(digitized, sub.GetString(ExifDirectoryBase.TagSubsecondTimeDigitized));
            capture = new CaptureDate(digitized, ResolveOffset(sub.GetString(ExifDirectoryBase.TagTimeZoneDigitized), digitized, gps), DateSource.ExifDigitized);
        }
        else if (ifd0 is not null && DateParsing.TryParseExifDateTime(ifd0.GetString(ExifDirectoryBase.TagDateTime), out var modified))
        {
            capture = new CaptureDate(modified, ResolveOffset(ifd0.GetString(ExifDirectoryBase.TagTimeZone), modified, gps), DateSource.ExifDigitized);
        }

        var xmp = ReadXmpProperties(dirs.OfType<XmpDirectory>().FirstOrDefault()?.XmpMeta);
        if (capture is null && xmp.Date is { } xmpDate)
        {
            capture = xmpDate;
        }

        // ---- Dimensions (container first, EXIF as fallback) ----
        int? width = null, height = null;
        var jpeg = dirs.OfType<JpegDirectory>().FirstOrDefault();
        var png = dirs.OfType<PngDirectory>().FirstOrDefault();
        if (jpeg is not null && jpeg.TryGetInt32(JpegDirectory.TagImageWidth, out var jw) && jpeg.TryGetInt32(JpegDirectory.TagImageHeight, out var jh))
        {
            (width, height) = (jw, jh);
        }
        else if (png is not null && png.TryGetInt32(PngDirectory.TagImageWidth, out var pw) && png.TryGetInt32(PngDirectory.TagImageHeight, out var ph))
        {
            (width, height) = (pw, ph);
        }
        else if (sub is not null && sub.TryGetInt32(ExifDirectoryBase.TagExifImageWidth, out var ew) && sub.TryGetInt32(ExifDirectoryBase.TagExifImageHeight, out var eh))
        {
            (width, height) = (ew, eh);
        }

        var orientation = 1;
        if (ifd0 is not null && ifd0.TryGetInt32(ExifDirectoryBase.TagOrientation, out var o) && o is >= 1 and <= 8)
        {
            orientation = o;
        }

        // ---- Camera ----
        var camera = new CameraInfo(
            Clean(ifd0?.GetString(ExifDirectoryBase.TagMake)),
            Clean(ifd0?.GetString(ExifDirectoryBase.TagModel)),
            Clean(sub?.GetString(ExifDirectoryBase.TagLensModel)),
            PositiveRational(sub, ExifDirectoryBase.TagFNumber),
            PositiveRational(sub, ExifDirectoryBase.TagExposureTime),
            sub is not null && sub.TryGetInt32(ExifDirectoryBase.TagIsoEquivalent, out var iso) && iso > 0 ? iso : null,
            PositiveRational(sub, ExifDirectoryBase.TagFocalLength));

        // ---- Location ----
        Domain.GeoLocation? location = null;
        if (gps is not null && gps.TryGetGeoLocation(out MeGeo geo) && !geo.IsZero)
        {
            double? altitude = null;
            if (gps.TryGetRational(GpsDirectory.TagAltitude, out var alt) && alt.Denominator != 0)
            {
                altitude = alt.ToDouble() * (gps.TryGetInt32(GpsDirectory.TagAltitudeRef, out var aref) && aref == 1 ? -1 : 1);
            }

            if (Domain.GeoLocation.TryCreate(geo.Latitude, geo.Longitude, altitude, out var g))
            {
                location = g;
            }
        }

        // ---- Descriptive (XMP wins over IPTC, as XMP is the modern carrier) ----
        var iptc = dirs.OfType<IptcDirectory>().FirstOrDefault();
        var keywords = xmp.Keywords.Count > 0
            ? xmp.Keywords
            : (iptc?.GetStringArray(IptcDirectory.TagKeywords) ?? []).Select(k => k.Trim()).Where(k => k.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).ToList();

        return new MediaMetadata
        {
            CaptureDate = capture,
            Width = width is > 0 ? width : null,
            Height = height is > 0 ? height : null,
            Orientation = orientation,
            Camera = camera,
            Location = location,
            Title = xmp.Title ?? Clean(iptc?.GetString(IptcDirectory.TagObjectName)),
            Description = xmp.Description ?? Clean(iptc?.GetString(IptcDirectory.TagCaption)) ?? Clean(ifd0?.GetString(ExifDirectoryBase.TagImageDescription)),
            Keywords = keywords,
            Rating = xmp.Rating,
            ContentIdentifier = FindContentIdentifier(dirs),
            Raw = Flatten(dirs),
            Warnings = warnings,
        };
    }

    private MediaMetadata ReadVideo(IReadOnlyList<Directory> dirs)
    {
        var warnings = dirs.SelectMany(d => d.Errors).Take(10).ToList();
        var movie = dirs.OfType<QuickTimeMovieHeaderDirectory>().FirstOrDefault();
        var meta = dirs.OfType<QuickTimeMetadataHeaderDirectory>().FirstOrDefault();

        CaptureDate? capture = null;
        // Apple devices write a local timestamp with offset in com.apple.quicktime.creationdate.
        var appleDate = meta?.GetString(QuickTimeMetadataHeaderDirectory.TagCreationDate);
        if (appleDate is not null && DateTimeOffset.TryParse(appleDate, CultureInfo.InvariantCulture, DateTimeStyles.None, out var dto) && dto.Year > 1904)
        {
            capture = new CaptureDate(dto.DateTime, dto.Offset, DateSource.QuickTime);
        }
        else if (movie is not null && movie.GetObject(QuickTimeMovieHeaderDirectory.TagCreated) is DateTime created && created > QuickTimeEpoch.AddDays(1))
        {
            // mvhd creation time is defined as UTC; express it in the machine's zone (best available guess).
            capture = CaptureDate.FromUtc(created, _clock.LocalZone, DateSource.QuickTime);
        }

        TimeSpan? duration = null;
        if (movie is not null)
        {
            duration = movie.GetObject(QuickTimeMovieHeaderDirectory.TagDuration) switch
            {
                TimeSpan ts when ts > TimeSpan.Zero => ts,
                IConvertible c when movie.TryGetInt64(QuickTimeMovieHeaderDirectory.TagTimeScale, out var scale) && scale > 0 => TimeSpan.FromSeconds(c.ToDouble(CultureInfo.InvariantCulture) / scale),
                _ => null,
            };
        }

        int? width = null, height = null;
        foreach (var track in dirs.OfType<QuickTimeTrackHeaderDirectory>())
        {
            if (track.TryGetDouble(QuickTimeTrackHeaderDirectory.TagWidth, out var w) && track.TryGetDouble(QuickTimeTrackHeaderDirectory.TagHeight, out var h) && w > 0 && h > 0)
            {
                width = (int)Math.Round(w);
                height = (int)Math.Round(h);
                break;
            }
        }

        Domain.GeoLocation? location = null;
        if (meta?.GetString(QuickTimeMetadataHeaderDirectory.TagGpsLocation) is { } iso6709 && TryParseIso6709(iso6709, out var loc))
        {
            location = loc;
        }

        var camera = new CameraInfo(
            Clean(meta?.GetString(QuickTimeMetadataHeaderDirectory.TagMake)),
            Clean(meta?.GetString(QuickTimeMetadataHeaderDirectory.TagModel)) ?? Clean(meta?.GetString(QuickTimeMetadataHeaderDirectory.TagAndroidModel)),
            null, null, null, null, null);

        return new MediaMetadata
        {
            CaptureDate = capture,
            Width = width,
            Height = height,
            Duration = duration,
            Camera = camera,
            Location = location,
            ContentIdentifier = Clean(meta?.GetString(QuickTimeMetadataHeaderDirectory.TagContentIdentifier)),
            Raw = Flatten(dirs),
            Warnings = warnings,
        };
    }

    public MediaMetadata ReadSidecar(string xmpPath)
    {
        try
        {
            var info = new FileInfo(xmpPath);
            if (info.Length > MaxSidecarBytes)
            {
                return new MediaMetadata { Warnings = ["XMP sidecar ignored: larger than 1 MB."] };
            }

            var bytes = File.ReadAllBytes(xmpPath);
            var meta = XmpMetaFactory.ParseFromBuffer(bytes);
            var xmp = ReadXmpProperties(meta);
            return new MediaMetadata { CaptureDate = xmp.Date, Title = xmp.Title, Description = xmp.Description, Keywords = xmp.Keywords, Rating = xmp.Rating };
        }
        catch (Exception ex) when (ex is XmpException or IOException or UnauthorizedAccessException or System.Xml.XmlException)
        {
            return new MediaMetadata { Warnings = [$"XMP sidecar could not be read: {ex.Message}"] };
        }
    }

    private sealed record XmpValues(string? Title, string? Description, IReadOnlyList<string> Keywords, int? Rating, CaptureDate? Date);

    private static XmpValues ReadXmpProperties(IXmpMeta? meta)
    {
        if (meta is null) return new XmpValues(null, null, [], null, null);
        string? title = null, description = null;
        int? rating = null;
        CaptureDate? date = null;
        var keywords = new List<string>();
        foreach (var p in meta.Properties)
        {
            if (string.IsNullOrEmpty(p.Path) || string.IsNullOrWhiteSpace(p.Value)) continue;
            var path = p.Path;
            if (path.StartsWith("dc:title[", StringComparison.Ordinal)) title ??= Clean(p.Value);
            else if (path.StartsWith("dc:description[", StringComparison.Ordinal)) description ??= Clean(p.Value);
            else if (path.StartsWith("dc:subject[", StringComparison.Ordinal) || path.StartsWith("lr:hierarchicalSubject[", StringComparison.Ordinal) && keywords.Count == 0)
            {
                var k = p.Value.Trim();
                var leaf = k.Contains('|', StringComparison.Ordinal) ? k[(k.LastIndexOf('|') + 1)..] : k;
                if (leaf.Length > 0 && !keywords.Contains(leaf, StringComparer.OrdinalIgnoreCase)) keywords.Add(leaf);
            }
            else if (path == "xmp:Rating" && int.TryParse(p.Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var r)) rating = Math.Clamp(r, 0, 5);
            else if (date is null && (path == "photoshop:DateCreated" || path == "exif:DateTimeOriginal" || path == "xmp:CreateDate") && TryParseXmpDate(p.Value, out var d)) date = d;
        }

        return new XmpValues(title, description, keywords, rating, date);
    }

    private static bool TryParseXmpDate(string value, out CaptureDate date)
    {
        date = default;
        if (!DateParsing.TryParseExifDateTime(value, out var local)) return false;
        TimeSpan? offset = null;
        var m = XmpOffsetRegex().Match(value.Trim());
        if (m.Success && DateParsing.TryParseOffset(m.Value, out var off)) offset = off;
        date = new CaptureDate(local, offset, DateSource.XmpSidecar);
        return true;
    }

    private static TimeSpan? ResolveOffset(string? offsetTag, DateTime local, GpsDirectory? gps)
    {
        if (DateParsing.TryParseOffset(offsetTag, out var offset)) return offset;
        if (gps is not null && gps.TryGetGpsDate(out var gpsUtc)) return DateParsing.InferOffset(local, gpsUtc);
        return null;
    }

    private static string? FindContentIdentifier(IReadOnlyList<Directory> dirs)
    {
        foreach (var d in dirs.Where(d => d.Name.Contains("Apple", StringComparison.OrdinalIgnoreCase)))
        {
            foreach (var t in d.Tags)
            {
                if (t.Name.Contains("Content Identifier", StringComparison.OrdinalIgnoreCase) && !string.IsNullOrWhiteSpace(t.Description))
                {
                    return t.Description.Trim();
                }
            }
        }

        return null;
    }

    internal static bool TryParseIso6709(string value, out Domain.GeoLocation location)
    {
        location = default;
        var m = Iso6709Regex().Match(value.Trim());
        if (!m.Success) return false;
        var lat = double.Parse(m.Groups["lat"].Value, CultureInfo.InvariantCulture);
        var lon = double.Parse(m.Groups["lon"].Value, CultureInfo.InvariantCulture);
        double? alt = m.Groups["alt"].Success ? double.Parse(m.Groups["alt"].Value, CultureInfo.InvariantCulture) : null;
        return Domain.GeoLocation.TryCreate(lat, lon, alt, out location);
    }

    private static double? PositiveRational(Directory? dir, int tag) =>
        dir is not null && dir.TryGetRational(tag, out var r) && r.Denominator != 0 && r.ToDouble() > 0 ? r.ToDouble() : null;

    private static string? Clean(string? value)
    {
        if (value is null) return null;
        var t = value.Replace("\0", string.Empty, StringComparison.Ordinal).Trim();
        return t.Length == 0 ? null : t.Length > 500 ? t[..500] : t;
    }

    private static List<MetadataEntry> Flatten(IReadOnlyList<Directory> dirs)
    {
        var list = new List<MetadataEntry>();
        foreach (var d in dirs)
        {
            if (d.Name is "File" or "File Type") continue;
            foreach (var t in d.Tags)
            {
                if (list.Count >= MaxRawEntries) return list;
                var desc = t.Description;
                if (string.IsNullOrWhiteSpace(desc) || desc.Length > 200 || desc.StartsWith("[", StringComparison.Ordinal) && desc.EndsWith("bytes]", StringComparison.Ordinal)) continue;
                list.Add(new MetadataEntry(d.Name, t.Name, desc));
            }
        }

        return list;
    }

    [GeneratedRegex(@"^(?<lat>[+-]\d+(\.\d+)?)(?<lon>[+-]\d+(\.\d+)?)(?<alt>[+-]\d+(\.\d+)?)?(CRS[^/]*)?/?$")]
    private static partial Regex Iso6709Regex();

    [GeneratedRegex(@"([+-]\d{2}:?\d{2}|Z)$")]
    private static partial Regex XmpOffsetRegex();
}
