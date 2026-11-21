using System.Buffers.Binary;

namespace PhotosWindows.Application.Common;

/// <summary>
/// Tells from the HEIF container whether the file stores a thumbnail image (an item referenced with 'thmb' in the
/// 'iref' box of 'meta'), reading only the first boxes of the file.
/// <para>
/// Why: when a HEIC has no stored thumbnail, Windows still "returns" one by decoding and scaling the whole image — as
/// costly as a full decode (575–740 ms, ~350 ms CPU) and with its own HEVC decoder — and WinRT's GetThumbnailAsync
/// throws ArgumentException for about half of them. Measured on Windows 11 with a 60 GB iPhone library: 0 of 30 HEICs
/// without camera EXIF and 14 of 100 camera HEICs had no 'thmb'; for them the app decoded the image twice, and that
/// raised private memory to ~3 GB while only one counted full decode was running.
/// </para>
/// </summary>
public static class HeifThumbnailProbe
{
    /// <summary>Most 'meta' boxes are a few KB; anything above this is treated as unknown.</summary>
    internal const int MaxMetaBytes = 1 << 20;

    /// <summary>True or false when the container says so; null when it cannot be read (callers keep their old behaviour).</summary>
    public static bool? HasThumbnail(string path)
    {
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 4096, FileOptions.SequentialScan);
            return HasThumbnail(stream);
        }
        catch (IOException)
        {
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }
    }

    public static bool? HasThumbnail(Stream stream)
    {
        // Top-level boxes: ftyp, meta, (mdat…). 'meta' comes before the image data in every HEIC seen so far.
        Span<byte> header = stackalloc byte[16];
        for (var i = 0; i < 8; i++)
        {
            if (!TryReadHeader(stream, header, out var size, out var type, out var headerLength)) return null;
            if (type == "meta")
            {
                var body = size - headerLength;
                if (body is < 4 or > MaxMetaBytes) return null;
                var meta = new byte[body];
                if (stream.ReadAtLeast(meta, meta.Length, throwOnEndOfStream: false) != meta.Length) return null;
                return MetaHasThumbnail(meta);
            }

            if (type == "mdat" || size < headerLength) return null;
            if (!stream.CanSeek) return null;
            stream.Seek(size - headerLength, SeekOrigin.Current);
        }

        return null;
    }

    /// <summary>The 'meta' FullBox body: version/flags, then child boxes; looks for a 'thmb' reference in 'iref'.</summary>
    internal static bool? MetaHasThumbnail(ReadOnlySpan<byte> meta)
    {
        var children = meta[4..];
        while (children.Length >= 8)
        {
            if (!TryBox(children, out var size, out var type, out var headerLength)) return null;
            if (type == "iref")
            {
                // FullBox: version (0 → 16-bit item ids, 1 → 32-bit), flags; then SingleItemTypeReference boxes.
                var refs = children[headerLength..(int)size];
                if (refs.Length < 4) return null;
                refs = refs[4..];
                while (refs.Length >= 8)
                {
                    if (!TryBox(refs, out var refSize, out var refType, out _)) return null;
                    if (refType == "thmb") return true;
                    refs = refs[(int)refSize..];
                }

                return false;
            }

            children = children[(int)size..];
        }

        return false; // no 'iref' at all: nothing references a thumbnail
    }

    private static bool TryReadHeader(Stream stream, Span<byte> buffer, out long size, out string type, out int headerLength)
    {
        size = 0;
        type = string.Empty;
        headerLength = 8;
        if (stream.ReadAtLeast(buffer[..8], 8, throwOnEndOfStream: false) != 8) return false;
        size = BinaryPrimitives.ReadUInt32BigEndian(buffer);
        type = System.Text.Encoding.ASCII.GetString(buffer[4..8]);
        if (size == 1)
        {
            if (stream.ReadAtLeast(buffer[8..16], 8, throwOnEndOfStream: false) != 8) return false;
            size = (long)BinaryPrimitives.ReadUInt64BigEndian(buffer[8..16]);
            headerLength = 16;
        }

        return size >= headerLength || size == 0;
    }

    private static bool TryBox(ReadOnlySpan<byte> data, out long size, out string type, out int headerLength)
    {
        size = BinaryPrimitives.ReadUInt32BigEndian(data);
        type = System.Text.Encoding.ASCII.GetString(data[4..8]);
        headerLength = 8;
        if (size == 1)
        {
            if (data.Length < 16) return false;
            size = (long)BinaryPrimitives.ReadUInt64BigEndian(data[8..16]);
            headerLength = 16;
        }
        else if (size == 0)
        {
            size = data.Length; // extends to the end of the parent
        }

        return size >= headerLength && size <= data.Length;
    }
}
