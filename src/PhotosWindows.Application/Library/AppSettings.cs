using PhotosWindows.Domain;

namespace PhotosWindows.Application.Library;

public enum AppTheme
{
    System = 0,
    Light = 1,
    Dark = 2,
}

/// <summary>Per-user preferences, stored as JSON under %LOCALAPPDATA%\PhotosWindows. Never holds library content.</summary>
public sealed record AppSettings
{
    public string? LastLibraryPath { get; init; }
    public IReadOnlyList<string> RecentLibraries { get; init; } = [];
    public AppTheme Theme { get; init; } = AppTheme.System;
    public int GridItemSize { get; init; } = 180;
    public AssetSort DefaultSort { get; init; } = AssetSort.CaptureDateDescending;
    public ImportMode DefaultImportMode { get; init; } = ImportMode.CopyToLibrary;
    public bool ImportDuplicates { get; init; }
    public int ThumbnailQuality { get; init; } = 82;
    public bool FirstRunCompleted { get; init; }
    /// <summary>BCP-47 language tag; empty means "follow Windows".</summary>
    public string Language { get; init; } = string.Empty;
    public int SlideshowSeconds { get; init; } = 3;
    public int MaxBackgroundWorkers { get; init; } = Math.Clamp(Environment.ProcessorCount / 2, 1, 4);
    /// <summary>Memories choose favourites and edited photos first for covers and samples.</summary>
    public bool MemoriesPreferFavorites { get; init; } = true;
    /// <summary>Folder last chosen for the originals backup (a local path; kept only on this PC).</summary>
    public string? OriginalsBackupDestination { get; init; }
    /// <summary>Last originals backups run from this PC, newest first (at most 10). Nothing is uploaded.</summary>
    public IReadOnlyList<OriginalsBackupHistoryEntry> OriginalsBackupHistory { get; init; } = [];
    /// <summary>Last Places filters and view mode, restored when the page opens again.</summary>
    public PlacesPreferences Places { get; init; } = new();
}

/// <summary>One line of the local history of originals backups.</summary>
public sealed record OriginalsBackupHistoryEntry(DateTime Utc, string Folder, int Files, long Bytes, string Status);

public enum PlacesViewMode
{
    Map = 0,
    List = 1,
}

/// <summary>Persisted Places state. Holds filters only, never coordinates of the user's photos.</summary>
public sealed record PlacesPreferences
{
    public PlacesViewMode ViewMode { get; init; } = PlacesViewMode.Map;
    /// <summary>When false the map is never drawn and Places shows the list only.</summary>
    public bool MapEnabled { get; init; } = true;
    public MediaType? MediaType { get; init; }
    public bool FavoritesOnly { get; init; }
    public long? AlbumId { get; init; }
    public DateTime? From { get; init; }
    public DateTime? To { get; init; }
}

public interface ISettingsStore
{
    AppSettings Load();
    void Save(AppSettings settings);
}
