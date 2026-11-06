namespace PhotosWindows.Domain;

/// <summary>Kind of logical asset shown in the library.</summary>
public enum MediaType
{
    Photo = 0,
    Video = 1,
}

/// <summary>How the physical file of an asset is stored.</summary>
public enum StorageMode
{
    /// <summary>The file stays where the user had it; the catalog only references it.</summary>
    Referenced = 0,

    /// <summary>The file was copied into the library's <c>originals</c> folder.</summary>
    Managed = 1,
}

/// <summary>Role of a physical file inside a logical asset.</summary>
public enum FileRole
{
    /// <summary>The primary original (what is displayed and edited).</summary>
    Original = 0,

    /// <summary>A RAW file paired with a JPEG/HEIC original of the same shot.</summary>
    RawPair = 1,

    /// <summary>Short motion clip paired with a still photo (Live Photo style capture).</summary>
    LiveVideo = 2,

    /// <summary>Metadata sidecar such as an XMP file.</summary>
    Sidecar = 3,
}

/// <summary>Where the capture date of an asset came from, from most to least trustworthy.</summary>
public enum DateSource
{
    Unknown = 0,
    ExifOriginal = 1,
    ExifDigitized = 2,
    QuickTime = 3,
    XmpSidecar = 4,
    FileName = 5,
    FileModified = 6,
    Manual = 7,
}

public enum ImportMode
{
    /// <summary>Index files in place without moving or copying them.</summary>
    AddInPlace = 0,

    /// <summary>Copy originals into the library-managed folder.</summary>
    CopyToLibrary = 1,
}

public enum ImportJobStatus
{
    Pending = 0,
    Running = 1,
    Completed = 2,
    CompletedWithErrors = 3,
    Cancelled = 4,
    Interrupted = 5,
}

public enum ImportItemStatus
{
    Pending = 0,
    Imported = 1,
    AlreadyInLibrary = 2,
    Duplicate = 3,
    Unsupported = 4,
    Failed = 5,
    Skipped = 6,
    /// <summary>File was attached to another asset (RAW pair, live video, sidecar).</summary>
    Attached = 7,
}

public enum ThumbnailState
{
    Pending = 0,
    Ready = 1,
    Failed = 2,
}
