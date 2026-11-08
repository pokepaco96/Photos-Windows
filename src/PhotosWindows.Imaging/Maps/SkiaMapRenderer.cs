using Microsoft.Extensions.Logging;
using PhotosWindows.Application.Abstractions;
using PhotosWindows.Application.Places;
using PhotosWindows.Domain;
using SkiaSharp;

namespace PhotosWindows.Imaging.Maps;

/// <summary>
/// Renders the embedded Natural Earth basemap with SkiaSharp (no network, no tiles). Paths are built once in world
/// units and drawn through a canvas matrix, once per horizontal world copy visible in the view.
/// </summary>
public sealed class SkiaMapRenderer : IMapRenderer, IDisposable
{
    private readonly Lazy<State> _state;
    private readonly ILogger<SkiaMapRenderer>? _logger;
    private readonly Lock _lock = new();

    public SkiaMapRenderer(ILogger<SkiaMapRenderer>? logger = null)
        : this(WorldBasemap.LoadEmbedded, logger)
    {
    }

    /// <summary>For tests: supply the map loader (e.g. one that throws, to simulate a corrupt file).</summary>
    public SkiaMapRenderer(Func<WorldBasemap> load, ILogger<SkiaMapRenderer>? logger = null)
    {
        ArgumentNullException.ThrowIfNull(load);
        _logger = logger;
        _state = new Lazy<State>(() =>
        {
            try
            {
                return new State(load(), null);
            }
            catch (Exception ex) when (ex is InvalidDataException or IOException)
            {
                _logger?.LogError(ex, "World map unavailable");
                return new State(null, ex.Message);
            }
        });
    }

    public bool IsAvailable => _state.Value.Map is not null;
    public string? UnavailableReason => _state.Value.Error;
    public string Attribution => "Natural Earth";

    public RasterImage Render(MapView view, MapTheme theme)
    {
        ArgumentNullException.ThrowIfNull(view);
        var palette = theme == MapTheme.Dark ? Palette.Dark : Palette.Light;
        var info = new SKImageInfo(view.Width, view.Height, SKColorType.Bgra8888, SKAlphaType.Premul);
        using var bitmap = new SKBitmap(info);
        using (var canvas = new SKCanvas(bitmap))
        {
            canvas.Clear(palette.Water);
            var state = _state.Value;
            if (state.Map is not null)
            {
                lock (_lock)
                {
                    Draw(canvas, view, state, palette);
                }
            }
        }

        var pixels = new byte[view.Width * view.Height * 4];
        System.Runtime.InteropServices.Marshal.Copy(bitmap.GetPixels(), pixels, 0, pixels.Length);
        return new RasterImage(view.Width, view.Height, pixels);
    }

    private static void Draw(SKCanvas canvas, MapView view, State state, Palette palette)
    {
        var ws = (float)view.WorldSize;
        var land = view.Zoom < 3 ? state.Path("land110") : state.Path("land50");
        var lakes = view.Zoom >= 3 ? state.Path("lakes50") : null;
        var borders = view.Zoom >= 1.5 ? state.Path("borders50") : null;

        using var fillLand = new SKPaint { Color = palette.Land, IsAntialias = true, Style = SKPaintStyle.Fill };
        using var coast = new SKPaint { Color = palette.Coast, IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = 1f / ws };
        using var fillLake = new SKPaint { Color = palette.Water, IsAntialias = true, Style = SKPaintStyle.Fill };
        using var border = new SKPaint { Color = palette.Border, IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = (view.Zoom >= 4 ? 1.2f : 0.8f) / ws };

        // Horizontal world copies that intersect the view.
        var left = view.CenterX - view.Width / 2.0 / ws;
        var right = view.CenterX + view.Width / 2.0 / ws;
        for (var k = (int)Math.Floor(left); k <= (int)Math.Floor(right); k++)
        {
            canvas.Save();
            canvas.Translate((float)(view.Width / 2.0 + (k - view.CenterX) * ws), (float)(view.Height / 2.0 - view.CenterY * ws));
            canvas.Scale(ws);
            if (land is not null)
            {
                canvas.DrawPath(land, fillLand);
                canvas.DrawPath(land, coast);
            }

            if (lakes is not null) canvas.DrawPath(lakes, fillLake);
            if (borders is not null) canvas.DrawPath(borders, border);
            canvas.Restore();
        }
    }

    public void Dispose()
    {
        if (_state.IsValueCreated) _state.Value.Dispose();
    }

    private sealed record Palette(SKColor Water, SKColor Land, SKColor Coast, SKColor Border)
    {
        public static readonly Palette Light = new(new SKColor(0xD6, 0xE4, 0xEF), new SKColor(0xF3, 0xF0, 0xE8), new SKColor(0xB8, 0xC9, 0xD8), new SKColor(0xB5, 0xAD, 0xA0));
        public static readonly Palette Dark = new(new SKColor(0x17, 0x1F, 0x2A), new SKColor(0x2C, 0x30, 0x36), new SKColor(0x3A, 0x48, 0x58), new SKColor(0x5F, 0x64, 0x6C));
    }

    private sealed class State(WorldBasemap? map, string? error) : IDisposable
    {
        private readonly Dictionary<string, SKPath?> _paths = [];

        public WorldBasemap? Map { get; } = map;
        public string? Error { get; } = error;

        public SKPath? Path(string layer)
        {
            if (_paths.TryGetValue(layer, out var cached)) return cached;
            var data = Map?[layer];
            SKPath? path = null;
            if (data is not null)
            {
                using var builder = new SKPathBuilder { FillType = SKPathFillType.EvenOdd };
                foreach (var ring in data.Rings)
                {
                    builder.MoveTo(ring[0], ring[1]);
                    for (var i = 2; i < ring.Length; i += 2) builder.LineTo(ring[i], ring[i + 1]);
                    if (data.Kind == BasemapLayerKind.Fill) builder.Close();
                }

                path = builder.Detach();
            }

            _paths[layer] = path;
            return path;
        }

        public void Dispose()
        {
            foreach (var p in _paths.Values) p?.Dispose();
        }
    }
}
