using System.Security.Cryptography;
using Microsoft.Extensions.Logging;
using PhotosWindows.Application.Abstractions;
using PhotosWindows.Application.Common;
using PhotosWindows.Application.Import;
using PhotosWindows.Domain;

namespace PhotosWindows.Application.Devices;

public enum PhoneImportMode
{
    /// <summary>Copy to the library; the device is only read.</summary>
    KeepOnDevice = 0,
    /// <summary>Copy, verify, and only then delete each verified item from the device.</summary>
    DeleteAfterVerifiedCopy = 1,
}

public enum PhoneItemStatus
{
    /// <summary>A new asset file in the library, verified against what the device sent.</summary>
    Imported = 0,
    /// <summary>The library already had this exact content; the existing managed file was verified.</summary>
    AlreadyInLibrary = 1,
    /// <summary>The device stopped answering before or while this item was read.</summary>
    Unavailable = 2,
    /// <summary>Read from the device but not stored (truncated, unsupported, or the import rejected it).</summary>
    Failed = 3,
}

public sealed record PhoneItemOutcome(DeviceMediaItem Item, PhoneItemStatus Status)
{
    /// <summary>A managed file with the same SHA-256 exists, was re-read from disk and matched.</summary>
    public bool Verified { get; init; }
    /// <summary>The bytes received do not look like what the name says (e.g. a JPEG under an .HEIC name): the device converted it.</summary>
    public bool FormatMismatch { get; init; }
    public bool DeletedFromDevice { get; init; }
    /// <summary>Why a verified item was left on the device although deletion was requested, or null.</summary>
    public string? KeptReason { get; init; }
    public string Sha256 { get; init; } = string.Empty;
}

public sealed record PhoneImportProgress(string Stage, int Done, int Total, string CurrentName);

public sealed record PhoneImportResult(IReadOnlyList<PhoneItemOutcome> Items, bool DeviceLost, bool Cancelled)
{
    public int Imported => Items.Count(i => i.Status == PhoneItemStatus.Imported);
    public int AlreadyInLibrary => Items.Count(i => i.Status == PhoneItemStatus.AlreadyInLibrary);
    public int Unavailable => Items.Count(i => i.Status == PhoneItemStatus.Unavailable);
    public int Failed => Items.Count(i => i.Status == PhoneItemStatus.Failed);
    public int Converted => Items.Count(i => i.FormatMismatch);
    public int Deleted => Items.Count(i => i.DeletedFromDevice);
}

/// <summary>
/// The user's explicit, informed consent to delete from the device. It can only be built with the exact number of
/// items the user was shown, so a stale or accidental call cannot authorise a deletion.
/// </summary>
public sealed record DeviceDeleteConsent(int ItemCount, DateTime ConfirmedUtc);

/// <summary>
/// Imports from a portable device into the open library, and optionally deletes from the device afterwards.
/// <para>
/// Safety contract. (1) Items are streamed to a private staging folder outside the library, then imported with the
/// normal <see cref="ImportService"/> in copy mode: that service still never deletes or modifies its sources.
/// (2) An item counts as verified only when a managed file of the library has the SHA-256 of the bytes the device
/// sent and that file was re-read from disk. (3) Deletion happens only in <see cref="PhoneImportMode.DeleteAfterVerifiedCopy"/>,
/// only with a <see cref="DeviceDeleteConsent"/> matching the selection, only when the session says it can delete,
/// only for items whose whole capture (photo + Live Photo clip + RAW) was verified, only when what was received
/// matches the declared format, and only after the object was looked up again by id and still has the same name,
/// size, date and folder. Cancellation and device loss stop deleting immediately. Anything else stays on the device.
/// </para>
/// </summary>
public sealed class PhoneImportCoordinator
{
    private readonly ImportService _import;
    private readonly IAssetRepository _assets;
    private readonly ILibraryContext _context;
    private readonly IAppPaths _paths;
    private readonly ILogger<PhoneImportCoordinator> _logger;

    public PhoneImportCoordinator(ImportService import, IAssetRepository assets, ILibraryContext context, IAppPaths paths, ILogger<PhoneImportCoordinator> logger)
    {
        _import = import;
        _assets = assets;
        _context = context;
        _paths = paths;
        _logger = logger;
    }

    public string StagingRoot => Path.Combine(_paths.AppDataPath, "device-staging");

    /// <summary>
    /// The selection plus the other parts of the same captures (so a Live Photo's clip always travels with its photo).
    /// </summary>
    public static IReadOnlyList<DeviceMediaItem> WithCompanions(IReadOnlyCollection<DeviceMediaItem> selection, IReadOnlyCollection<DeviceMediaItem> all)
    {
        var keys = selection.Select(i => i.CaptureKey).ToHashSet(StringComparer.Ordinal);
        return all.Where(i => keys.Contains(i.CaptureKey)).OrderBy(i => i.FolderId, StringComparer.Ordinal).ThenBy(i => i.Name, StringComparer.OrdinalIgnoreCase).ToList();
    }

    public async Task<PhoneImportResult> ImportAsync(
        IPortableDeviceSession session,
        IReadOnlyCollection<DeviceMediaItem> selection,
        IReadOnlyCollection<DeviceMediaItem> allDeviceItems,
        PhoneImportMode mode,
        DeviceDeleteConsent? consent,
        IProgress<PhoneImportProgress>? progress,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(session);
        var library = _context.RequireCurrent();
        var items = WithCompanions(selection, allDeviceItems);
        var delete = mode == PhoneImportMode.DeleteAfterVerifiedCopy;
        if (delete)
        {
            if (!session.CanDelete) throw new InvalidOperationException("This device does not support verified deletion.");
            if (consent is null || consent.ItemCount != items.Count) throw new InvalidOperationException("Deleting from the device was not confirmed for this selection.");
        }

        var staging = Path.Combine(StagingRoot, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(staging);
        var received = new Dictionary<string, Received>(StringComparer.Ordinal);
        var outcomes = new Dictionary<string, PhoneItemOutcome>(StringComparer.Ordinal);
        var deviceLost = false;
        var cancelled = false;

        try
        {
            // 1. Read from the device into staging. Nothing touches the library or the device yet.
            var done = 0;
            foreach (var item in items)
            {
                if (cancellationToken.IsCancellationRequested)
                {
                    cancelled = true;
                    break;
                }

                progress?.Report(new PhoneImportProgress("download", done++, items.Count, item.Name));
                if (deviceLost)
                {
                    outcomes[item.ObjectId] = new PhoneItemOutcome(item, PhoneItemStatus.Unavailable);
                    continue;
                }

                try
                {
                    var file = await DownloadAsync(session, item, staging, cancellationToken).ConfigureAwait(false);
                    if (file is null) outcomes[item.ObjectId] = new PhoneItemOutcome(item, PhoneItemStatus.Failed);
                    else received[item.ObjectId] = file;
                }
                catch (OperationCanceledException)
                {
                    cancelled = true;
                    break;
                }
                catch (DeviceUnavailableException)
                {
                    deviceLost = true;
                    outcomes[item.ObjectId] = new PhoneItemOutcome(item, PhoneItemStatus.Unavailable);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    _logger.LogWarning("Device import: an item could not be read ({Error})", ex.GetType().Name);
                    outcomes[item.ObjectId] = new PhoneItemOutcome(item, PhoneItemStatus.Failed);
                }
            }

            foreach (var item in items.Where(i => !received.ContainsKey(i.ObjectId) && !outcomes.ContainsKey(i.ObjectId)))
            {
                outcomes[item.ObjectId] = new PhoneItemOutcome(item, PhoneItemStatus.Unavailable); // not reached before cancelling
            }

            // 2. Normal import of what arrived (copy mode). A cancelled run still stores what was fully received:
            //    those bytes are complete files, and nothing is deleted from the device after a cancellation.
            IReadOnlySet<long> newAssets = new HashSet<long>();
            if (received.Count > 0)
            {
                progress?.Report(new PhoneImportProgress("import", 0, received.Count, string.Empty));
                var result = await _import.ImportAsync(new ImportRequest([staging], ImportMode.CopyToLibrary), null, CancellationToken.None).ConfigureAwait(false);
                newAssets = result.NewAssetIds.ToHashSet();
            }

            // 3. Verify every received item against a managed file re-read from disk.
            var verifiedCount = 0;
            foreach (var (objectId, file) in received)
            {
                progress?.Report(new PhoneImportProgress("verify", verifiedCount++, received.Count, file.Item.Name));
                var (verified, isNew) = await VerifyAsync(library, file.Sha256, newAssets).ConfigureAwait(false);
                outcomes[objectId] = new PhoneItemOutcome(file.Item, !verified ? PhoneItemStatus.Failed : isNew ? PhoneItemStatus.Imported : PhoneItemStatus.AlreadyInLibrary)
                {
                    Verified = verified,
                    FormatMismatch = file.FormatMismatch,
                    Sha256 = file.Sha256,
                };
            }

            // 4. Delete from the device: only whole, verified, unconverted captures, re-identified one by one.
            if (delete && !cancelled && !deviceLost)
            {
                deviceLost = await DeleteVerifiedAsync(session, items, outcomes, progress, cancellationToken).ConfigureAwait(false);
            }
            else if (delete)
            {
                foreach (var key in outcomes.Keys.ToList())
                {
                    if (outcomes[key].Verified) outcomes[key] = outcomes[key] with { KeptReason = cancelled ? "cancelled" : "device-lost" };
                }
            }
        }
        finally
        {
            TryDeleteStaging(staging);
        }

        return new PhoneImportResult(items.Select(i => outcomes[i.ObjectId]).ToList(), deviceLost, cancelled || cancellationToken.IsCancellationRequested);
    }

    /// <summary>Removes staging folders left by a crash. Staging never holds the only copy of anything.</summary>
    public void CleanStaging()
    {
        if (!Directory.Exists(StagingRoot)) return;
        foreach (var dir in Directory.EnumerateDirectories(StagingRoot)) TryDeleteStaging(dir);
    }

    private static async Task<Received?> DownloadAsync(IPortableDeviceSession session, DeviceMediaItem item, string staging, CancellationToken ct)
    {
        // One staging sub-folder per device folder: IMG_0001 of 100APPLE and of 101APPLE never collide, and the
        // parts of a Live Photo stay side by side so the importer pairs them.
        var folder = Path.Combine(staging, PathSafety.SanitizeFileName(item.FolderId.Replace('/', '_').Replace('\\', '_') is { Length: > 0 } f ? f : "root"));
        Directory.CreateDirectory(folder);
        var target = Path.Combine(folder, PathSafety.SanitizeFileName(Path.GetFileName(item.Name)));
        if (File.Exists(target)) return null; // two objects with the same name in one folder: ambiguous, not imported
        var partial = target + PathSafety.PartialSuffix;
        using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var header = new byte[MediaFormats.SniffLength];
        var headerLength = 0;
        long total = 0;
        try
        {
            await using (var input = await session.OpenReadAsync(item.ObjectId, ct).ConfigureAwait(false))
            await using (var output = new FileStream(partial, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1 << 20, FileOptions.Asynchronous))
            {
                var buffer = new byte[1 << 20];
                int read;
                while ((read = await input.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0)
                {
                    if (headerLength < header.Length)
                    {
                        var take = Math.Min(header.Length - headerLength, read);
                        Array.Copy(buffer, 0, header, headerLength, take);
                        headerLength += take;
                    }

                    sha.AppendData(buffer, 0, read);
                    await output.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);
                    total += read;
                }

                await output.FlushAsync(ct).ConfigureAwait(false);
                output.Flush(flushToDisk: true);
            }

            // A stream that ended early is not the file: never stored, never a reason to delete anything.
            if (total == 0 || (item.Size is { } size && size != total))
            {
                File.Delete(partial);
                return null;
            }

            File.Move(partial, target);
            var sniffed = MediaFormats.Sniff(header.AsSpan(0, headerLength), Path.GetExtension(item.Name));
            var mismatch = !MediaFormats.SignatureMatchesExtension(sniffed, item.Name);
            return new Received(item, Convert.ToHexStringLower(sha.GetHashAndReset()), mismatch);
        }
        catch
        {
            try
            {
                if (File.Exists(partial)) File.Delete(partial);
            }
            catch (IOException)
            {
                // The whole staging folder is removed at the end anyway.
            }

            throw;
        }
    }

    private async Task<(bool Verified, bool IsNew)> VerifyAsync(LibraryInfo library, string sha256, IReadOnlySet<long> newAssets)
    {
        var files = await _assets.FindFilesByHashAsync(sha256, CancellationToken.None).ConfigureAwait(false);
        foreach (var file in files.Where(f => f.StorageMode == StorageMode.Managed))
        {
            var path = library.Resolve(file);
            if (!File.Exists(path)) continue;
            // Re-read the stored file: the catalog row alone is not proof that the bytes are safely on disk.
            if (await FileHashing.Sha256Async(path, CancellationToken.None).ConfigureAwait(false) == sha256) return (true, newAssets.Contains(file.AssetId));
        }

        return (false, false);
    }

    /// <summary>Returns true when the device was lost while deleting.</summary>
    private async Task<bool> DeleteVerifiedAsync(IPortableDeviceSession session, IReadOnlyList<DeviceMediaItem> items, Dictionary<string, PhoneItemOutcome> outcomes,
        IProgress<PhoneImportProgress>? progress, CancellationToken ct)
    {
        var done = 0;
        foreach (var capture in items.GroupBy(i => i.CaptureKey))
        {
            var parts = capture.ToList();
            void Keep(string reason)
            {
                foreach (var p in parts.Where(p => outcomes[p.ObjectId].Verified)) outcomes[p.ObjectId] = outcomes[p.ObjectId] with { KeptReason = reason };
            }

            if (ct.IsCancellationRequested)
            {
                Keep("cancelled");
                continue;
            }

            progress?.Report(new PhoneImportProgress("delete", done, items.Count, parts[0].Name));
            done += parts.Count;
            if (parts.Any(p => !outcomes[p.ObjectId].Verified))
            {
                Keep("incomplete-capture"); // e.g. the photo is safe but its Live Photo clip is not: keep both
                continue;
            }

            if (parts.Any(p => outcomes[p.ObjectId].FormatMismatch))
            {
                Keep("converted"); // what arrived is not the original format: the original stays on the device
                continue;
            }

            try
            {
                // Identify every part again right before deleting; the listing order or ids are never trusted alone.
                var same = true;
                foreach (var p in parts)
                {
                    var now = await session.GetItemAsync(p.ObjectId, ct).ConfigureAwait(false);
                    same &= now is not null && now.Name == p.Name && now.FolderId == p.FolderId && now.Size == p.Size && now.Created == p.Created;
                }

                if (!same)
                {
                    Keep("changed-on-device");
                    continue;
                }

                foreach (var p in parts)
                {
                    // No cancellation token here: a delete that started is allowed to finish cleanly, and the next
                    // capture is not started once cancellation was requested.
                    await session.DeleteAsync(p.ObjectId, CancellationToken.None).ConfigureAwait(false);
                    outcomes[p.ObjectId] = outcomes[p.ObjectId] with { DeletedFromDevice = true };
                }
            }
            catch (OperationCanceledException)
            {
                Keep("cancelled");
            }
            catch (DeviceUnavailableException)
            {
                foreach (var rest in items.Where(i => outcomes[i.ObjectId] is { Verified: true, DeletedFromDevice: false, KeptReason: null }))
                {
                    outcomes[rest.ObjectId] = outcomes[rest.ObjectId] with { KeptReason = "device-lost" };
                }

                return true;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
            {
                _logger.LogWarning("Device import: a verified item could not be deleted from the device ({Error})", ex.GetType().Name);
                foreach (var p in parts.Where(p => !outcomes[p.ObjectId].DeletedFromDevice)) outcomes[p.ObjectId] = outcomes[p.ObjectId] with { KeptReason = "delete-failed" };
            }
        }

        return false;
    }

    private void TryDeleteStaging(string staging)
    {
        try
        {
            if (Directory.Exists(staging)) Directory.Delete(staging, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning("Device import: staging could not be removed ({Error})", ex.GetType().Name);
        }
    }

    private sealed record Received(DeviceMediaItem Item, string Sha256, bool FormatMismatch);
}
