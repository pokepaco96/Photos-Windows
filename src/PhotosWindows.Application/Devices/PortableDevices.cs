using PhotosWindows.Domain;

namespace PhotosWindows.Application.Devices;

public enum DeviceAccess
{
    /// <summary>Media can be listed and read.</summary>
    Ready = 0,
    /// <summary>The device is connected but exposes no storage: locked, or "Trust this computer" not accepted yet.</summary>
    NoAccess = 1,
}

/// <summary>A phone or camera connected by USB, as the platform exposes it.</summary>
public sealed record PortableDevice(string Id, string Name, DeviceAccess Access);

/// <summary>
/// A photo or video the device exposes over USB. <see cref="ObjectId"/> is only valid during the session that listed
/// it; <see cref="Size"/> and <see cref="Created"/> may be missing when the device does not report them.
/// </summary>
public sealed record DeviceMediaItem(string ObjectId, string FolderId, string Name, long? Size, DateTime? Created, MediaType? MediaType)
{
    /// <summary>Key shared by the parts of one capture (IMG_0001.HEIC + IMG_0001.MOV in the same folder).</summary>
    public string CaptureKey => FolderId + "/" + Path.GetFileNameWithoutExtension(Name).ToUpperInvariant();
}

/// <summary>The device went away or stopped answering (unplugged, locked, trust revoked).</summary>
public sealed class DeviceUnavailableException(string message, Exception? inner = null) : IOException(message, inner);

/// <summary>
/// Finds portable devices. The Windows implementation lives in PhotosWindows.Platform.Windows; Application and
/// Domain never see COM or WinRT types.
/// </summary>
public interface IPortableMediaDeviceProvider
{
    /// <summary>False where the platform has no portable-device support (e.g. when running the core on Linux).</summary>
    bool IsSupported { get; }

    Task<IReadOnlyList<PortableDevice>> GetDevicesAsync(CancellationToken cancellationToken);

    Task<IPortableDeviceSession> OpenAsync(string deviceId, CancellationToken cancellationToken);
}

/// <summary>An open connection to one device. Reading never changes the device.</summary>
public interface IPortableDeviceSession : IAsyncDisposable
{
    PortableDevice Device { get; }

    /// <summary>
    /// True only when deleting from this device through this driver has been verified to work. When false the
    /// "import and delete" option is shown disabled and <see cref="DeleteAsync"/> must not be called.
    /// </summary>
    bool CanDelete { get; }

    /// <summary>Photos and videos the device exposes. Things that exist only in a cloud service are not listed.</summary>
    Task<IReadOnlyList<DeviceMediaItem>> ListMediaAsync(IProgress<int>? progress, CancellationToken cancellationToken);

    /// <summary>The object as it is right now, or null when it no longer exists. Used to re-check identity before deleting.</summary>
    Task<DeviceMediaItem?> GetItemAsync(string objectId, CancellationToken cancellationToken);

    Task<Stream> OpenReadAsync(string objectId, CancellationToken cancellationToken);

    /// <summary>A small preview (encoded image bytes), or null when the device offers none.</summary>
    Task<byte[]?> GetThumbnailAsync(string objectId, int size, CancellationToken cancellationToken);

    Task DeleteAsync(string objectId, CancellationToken cancellationToken);
}

/// <summary>Used where no real provider exists: nothing is ever found.</summary>
public sealed class NoPortableDeviceProvider : IPortableMediaDeviceProvider
{
    public bool IsSupported => false;

    public Task<IReadOnlyList<PortableDevice>> GetDevicesAsync(CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<PortableDevice>>([]);

    public Task<IPortableDeviceSession> OpenAsync(string deviceId, CancellationToken cancellationToken) => throw new DeviceUnavailableException("No portable device support on this platform.");
}

/// <summary>
/// A device simulated by a folder (sub-folders play the role of DCIM/100APPLE…). It exists for automated tests of the
/// interface: the published app uses it only when the QA variable PW_QA_SIMULATED_DEVICE names a folder, and the
/// device is labelled as simulated. It is not, and does not stand for, support for any real phone.
/// </summary>
public sealed class SimulatedFolderDeviceProvider(string root, bool canDelete) : IPortableMediaDeviceProvider
{
    public const string EnvironmentVariable = "PW_QA_SIMULATED_DEVICE";

    public bool IsSupported => true;

    public Task<IReadOnlyList<PortableDevice>> GetDevicesAsync(CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<PortableDevice>>(Directory.Exists(root)
            ? [new PortableDevice("simulated", "Simulated device (QA)", Directory.EnumerateFileSystemEntries(root).Any() ? DeviceAccess.Ready : DeviceAccess.NoAccess)]
            : []);

    public Task<IPortableDeviceSession> OpenAsync(string deviceId, CancellationToken cancellationToken) =>
        Directory.Exists(root)
            ? Task.FromResult<IPortableDeviceSession>(new Session(root, canDelete))
            : throw new DeviceUnavailableException("The device is not connected.");

    private sealed class Session(string root, bool canDelete) : IPortableDeviceSession
    {
        public PortableDevice Device { get; } = new("simulated", "Simulated device (QA)", DeviceAccess.Ready);

        public bool CanDelete => canDelete;

        public Task<IReadOnlyList<DeviceMediaItem>> ListMediaAsync(IProgress<int>? progress, CancellationToken cancellationToken)
        {
            EnsureConnected();
            var items = Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)
                .Where(f => MediaFormats.FromExtension(f) is { MediaType: not null })
                .Order(StringComparer.OrdinalIgnoreCase)
                .Select(Describe)
                .ToList();
            progress?.Report(items.Count);
            return Task.FromResult<IReadOnlyList<DeviceMediaItem>>(items);
        }

        public Task<DeviceMediaItem?> GetItemAsync(string objectId, CancellationToken cancellationToken)
        {
            EnsureConnected();
            var path = Resolve(objectId);
            return Task.FromResult(File.Exists(path) ? Describe(path) : null);
        }

        public Task<Stream> OpenReadAsync(string objectId, CancellationToken cancellationToken)
        {
            EnsureConnected();
            return Task.FromResult<Stream>(new FileStream(Resolve(objectId), FileMode.Open, FileAccess.Read, FileShare.Read));
        }

        public async Task<byte[]?> GetThumbnailAsync(string objectId, int size, CancellationToken cancellationToken)
        {
            EnsureConnected();
            var path = Resolve(objectId);
            // Small still images are their own preview; anything else has none in the simulation.
            return MediaFormats.FromExtension(path)?.MediaType == MediaType.Photo && new FileInfo(path).Length < 8 * 1024 * 1024
                ? await File.ReadAllBytesAsync(path, cancellationToken).ConfigureAwait(false)
                : null;
        }

        public Task DeleteAsync(string objectId, CancellationToken cancellationToken)
        {
            EnsureConnected();
            if (!canDelete) throw new NotSupportedException("This device does not allow deleting.");
            File.Delete(Resolve(objectId));
            return Task.CompletedTask;
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;

        private void EnsureConnected()
        {
            if (!Directory.Exists(root)) throw new DeviceUnavailableException("The device was disconnected.");
        }

        private DeviceMediaItem Describe(string path)
        {
            var info = new FileInfo(path);
            var relative = Path.GetRelativePath(root, path).Replace('\\', '/');
            var folder = relative.Contains('/') ? relative[..relative.LastIndexOf('/')] : string.Empty;
            return new DeviceMediaItem(relative, folder, info.Name, info.Length, info.LastWriteTime, MediaFormats.FromExtension(path)?.MediaType);
        }

        private string Resolve(string objectId)
        {
            var full = Path.GetFullPath(Path.Combine(root, objectId.Replace('/', Path.DirectorySeparatorChar)));
            if (!Common.PathSafety.IsUnder(full, root)) throw new ArgumentException("Invalid object id.", nameof(objectId));
            return full;
        }
    }
}
