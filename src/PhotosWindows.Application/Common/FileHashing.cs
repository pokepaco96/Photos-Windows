using System.Security.Cryptography;

namespace PhotosWindows.Application.Common;

public static class FileHashing
{
    private const int BufferSize = 1024 * 1024;

    /// <summary>Streams the file through SHA-256 (lower-case hex). The file is opened read-only.</summary>
    public static async Task<string> Sha256Async(string path, CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, BufferSize, FileOptions.Asynchronous | FileOptions.SequentialScan);
        var hash = await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false);
        return Convert.ToHexStringLower(hash);
    }

    /// <summary>
    /// Copies <paramref name="source"/> to a NEW file <paramref name="destination"/> (never overwrites) while hashing
    /// the bytes read, so the source is read exactly once. The destination is flushed to disk before returning.
    /// On failure or cancellation the partial destination is deleted.
    /// </summary>
    public static async Task<string> CopyWithHashAsync(string source, string destination, CancellationToken cancellationToken)
    {
        using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new byte[BufferSize];
        try
        {
            await using (var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read, BufferSize, FileOptions.Asynchronous | FileOptions.SequentialScan))
            await using (var output = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None, BufferSize, FileOptions.Asynchronous))
            {
                int read;
                while ((read = await input.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) > 0)
                {
                    sha.AppendData(buffer, 0, read);
                    await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
                }

                await output.FlushAsync(cancellationToken).ConfigureAwait(false);
                output.Flush(flushToDisk: true);
            }

            File.SetLastWriteTimeUtc(destination, File.GetLastWriteTimeUtc(source));
            return Convert.ToHexStringLower(sha.GetHashAndReset());
        }
        catch
        {
            TryDelete(destination);
            throw;
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path)) File.Delete(path);
        }
        catch (IOException)
        {
            // Left behind as *.pwpartial; library verification reports and cleans these files.
        }
        catch (UnauthorizedAccessException)
        {
            // Same as above.
        }
    }
}

public static class PathSafety
{
    /// <summary>True when <paramref name="candidate"/> is <paramref name="root"/> or inside it (after full path normalisation).</summary>
    public static bool IsUnder(string candidate, string root)
    {
        var c = Path.GetFullPath(candidate).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var r = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        return c.Equals(r, comparison) || c.StartsWith(r + Path.DirectorySeparatorChar, comparison);
    }

    /// <summary>Replaces characters that are invalid in Windows file names.</summary>
    public static string SanitizeFileName(string name)
    {
        var invalid = new HashSet<char>(Path.GetInvalidFileNameChars()) { '<', '>', ':', '"', '/', '\\', '|', '?', '*' };
        var chars = name.Select(ch => invalid.Contains(ch) || char.IsControl(ch) ? '_' : ch).ToArray();
        var result = new string(chars).Trim().TrimEnd('.');
        return result.Length == 0 ? "untitled" : result;
    }

    /// <summary>
    /// Returns a path in <paramref name="directory"/> that does not exist yet, appending " (n)" before the extension if needed.
    /// </summary>
    public static string UniquePath(string directory, string fileName)
    {
        var safe = SanitizeFileName(fileName);
        var stem = Path.GetFileNameWithoutExtension(safe);
        var ext = Path.GetExtension(safe);
        var candidate = Path.Combine(directory, safe);
        for (var i = 1; File.Exists(candidate) || File.Exists(candidate + PartialSuffix); i++)
        {
            candidate = Path.Combine(directory, $"{stem} ({i}){ext}");
        }

        return candidate;
    }

    public const string PartialSuffix = ".pwpartial";
}
