using PhotosWindows.Application.Abstractions;
using PhotosWindows.Domain;

namespace PhotosWindows.Application.Places;

public enum MapTheme
{
    Light = 0,
    Dark = 1,
}

/// <summary>
/// Draws the offline basemap for a view. Implementations must not touch the network. The UI only depends on this
/// interface, so the built-in Natural Earth renderer can be replaced (e.g. by a user-installed MBTiles reader).
/// </summary>
public interface IMapRenderer
{
    /// <summary>False when the map data is missing or failed its integrity check; Places then offers list mode only.</summary>
    bool IsAvailable { get; }

    /// <summary>Why the map is unavailable (diagnostics only), or null.</summary>
    string? UnavailableReason { get; }

    /// <summary>Attribution to show on the map, e.g. "Natural Earth (public domain)".</summary>
    string Attribution { get; }

    /// <summary>Renders the basemap as opaque BGRA pixels of the view's size.</summary>
    RasterImage Render(MapView view, MapTheme theme);
}
