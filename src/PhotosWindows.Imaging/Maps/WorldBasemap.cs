using System.IO.Compression;
using System.Security.Cryptography;
using PhotosWindows.Domain;

namespace PhotosWindows.Imaging.Maps;

public enum BasemapLayerKind
{
    Fill = 0,
    Line = 1,
}

/// <summary>One layer of the basemap: rings (or polylines) already projected to Web Mercator world units.</summary>
public sealed class BasemapLayer
{
    internal BasemapLayer(string name, BasemapLayerKind kind, IReadOnlyList<float[]> rings, int points)
    {
        Name = name;
        Kind = kind;
        Rings = rings;
        PointCount = points;
    }

    public string Name { get; }
    public BasemapLayerKind Kind { get; }

    /// <summary>Each ring is x0,y0,x1,y1,… in world units ([0,1)).</summary>
    public IReadOnlyList<float[]> Rings { get; }

    public int PointCount { get; }
}

/// <summary>
/// The built-in world map: Natural Earth 1:110m/1:50m land, lakes and land borders (public domain), converted by
/// <c>tools/basemap/build_basemap.py</c> into the compact <c>pwmap/1</c> format and embedded in this assembly.
/// </summary>
public sealed class WorldBasemap
{
    /// <summary>SHA-256 of the embedded file, as written by the generator (see world.manifest.json).</summary>
    public const string EmbeddedSha256 = "97ed2537777af1945878ab44b8ced10dfe26a0f85e6f01ffcc855d3115b0e7ec";

    public const string ResourceName = "PhotosWindows.Maps.world.pwmap";
    private static readonly byte[] Magic = "PWMAP\u0001\0\0"u8.ToArray();
    private const double Scale = 10_000;
    private const int MaxRings = 100_000;
    private const int MaxPointsPerRing = 1_000_000;

    private WorldBasemap(IReadOnlyList<BasemapLayer> layers) => Layers = layers;

    public IReadOnlyList<BasemapLayer> Layers { get; }

    public BasemapLayer? this[string name] => Layers.FirstOrDefault(l => l.Name == name);

    /// <summary>Loads the embedded map after checking its SHA-256. Throws <see cref="InvalidDataException"/> if corrupt.</summary>
    public static WorldBasemap LoadEmbedded()
    {
        using var stream = typeof(WorldBasemap).Assembly.GetManifestResourceStream(ResourceName)
            ?? throw new InvalidDataException("The embedded world map is missing.");
        using var copy = new MemoryStream();
        stream.CopyTo(copy);
        return Load(copy.ToArray(), EmbeddedSha256);
    }

    /// <summary>Parses a pwmap/1 blob. When <paramref name="expectedSha256"/> is given, a mismatch is rejected.</summary>
    public static WorldBasemap Load(byte[] data, string? expectedSha256 = null)
    {
        ArgumentNullException.ThrowIfNull(data);
        if (expectedSha256 is not null && !string.Equals(Convert.ToHexStringLower(SHA256.HashData(data)), expectedSha256, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("The world map failed its integrity check (SHA-256 mismatch).");
        }

        if (data.Length < Magic.Length || !data.AsSpan(0, Magic.Length).SequenceEqual(Magic))
        {
            throw new InvalidDataException("Not a pwmap/1 file.");
        }

        try
        {
            using var deflate = new DeflateStream(new MemoryStream(data, Magic.Length, data.Length - Magic.Length), CompressionMode.Decompress);
            using var payload = new MemoryStream();
            deflate.CopyTo(payload);
            var reader = new Reader(payload.ToArray());
            var layerCount = reader.Byte();
            var layers = new List<BasemapLayer>(layerCount);
            for (var l = 0; l < layerCount; l++)
            {
                var kind = reader.Byte() switch
                {
                    0 => BasemapLayerKind.Fill,
                    1 => BasemapLayerKind.Line,
                    var k => throw new InvalidDataException($"Unknown layer kind {k}."),
                };
                var name = reader.Ascii(reader.Byte());
                var ringCount = reader.Count(MaxRings);
                var rings = new List<float[]>(ringCount);
                var total = 0;
                for (var r = 0; r < ringCount; r++)
                {
                    var n = reader.Count(MaxPointsPerRing);
                    var ring = new float[n * 2];
                    long lon = 0, lat = 0;
                    for (var i = 0; i < n; i++)
                    {
                        lon += reader.ZigZag();
                        lat += reader.ZigZag();
                        var (x, y) = WebMercator.ToWorld(lat / Scale, lon / Scale);
                        ring[2 * i] = (float)x;
                        ring[2 * i + 1] = (float)y;
                    }

                    rings.Add(ring);
                    total += n;
                }

                layers.Add(new BasemapLayer(name, kind, rings, total));
            }

            return new WorldBasemap(layers);
        }
        catch (Exception ex) when (ex is EndOfStreamException or IndexOutOfRangeException or ArgumentOutOfRangeException or OverflowException)
        {
            throw new InvalidDataException("The world map is truncated or malformed.", ex);
        }
    }

    private sealed class Reader(byte[] buffer)
    {
        private int _pos;

        public byte Byte() => _pos < buffer.Length ? buffer[_pos++] : throw new EndOfStreamException();

        public string Ascii(int length)
        {
            if (_pos + length > buffer.Length) throw new EndOfStreamException();
            var s = System.Text.Encoding.ASCII.GetString(buffer, _pos, length);
            _pos += length;
            return s;
        }

        public ulong VarUInt()
        {
            ulong value = 0;
            for (var shift = 0; shift < 64; shift += 7)
            {
                var b = Byte();
                value |= (ulong)(b & 0x7F) << shift;
                if ((b & 0x80) == 0) return value;
            }

            throw new InvalidDataException("Varint too long.");
        }

        public long ZigZag()
        {
            var v = VarUInt();
            return (long)(v >> 1) ^ -(long)(v & 1);
        }

        public int Count(int max)
        {
            var v = VarUInt();
            return v <= (ulong)max ? (int)v : throw new InvalidDataException("Count out of range.");
        }
    }
}
