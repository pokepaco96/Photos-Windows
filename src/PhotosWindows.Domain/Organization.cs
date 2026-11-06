namespace PhotosWindows.Domain;

public enum AlbumKind
{
    Manual = 0,
    Smart = 1,
}

/// <summary>Logical collection. Adding an asset never duplicates its file.</summary>
public sealed class Album
{
    public long Id { get; set; }
    public Guid Uid { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = string.Empty;
    public AlbumKind Kind { get; set; }
    public long? FolderId { get; set; }
    public SmartAlbumDefinition? SmartDefinition { get; set; }
    public int SortOrder { get; set; }
    public DateTime CreatedUtc { get; set; }
    public int ItemCount { get; set; }
    public long? CoverAssetId { get; set; }
}

public sealed class AlbumFolder
{
    public long Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public long? ParentId { get; set; }
    public int SortOrder { get; set; }
}

public sealed record Keyword(long Id, string Name, int UsageCount);

public static class LibraryRules
{
    /// <summary>Items in Recently Deleted are purged after this period (catalog entry only by default).</summary>
    public static readonly TimeSpan TrashRetention = TimeSpan.FromDays(30);

    public const int MaxNameLength = 200;

    public static string NormalizeName(string? name, string paramName)
    {
        var trimmed = (name ?? string.Empty).Trim();
        if (trimmed.Length == 0)
        {
            throw new ArgumentException("The name cannot be empty.", paramName);
        }

        if (trimmed.Length > MaxNameLength)
        {
            throw new ArgumentException($"The name cannot exceed {MaxNameLength} characters.", paramName);
        }

        return trimmed;
    }
}
