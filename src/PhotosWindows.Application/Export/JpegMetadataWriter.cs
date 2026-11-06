using System.Buffers.Binary;
using System.Globalization;
using System.Security;
using System.Text;
using PhotosWindows.Domain;

namespace PhotosWindows.Application.Export;

/// <summary>Metadata written into exported JPEG files.</summary>
public sealed record ExportMetadata(
    CaptureDate? CaptureDate,
    CameraInfo Camera,
    GeoLocation? Location,
    int PixelWidth,
    int PixelHeight,
    string? Title,
    string? Description,
    IReadOnlyList<string> Keywords,
    int Rating);

/// <summary>
/// Builds minimal, standards-compliant EXIF (little-endian TIFF) and XMP APP1 segments and injects them into an
/// encoded JPEG. Exported pixels are already upright, so exports always write Orientation = 1
/// (the parameter exists for generating test fixtures).
/// </summary>
public static class JpegMetadataWriter
{
    private const ushort Ascii = 2, Short = 3, Long = 4, Rational = 5, Byte = 1;
    private static readonly byte[] ExifHeader = "Exif\0\0"u8.ToArray();
    private static readonly byte[] XmpHeader = Encoding.ASCII.GetBytes("http://ns.adobe.com/xap/1.0/\0");
    private const int MaxSegmentPayload = 65533;

    public static byte[] Inject(byte[] jpeg, ExportMetadata metadata, bool includeLocation, bool includeDescriptive, int orientation = 1)
    {
        ArgumentNullException.ThrowIfNull(jpeg);
        ArgumentNullException.ThrowIfNull(metadata);
        if (jpeg.Length < 4 || jpeg[0] != 0xFF || jpeg[1] != 0xD8)
        {
            throw new ArgumentException("Not a JPEG stream.", nameof(jpeg));
        }

        var segments = new List<byte[]> { Segment(0xE1, [.. ExifHeader, .. BuildExif(metadata, includeLocation, orientation)]) };
        if (includeDescriptive && BuildXmp(metadata) is { } xmp)
        {
            segments.Add(Segment(0xE1, [.. XmpHeader, .. xmp]));
        }

        using var output = new MemoryStream(jpeg.Length + 4096);
        output.Write(jpeg, 0, 2);
        foreach (var s in segments) output.Write(s);

        // Copy the remaining markers, dropping existing APP0 (JFIF) / APP1 (EXIF/XMP) segments.
        var pos = 2;
        while (pos + 4 <= jpeg.Length && jpeg[pos] == 0xFF)
        {
            var marker = jpeg[pos + 1];
            if (marker == 0xDA || marker == 0xD9) break; // start of scan / end of image: rest is entropy-coded data
            var length = BinaryPrimitives.ReadUInt16BigEndian(jpeg.AsSpan(pos + 2));
            if (length < 2 || pos + 2 + length > jpeg.Length) break;
            if (marker is not (0xE0 or 0xE1))
            {
                output.Write(jpeg, pos, 2 + length);
            }

            pos += 2 + length;
        }

        output.Write(jpeg, pos, jpeg.Length - pos);
        return output.ToArray();
    }

    /// <summary>Removes every APPn and COM segment (metadata stripping) while keeping image data intact.</summary>
    public static byte[] StripMetadata(byte[] jpeg)
    {
        if (jpeg.Length < 4 || jpeg[0] != 0xFF || jpeg[1] != 0xD8) return jpeg;
        using var output = new MemoryStream(jpeg.Length);
        output.Write(jpeg, 0, 2);
        var pos = 2;
        while (pos + 4 <= jpeg.Length && jpeg[pos] == 0xFF)
        {
            var marker = jpeg[pos + 1];
            if (marker == 0xDA || marker == 0xD9) break;
            var length = BinaryPrimitives.ReadUInt16BigEndian(jpeg.AsSpan(pos + 2));
            if (length < 2 || pos + 2 + length > jpeg.Length) break;
            var isMetadata = marker is >= 0xE1 and <= 0xEF || marker == 0xFE; // keep APP0 (JFIF) for compatibility
            if (!isMetadata) output.Write(jpeg, pos, 2 + length);
            pos += 2 + length;
        }

        output.Write(jpeg, pos, jpeg.Length - pos);
        return output.ToArray();
    }

    private static byte[] Segment(byte marker, byte[] payload)
    {
        if (payload.Length > MaxSegmentPayload) throw new InvalidOperationException("Metadata segment too large.");
        var s = new byte[payload.Length + 4];
        s[0] = 0xFF;
        s[1] = marker;
        BinaryPrimitives.WriteUInt16BigEndian(s.AsSpan(2), (ushort)(payload.Length + 2));
        payload.CopyTo(s, 4);
        return s;
    }

    // ---------------- EXIF ----------------
    private sealed record Entry(ushort Tag, ushort Type, uint Count, byte[] Value);

    internal static byte[] BuildExif(ExportMetadata m, bool includeLocation, int orientation = 1)
    {
        var ifd0 = new List<Entry>();
        AddAscii(ifd0, 0x010F, m.Camera.Make);
        AddAscii(ifd0, 0x0110, m.Camera.Model);
        ifd0.Add(new Entry(0x0112, Short, 1, U16((ushort)Math.Clamp(orientation, 1, 8))));
        AddAscii(ifd0, 0x0131, "Photos Windows");

        var exif = new List<Entry>();
        if (m.Camera.ExposureTimeSeconds is { } t && t > 0)
        {
            exif.Add(t < 1 ? new Entry(0x829A, Rational, 1, Rat(1, (uint)Math.Round(1 / t))) : new Entry(0x829A, Rational, 1, Rat((uint)Math.Round(t * 10), 10)));
        }

        if (m.Camera.FNumber is { } f && f > 0) exif.Add(new Entry(0x829D, Rational, 1, Rat((uint)Math.Round(f * 10), 10)));
        if (m.Camera.Iso is { } iso && iso > 0) exif.Add(new Entry(0x8827, Short, 1, U16((ushort)Math.Min(iso, ushort.MaxValue))));
        if (m.CaptureDate is { } date)
        {
            AddAscii(exif, 0x9003, date.LocalDateTime.ToString("yyyy:MM:dd HH:mm:ss", CultureInfo.InvariantCulture));
            if (date.UtcOffset is { } off)
            {
                var sign = off < TimeSpan.Zero ? "-" : "+";
                AddAscii(exif, 0x9011, $"{sign}{Math.Abs(off.Hours):00}:{Math.Abs(off.Minutes):00}");
            }
        }

        if (m.Camera.FocalLengthMm is { } fl && fl > 0) exif.Add(new Entry(0x920A, Rational, 1, Rat((uint)Math.Round(fl * 10), 10)));
        if (m.PixelWidth > 0) exif.Add(new Entry(0xA002, Long, 1, U32((uint)m.PixelWidth)));
        if (m.PixelHeight > 0) exif.Add(new Entry(0xA003, Long, 1, U32((uint)m.PixelHeight)));
        AddAscii(exif, 0xA434, m.Camera.Lens);

        List<Entry>? gps = null;
        if (includeLocation && m.Location is { } loc)
        {
            gps =
            [
                new Entry(0x0000, Byte, 4, [2, 3, 0, 0]),
                new Entry(0x0001, Ascii, 2, Encoding.ASCII.GetBytes(loc.Latitude >= 0 ? "N\0" : "S\0")),
                new Entry(0x0002, Rational, 3, Dms(Math.Abs(loc.Latitude))),
                new Entry(0x0003, Ascii, 2, Encoding.ASCII.GetBytes(loc.Longitude >= 0 ? "E\0" : "W\0")),
                new Entry(0x0004, Rational, 3, Dms(Math.Abs(loc.Longitude))),
            ];
            if (loc.Altitude is { } alt)
            {
                gps.Add(new Entry(0x0005, Byte, 1, [(byte)(alt < 0 ? 1 : 0)]));
                gps.Add(new Entry(0x0006, Rational, 1, Rat((uint)Math.Round(Math.Abs(alt) * 100), 100)));
            }
        }

        // Pointers are placeholders now; their final values are patched after layout.
        ifd0.Add(new Entry(0x8769, Long, 1, U32(0)));
        if (gps is not null) ifd0.Add(new Entry(0x8825, Long, 1, U32(0)));

        var blocks = new List<List<Entry>> { ifd0, exif };
        if (gps is not null) blocks.Add(gps);
        foreach (var b in blocks) b.Sort((a, c) => a.Tag.CompareTo(c.Tag));

        var offsets = new List<uint>();
        uint cursor = 8;
        foreach (var b in blocks)
        {
            offsets.Add(cursor);
            cursor += BlockSize(b);
        }

        Patch(ifd0, 0x8769, offsets[1]);
        if (gps is not null) Patch(ifd0, 0x8825, offsets[2]);

        var tiff = new byte[cursor];
        tiff[0] = (byte)'I';
        tiff[1] = (byte)'I';
        BinaryPrimitives.WriteUInt16LittleEndian(tiff.AsSpan(2), 42);
        BinaryPrimitives.WriteUInt32LittleEndian(tiff.AsSpan(4), 8);
        for (var i = 0; i < blocks.Count; i++) WriteBlock(tiff, offsets[i], blocks[i]);
        return tiff;
    }

    private static uint BlockSize(List<Entry> entries)
    {
        uint size = (uint)(2 + entries.Count * 12 + 4);
        foreach (var e in entries.Where(e => e.Value.Length > 4)) size += (uint)(e.Value.Length + (e.Value.Length & 1));
        return size;
    }

    private static void WriteBlock(byte[] tiff, uint offset, List<Entry> entries)
    {
        var span = tiff.AsSpan();
        BinaryPrimitives.WriteUInt16LittleEndian(span[(int)offset..], (ushort)entries.Count);
        var dataOffset = offset + 2 + (uint)entries.Count * 12 + 4;
        for (var i = 0; i < entries.Count; i++)
        {
            var e = entries[i];
            var at = (int)offset + 2 + i * 12;
            BinaryPrimitives.WriteUInt16LittleEndian(span[at..], e.Tag);
            BinaryPrimitives.WriteUInt16LittleEndian(span[(at + 2)..], e.Type);
            BinaryPrimitives.WriteUInt32LittleEndian(span[(at + 4)..], e.Count);
            if (e.Value.Length <= 4)
            {
                e.Value.CopyTo(span[(at + 8)..]);
            }
            else
            {
                BinaryPrimitives.WriteUInt32LittleEndian(span[(at + 8)..], dataOffset);
                e.Value.CopyTo(span[(int)dataOffset..]);
                dataOffset += (uint)(e.Value.Length + (e.Value.Length & 1));
            }
        }

        BinaryPrimitives.WriteUInt32LittleEndian(span[((int)offset + 2 + entries.Count * 12)..], 0); // no next IFD
    }

    private static void Patch(List<Entry> entries, ushort tag, uint value)
    {
        var i = entries.FindIndex(e => e.Tag == tag);
        entries[i] = entries[i] with { Value = U32(value) };
    }

    private static void AddAscii(List<Entry> entries, ushort tag, string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return;
        var clean = new string(value.Trim().Where(c => c is >= ' ' and <= '~').Take(255).ToArray());
        if (clean.Length == 0) return;
        var bytes = Encoding.ASCII.GetBytes(clean + "\0");
        entries.Add(new Entry(tag, Ascii, (uint)bytes.Length, bytes));
    }

    private static byte[] U16(ushort v) { var b = new byte[2]; BinaryPrimitives.WriteUInt16LittleEndian(b, v); return b; }

    private static byte[] U32(uint v) { var b = new byte[4]; BinaryPrimitives.WriteUInt32LittleEndian(b, v); return b; }

    private static byte[] Rat(uint num, uint den)
    {
        var b = new byte[8];
        BinaryPrimitives.WriteUInt32LittleEndian(b, num);
        BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(4), Math.Max(1, den));
        return b;
    }

    private static byte[] Dms(double decimalDegrees)
    {
        var deg = Math.Floor(decimalDegrees);
        var minutesFull = (decimalDegrees - deg) * 60;
        var min = Math.Floor(minutesFull);
        var sec = (minutesFull - min) * 60;
        return [.. Rat((uint)deg, 1), .. Rat((uint)min, 1), .. Rat((uint)Math.Round(sec * 10000), 10000)];
    }

    // ---------------- XMP ----------------
    internal static byte[]? BuildXmp(ExportMetadata m)
    {
        if (string.IsNullOrWhiteSpace(m.Title) && string.IsNullOrWhiteSpace(m.Description) && m.Keywords.Count == 0 && m.Rating == 0)
        {
            return null;
        }

        var keywords = m.Keywords.ToList();
        while (true)
        {
            var sb = new StringBuilder();
            sb.Append("<?xpacket begin=\"﻿\" id=\"W5M0MpCehiHzreSzNTczkc9d\"?>");
            sb.Append("<x:xmpmeta xmlns:x=\"adobe:ns:meta/\"><rdf:RDF xmlns:rdf=\"http://www.w3.org/1999/02/22-rdf-syntax-ns#\">");
            sb.Append("<rdf:Description rdf:about=\"\" xmlns:dc=\"http://purl.org/dc/elements/1.1/\" xmlns:xmp=\"http://ns.adobe.com/xap/1.0/\"");
            if (m.Rating > 0) sb.Append(CultureInfo.InvariantCulture, $" xmp:Rating=\"{Math.Clamp(m.Rating, 0, 5)}\"");
            sb.Append('>');
            if (!string.IsNullOrWhiteSpace(m.Title)) sb.Append("<dc:title><rdf:Alt><rdf:li xml:lang=\"x-default\">").Append(SecurityElement.Escape(m.Title)).Append("</rdf:li></rdf:Alt></dc:title>");
            if (!string.IsNullOrWhiteSpace(m.Description)) sb.Append("<dc:description><rdf:Alt><rdf:li xml:lang=\"x-default\">").Append(SecurityElement.Escape(m.Description)).Append("</rdf:li></rdf:Alt></dc:description>");
            if (keywords.Count > 0)
            {
                sb.Append("<dc:subject><rdf:Bag>");
                foreach (var k in keywords) sb.Append("<rdf:li>").Append(SecurityElement.Escape(k)).Append("</rdf:li>");
                sb.Append("</rdf:Bag></dc:subject>");
            }

            sb.Append("</rdf:Description></rdf:RDF></x:xmpmeta><?xpacket end=\"w\"?>");
            var bytes = Encoding.UTF8.GetBytes(sb.ToString());
            if (bytes.Length + XmpHeader.Length <= MaxSegmentPayload || keywords.Count == 0) return bytes;
            keywords.RemoveAt(keywords.Count - 1); // keep the packet inside a single APP1 segment
        }
    }
}
