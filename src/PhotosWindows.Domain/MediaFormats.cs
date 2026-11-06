namespace PhotosWindows.Domain;

public enum FormatFamily
{
    Unknown = 0,
    Jpeg,
    Png,
    Gif,
    Bmp,
    Tiff,
    WebP,
    Heif,
    Avif,
    Raw,
    Video,
    Sidecar,
}

public sealed record MediaFormat(string Name, FormatFamily Family, MediaType? MediaType, IReadOnlyList<string> Extensions);

/// <summary>
/// Registry of formats the library recognises. Recognition is based on the extension AND the file signature
/// (magic bytes) so renamed or disguised files are not trusted blindly.
/// Whether a format can actually be decoded on this machine is decided by the registered decoders
/// (see docs/SUPPORTED_FORMATS.md).
/// </summary>
public static class MediaFormats
{
    public static readonly MediaFormat Jpeg = new("JPEG", FormatFamily.Jpeg, Domain.MediaType.Photo, [".jpg", ".jpeg", ".jpe", ".jfif"]);
    public static readonly MediaFormat Png = new("PNG", FormatFamily.Png, Domain.MediaType.Photo, [".png"]);
    public static readonly MediaFormat Gif = new("GIF", FormatFamily.Gif, Domain.MediaType.Photo, [".gif"]);
    public static readonly MediaFormat Bmp = new("BMP", FormatFamily.Bmp, Domain.MediaType.Photo, [".bmp", ".dib"]);
    public static readonly MediaFormat Tiff = new("TIFF", FormatFamily.Tiff, Domain.MediaType.Photo, [".tif", ".tiff"]);
    public static readonly MediaFormat WebP = new("WebP", FormatFamily.WebP, Domain.MediaType.Photo, [".webp"]);
    public static readonly MediaFormat Heif = new("HEIF", FormatFamily.Heif, Domain.MediaType.Photo, [".heic", ".heif", ".hif"]);
    public static readonly MediaFormat Avif = new("AVIF", FormatFamily.Avif, Domain.MediaType.Photo, [".avif"]);
    public static readonly MediaFormat Raw = new("RAW", FormatFamily.Raw, Domain.MediaType.Photo,
        [".dng", ".cr2", ".cr3", ".nef", ".nrw", ".arw", ".srf", ".sr2", ".raf", ".orf", ".rw2", ".pef", ".srw", ".x3f", ".3fr", ".iiq", ".rwl"]);
    public static readonly MediaFormat Video = new("Video", FormatFamily.Video, Domain.MediaType.Video,
        [".mp4", ".m4v", ".mov", ".avi", ".mkv", ".wmv", ".3gp", ".mts", ".m2ts", ".webm"]);
    public static readonly MediaFormat Xmp = new("XMP", FormatFamily.Sidecar, null, [".xmp"]);

    public static IReadOnlyList<MediaFormat> All { get; } = [Jpeg, Png, Gif, Bmp, Tiff, WebP, Heif, Avif, Raw, Video, Xmp];

    private static readonly Dictionary<string, MediaFormat> ByExtension = All
        .SelectMany(f => f.Extensions.Select(e => (e, f)))
        .ToDictionary(x => x.e, x => x.f, StringComparer.OrdinalIgnoreCase);

    public static MediaFormat? FromExtension(string pathOrExtension)
    {
        var ext = pathOrExtension.StartsWith('.') ? pathOrExtension : Path.GetExtension(pathOrExtension);
        return ext.Length > 0 && ByExtension.TryGetValue(ext, out var f) ? f : null;
    }

    public static bool IsCandidate(string path) => FromExtension(path) is not null;

    public static bool IsRawExtension(string path) => FromExtension(path)?.Family == FormatFamily.Raw;

    /// <summary>Number of header bytes <see cref="Sniff"/> needs.</summary>
    public const int SniffLength = 32;

    /// <summary>
    /// Identifies the container from its first bytes. Returns <see cref="FormatFamily.Unknown"/> when the header
    /// is not recognised. RAW formats mostly use TIFF containers, so <paramref name="extensionHint"/> is used to
    /// disambiguate TIFF-based files.
    /// </summary>
    public static FormatFamily Sniff(ReadOnlySpan<byte> h, string? extensionHint = null)
    {
        if (h.Length >= 3 && h[0] == 0xFF && h[1] == 0xD8 && h[2] == 0xFF) return FormatFamily.Jpeg;
        if (h.Length >= 8 && h[..8].SequenceEqual(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A })) return FormatFamily.Png;
        if (h.Length >= 6 && h[0] == 'G' && h[1] == 'I' && h[2] == 'F' && h[3] == '8') return FormatFamily.Gif;
        if (h.Length >= 2 && h[0] == 'B' && h[1] == 'M') return FormatFamily.Bmp;
        if (h.Length >= 12 && h[0] == 'R' && h[1] == 'I' && h[2] == 'F' && h[3] == 'F')
        {
            if (h[8] == 'W' && h[9] == 'E' && h[10] == 'B' && h[11] == 'P') return FormatFamily.WebP;
            if (h[8] == 'A' && h[9] == 'V' && h[10] == 'I' && h[11] == ' ') return FormatFamily.Video;
            return FormatFamily.Unknown;
        }

        if (h.Length >= 4 && h[0] == 0x1A && h[1] == 0x45 && h[2] == 0xDF && h[3] == 0xA3) return FormatFamily.Video; // Matroska/WebM
        if (h.Length >= 16 && h[0] == 0x30 && h[1] == 0x26 && h[2] == 0xB2 && h[3] == 0x75) return FormatFamily.Video; // ASF/WMV
        if (h.Length >= 5 && h[4] == 0x47 && (extensionHint is ".mts" or ".m2ts")) return FormatFamily.Video; // MPEG-TS with 4-byte timecode

        if (h.Length >= 12 && h[4] == 'f' && h[5] == 't' && h[6] == 'y' && h[7] == 'p')
        {
            var brand = System.Text.Encoding.ASCII.GetString(h.Slice(8, 4));
            return brand switch
            {
                "heic" or "heix" or "hevc" or "hevx" or "heim" or "heis" or "mif1" or "msf1" => FormatFamily.Heif,
                "avif" or "avis" => FormatFamily.Avif,
                "crx " => FormatFamily.Raw, // Canon CR3 (ISO-BMFF based)
                _ => FormatFamily.Video, // mp4/isom/qt/m4v/3gp...
            };
        }

        // Older QuickTime files may start with other atoms (wide, mdat, moov, free).
        if (h.Length >= 8)
        {
            var atom = System.Text.Encoding.ASCII.GetString(h.Slice(4, 4));
            if (atom is "moov" or "mdat" or "wide" or "free" or "skip" or "pnot") return FormatFamily.Video;
        }

        var isTiffLe = h.Length >= 4 && h[0] == 'I' && h[1] == 'I' && (h[2] == 0x2A || h[2] == 0x52 || h[2] == 0x55) ;
        var isTiffBe = h.Length >= 4 && h[0] == 'M' && h[1] == 'M' && h[2] == 0x00 && h[3] == 0x2A;
        if (isTiffLe || isTiffBe)
        {
            // CR2, NEF, ARW, DNG, ORF (IIRO), RW2 (IIU) ... are TIFF-structured.
            return extensionHint is not null && FromExtension(extensionHint)?.Family == FormatFamily.Raw ? FormatFamily.Raw : FormatFamily.Tiff;
        }

        if (h.Length >= 16 && h[0] == 'F' && h[1] == 'U' && h[2] == 'J' && h[3] == 'I') return FormatFamily.Raw; // RAF
        if (h.Length >= 4 && h[0] == 'F' && h[1] == 'O' && h[2] == 'V' && h[3] == 'b') return FormatFamily.Raw; // X3F

        var text = h.Length > 0 ? System.Text.Encoding.ASCII.GetString(h).TrimStart('﻿', ' ', '\r', '\n', '\t') : string.Empty;
        if (text.StartsWith("<?xpacket", StringComparison.Ordinal) || text.StartsWith("<x:xmpmeta", StringComparison.Ordinal) || text.StartsWith("<?xml", StringComparison.Ordinal))
        {
            return FormatFamily.Sidecar;
        }

        return FormatFamily.Unknown;
    }

    /// <summary>True when the sniffed container is plausible for the extension (guards against renamed files).</summary>
    public static bool SignatureMatchesExtension(FormatFamily sniffed, string path)
    {
        var expected = FromExtension(path);
        if (expected is null || sniffed == FormatFamily.Unknown)
        {
            return false;
        }

        if (sniffed == expected.Family)
        {
            return true;
        }

        // Some cameras write HEIF with AVIF-compatible brands and vice versa; TIFF-based RAW detection is extension-driven.
        return (expected.Family, sniffed) switch
        {
            (FormatFamily.Heif, FormatFamily.Avif) or (FormatFamily.Avif, FormatFamily.Heif) => true,
            (FormatFamily.Raw, FormatFamily.Tiff) => true,
            (FormatFamily.Tiff, FormatFamily.Raw) => true,
            _ => false,
        };
    }
}
