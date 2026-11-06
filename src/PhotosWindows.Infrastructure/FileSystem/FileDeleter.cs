using System.Runtime.Versioning;
using PhotosWindows.Application.Abstractions;

namespace PhotosWindows.Infrastructure.FileSystem;

/// <summary>
/// On Windows, deleted files go to the Recycle Bin so a mistaken permanent deletion can still be undone from Explorer.
/// Elsewhere (developer machines, CI) files are deleted directly.
/// </summary>
public sealed class FileDeleter : IFileDeleter
{
    public bool IsRecoverable => OperatingSystem.IsWindows();

    public void Delete(string path)
    {
        if (OperatingSystem.IsWindows())
        {
            DeleteToRecycleBin(path);
        }
        else
        {
            File.Delete(path);
        }
    }

    [SupportedOSPlatform("windows")]
    private static void DeleteToRecycleBin(string path) =>
        Microsoft.VisualBasic.FileIO.FileSystem.DeleteFile(path, Microsoft.VisualBasic.FileIO.UIOption.OnlyErrorDialogs, Microsoft.VisualBasic.FileIO.RecycleOption.SendToRecycleBin);
}
