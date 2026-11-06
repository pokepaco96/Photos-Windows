namespace PhotosWindows.Domain;

public enum AssetSort
{
    CaptureDateAscending = 0,
    CaptureDateDescending = 1,
    ImportDateDescending = 2,
    FileNameAscending = 3,
    /// <summary>Manual order of a regular album.</summary>
    AlbumOrder = 4,
}

/// <summary>Built-in library scopes that map to sidebar sections.</summary>
public enum LibraryScope
{
    All = 0,
    Photos = 1,
    Videos = 2,
    Favorites = 3,
    RecentlyAdded = 4,
    Hidden = 5,
    RecentlyDeleted = 6,
    Edited = 7,
    WithLocation = 8,
    LivePhotos = 9,
    Raw = 10,
}

/// <summary>Combinable filter set used by the grid, the search page and smart albums.</summary>
public sealed record AssetQuery
{
    public LibraryScope Scope { get; init; } = LibraryScope.All;
    public string? Text { get; init; }
    public MediaType? MediaType { get; init; }
    public bool? Favorite { get; init; }
    public bool? Edited { get; init; }
    public bool? HasLocation { get; init; }
    public DateTime? From { get; init; }
    public DateTime? To { get; init; }
    public string? Camera { get; init; }
    public string? Lens { get; init; }
    public string? Format { get; init; }
    public IReadOnlyList<string> Keywords { get; init; } = [];
    public long? AlbumId { get; init; }
    public int? MinRating { get; init; }
    /// <summary>Optional bounding box: (minLat, minLon, maxLat, maxLon).</summary>
    public (double MinLat, double MinLon, double MaxLat, double MaxLon)? Area { get; init; }
    /// <summary>Rules of a smart album, combined (AND) with the other filters.</summary>
    public SmartAlbumDefinition? Smart { get; init; }
    public AssetSort Sort { get; init; } = AssetSort.CaptureDateDescending;
    public int? Limit { get; init; }
}

public enum SmartMatch
{
    All = 0,
    Any = 1,
}

public enum SmartField
{
    MediaType = 0,
    Favorite = 1,
    Keyword = 2,
    CaptureDate = 3,
    Camera = 4,
    Lens = 5,
    FileName = 6,
    Title = 7,
    Rating = 8,
    Edited = 9,
    HasLocation = 10,
    Album = 11,
    Format = 12,
    ImportDate = 13,
}

public enum SmartOperator
{
    Is = 0,
    IsNot = 1,
    Contains = 2,
    DoesNotContain = 3,
    Before = 4,
    After = 5,
    InLastDays = 6,
    GreaterOrEqual = 7,
    LessOrEqual = 8,
}

public sealed record SmartRule(SmartField Field, SmartOperator Operator, string Value);

public sealed record SmartAlbumDefinition(SmartMatch Match, IReadOnlyList<SmartRule> Rules)
{
    public static SmartAlbumDefinition Empty { get; } = new(SmartMatch.All, []);
}
