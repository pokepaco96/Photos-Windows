using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;
using PhotosWindows.Application.Devices;
using PhotosWindows.Domain;
using Windows.Devices.Enumeration;
using Windows.Devices.Portable;
using Windows.Storage;
using Windows.Storage.FileProperties;

namespace PhotosWindows.Platform.Windows;

/// <summary>
/// Portable devices (phones, cameras) through the Windows Runtime wrapper over Windows Portable Devices:
/// <see cref="StorageDevice"/> gives a <see cref="StorageFolder"/> for each WPD device (MTP or PTP, the protocol an
/// iPhone uses over USB), which is then browsed with the ordinary storage API. Only documented, public Windows APIs
/// are used; nothing is installed and no device credential or trust record is read or stored.
/// <para>
/// Deleting is deliberately not offered: <see cref="IPortableDeviceSession.CanDelete"/> is false until deletion has
/// been verified on real hardware with this driver stack (see docs/research/IPHONE_USB_REVIEW.md).
/// </para>
/// </summary>
public sealed class WinRtPortableDeviceProvider(ILogger<WinRtPortableDeviceProvider> logger) : IPortableMediaDeviceProvider
{
    /// <summary>DCIM trees are shallow (DCIM/100APPLE/…); the limit only guards against a misbehaving device.</summary>
    private const int MaxDepth = 6;

    public bool IsSupported => true;

    public async Task<IReadOnlyList<PortableDevice>> GetDevicesAsync(CancellationToken cancellationToken)
    {
        var result = new List<PortableDevice>();
        try
        {
            var found = await DeviceInformation.FindAllAsync(StorageDevice.GetDeviceSelector()).AsTask(cancellationToken).ConfigureAwait(false);
            foreach (var info in found)
            {
                cancellationToken.ThrowIfCancellationRequested();
                result.Add(new PortableDevice(info.Id, string.IsNullOrWhiteSpace(info.Name) ? "USB device" : info.Name, await ProbeAsync(info.Id).ConfigureAwait(false)));
            }
        }
        catch (Exception ex) when (IsDeviceError(ex))
        {
            logger.LogWarning("Portable devices could not be listed ({Error})", ex.GetType().Name);
        }

        return result;
    }

    public async Task<IPortableDeviceSession> OpenAsync(string deviceId, CancellationToken cancellationToken)
    {
        try
        {
            var info = await DeviceInformation.CreateFromIdAsync(deviceId).AsTask(cancellationToken).ConfigureAwait(false);
            var root = StorageDevice.FromId(deviceId);
            var access = await ProbeAsync(deviceId).ConfigureAwait(false);
            return new Session(new PortableDevice(deviceId, string.IsNullOrWhiteSpace(info.Name) ? "USB device" : info.Name, access), root);
        }
        catch (Exception ex) when (IsDeviceError(ex))
        {
            throw new DeviceUnavailableException("The device is not available.", ex);
        }
    }

    /// <summary>A locked or untrusted phone is listed by Windows but exposes no storage at all.</summary>
    private static async Task<DeviceAccess> ProbeAsync(string deviceId)
    {
        try
        {
            var root = StorageDevice.FromId(deviceId);
            var items = await root.GetItemsAsync(0, 1);
            return items.Count > 0 ? DeviceAccess.Ready : DeviceAccess.NoAccess;
        }
        catch (Exception ex) when (IsDeviceError(ex))
        {
            return DeviceAccess.NoAccess;
        }
    }

    private static bool IsDeviceError(Exception ex) =>
        ex is COMException or FileNotFoundException or UnauthorizedAccessException or IOException or InvalidOperationException or ArgumentException;

    private sealed class Session(PortableDevice device, StorageFolder root) : IPortableDeviceSession
    {
        private readonly Dictionary<string, (StorageFolder Folder, string FolderId, string Name)> _objects = new(StringComparer.Ordinal);

        public PortableDevice Device { get; } = device;

        public bool CanDelete => false;

        public async Task<IReadOnlyList<DeviceMediaItem>> ListMediaAsync(IProgress<int>? progress, CancellationToken cancellationToken)
        {
            var items = new List<DeviceMediaItem>();
            _objects.Clear();
            try
            {
                await WalkAsync(root, string.Empty, 0, items, progress, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (IsDeviceError(ex))
            {
                throw new DeviceUnavailableException("The device stopped answering while its content was listed.", ex);
            }

            return items;
        }

        private async Task WalkAsync(StorageFolder folder, string folderId, int depth, List<DeviceMediaItem> items, IProgress<int>? progress, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            foreach (var file in await folder.GetFilesAsync().AsTask(ct).ConfigureAwait(false))
            {
                ct.ThrowIfCancellationRequested();
                var type = MediaFormats.FromExtension(file.Name)?.MediaType;
                if (type is null) continue; // only photos and videos are offered
                var item = await DescribeAsync(file, folderId, ObjectId(folderId, file.Name)).ConfigureAwait(false);
                _objects[item.ObjectId] = (folder, folderId, file.Name);
                items.Add(item);
                if (items.Count % 25 == 0) progress?.Report(items.Count);
            }

            if (depth >= MaxDepth) return;
            foreach (var sub in await folder.GetFoldersAsync().AsTask(ct).ConfigureAwait(false))
            {
                await WalkAsync(sub, folderId.Length == 0 ? sub.Name : folderId + "/" + sub.Name, depth + 1, items, progress, ct).ConfigureAwait(false);
            }

            progress?.Report(items.Count);
        }

        private static string ObjectId(string folderId, string name) => folderId + "/" + name;

        private static async Task<DeviceMediaItem> DescribeAsync(StorageFile file, string folderId, string objectId)
        {
            long? size = null;
            DateTime? created = null;
            try
            {
                BasicProperties properties = await file.GetBasicPropertiesAsync();
                if (properties.Size > 0) size = (long)properties.Size;
                var date = file.DateCreated;
                if (date.Year > 1980) created = date.LocalDateTime;
            }
            catch (Exception ex) when (IsDeviceError(ex))
            {
                // Some devices do not report size or date for every object: they are optional.
            }

            return new DeviceMediaItem(objectId, folderId, file.Name, size, created, MediaFormats.FromExtension(file.Name)?.MediaType);
        }

        private async Task<StorageFile> FileAsync(string objectId, CancellationToken ct)
        {
            if (!_objects.TryGetValue(objectId, out var entry)) throw new FileNotFoundException("Unknown object for this session.");
            try
            {
                // Looked up again by name in its folder every time: a stale handle is never trusted.
                var item = await entry.Folder.TryGetItemAsync(entry.Name).AsTask(ct).ConfigureAwait(false);
                return item as StorageFile ?? throw new FileNotFoundException("The object is no longer on the device.");
            }
            catch (Exception ex) when (ex is COMException or UnauthorizedAccessException)
            {
                throw new DeviceUnavailableException("The device stopped answering.", ex);
            }
        }

        public async Task<DeviceMediaItem?> GetItemAsync(string objectId, CancellationToken cancellationToken)
        {
            if (!_objects.TryGetValue(objectId, out var entry)) return null;
            try
            {
                var file = await FileAsync(objectId, cancellationToken).ConfigureAwait(false);
                return await DescribeAsync(file, entry.FolderId, objectId).ConfigureAwait(false);
            }
            catch (FileNotFoundException)
            {
                return null;
            }
        }

        public async Task<Stream> OpenReadAsync(string objectId, CancellationToken cancellationToken)
        {
            var file = await FileAsync(objectId, cancellationToken).ConfigureAwait(false);
            try
            {
                return new DeviceStream(await file.OpenStreamForReadAsync().ConfigureAwait(false));
            }
            catch (Exception ex) when (ex is COMException or UnauthorizedAccessException or FileNotFoundException)
            {
                throw new DeviceUnavailableException("The item could not be read from the device.", ex);
            }
        }

        public async Task<byte[]?> GetThumbnailAsync(string objectId, int size, CancellationToken cancellationToken)
        {
            try
            {
                var file = await FileAsync(objectId, cancellationToken).ConfigureAwait(false);
                using var thumbnail = await file.GetThumbnailAsync(ThumbnailMode.SingleItem, (uint)Math.Clamp(size, 32, 1024)).AsTask(cancellationToken).ConfigureAwait(false);
                if (thumbnail is null || thumbnail.Size == 0) return null;
                using var stream = thumbnail.AsStreamForRead();
                using var memory = new MemoryStream();
                await stream.CopyToAsync(memory, cancellationToken).ConfigureAwait(false);
                return memory.ToArray();
            }
            catch (Exception ex) when (IsDeviceError(ex))
            {
                return null; // a missing preview is not an error
            }
        }

        public Task DeleteAsync(string objectId, CancellationToken cancellationToken) =>
            throw new NotSupportedException("Deleting from this device has not been verified and is not enabled.");

        public ValueTask DisposeAsync()
        {
            _objects.Clear();
            return ValueTask.CompletedTask;
        }
    }

    /// <summary>Turns the COM errors of an unplugged device into <see cref="DeviceUnavailableException"/> while reading.</summary>
    private sealed class DeviceStream(Stream inner) : Stream
    {
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

        public override int Read(byte[] buffer, int offset, int count)
        {
            try
            {
                return inner.Read(buffer, offset, count);
            }
            catch (Exception ex) when (ex is COMException or UnauthorizedAccessException or FileNotFoundException)
            {
                throw new DeviceUnavailableException("The device was disconnected while reading.", ex);
            }
        }

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            try
            {
                return await inner.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is COMException or UnauthorizedAccessException or FileNotFoundException || (ex is IOException && ex is not DeviceUnavailableException))
            {
                throw new DeviceUnavailableException("The device was disconnected while reading.", ex);
            }
        }

        public override void Flush()
        {
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (disposing) inner.Dispose();
            base.Dispose(disposing);
        }
    }
}
