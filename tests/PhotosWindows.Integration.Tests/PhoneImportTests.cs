using PhotosWindows.Application.Devices;
using PhotosWindows.Application.Import;
using PhotosWindows.Domain;
using PhotosWindows.Integration.Tests.Support;

namespace PhotosWindows.Integration.Tests;

/// <summary>A device held in memory, with switches for everything that can go wrong on a real one.</summary>
public sealed class FakeDevice : IPortableMediaDeviceProvider, IPortableDeviceSession
{
    private readonly Dictionary<string, (DeviceMediaItem Item, byte[] Bytes)> _objects = new(StringComparer.Ordinal);
    private int _next = 1;

    public List<string> Log { get; } = [];
    public bool Connected { get; set; } = true;
    public DeviceAccess Access { get; set; } = DeviceAccess.Ready;
    public bool CanDelete { get; set; } = true;
    public bool HideSizes { get; set; }
    /// <summary>Disconnect when this many reads have been opened.</summary>
    public int? DisconnectOnRead { get; set; }
    public HashSet<string> TruncateNames { get; } = [];
    public HashSet<string> FailDeleteNames { get; } = [];
    public Action<string>? BeforeDelete { get; set; }
    public Action<string>? AfterRead { get; set; }
    private int _reads;

    public string Add(string folder, string name, byte[] bytes, DateTime? created = null)
    {
        var id = "o" + _next++;
        _objects[id] = (new DeviceMediaItem(id, folder, name, bytes.Length, created ?? new DateTime(2024, 7, 14, 10, 0, 0), MediaFormats.FromExtension(name)?.MediaType), bytes);
        return id;
    }

    public string AddFile(string folder, string path, string? name = null) => Add(folder, name ?? Path.GetFileName(path), File.ReadAllBytes(path));

    public IReadOnlyList<string> Names => _objects.Values.Select(o => o.Item.Name).Order(StringComparer.Ordinal).ToList();

    public void Replace(string id, byte[] bytes) => _objects[id] = (_objects[id].Item with { Size = bytes.Length }, bytes);

    /// <summary>Simulates the device renumbering its objects between sessions.</summary>
    public void Rename(string id, string newName) => _objects[id] = (_objects[id].Item with { Name = newName }, _objects[id].Bytes);

    // Provider
    public bool IsSupported => true;
    public Task<IReadOnlyList<PortableDevice>> GetDevicesAsync(CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<PortableDevice>>(Connected ? [Device] : []);
    public Task<IPortableDeviceSession> OpenAsync(string deviceId, CancellationToken cancellationToken) =>
        Connected ? Task.FromResult<IPortableDeviceSession>(this) : throw new DeviceUnavailableException("not connected");

    // Session
    public PortableDevice Device => new("fake", "Fake phone", Access);

    public Task<IReadOnlyList<DeviceMediaItem>> ListMediaAsync(IProgress<int>? progress, CancellationToken cancellationToken)
    {
        Check();
        IReadOnlyList<DeviceMediaItem> list = Access == DeviceAccess.NoAccess ? [] : _objects.Values.Select(o => HideSizes ? o.Item with { Size = null, Created = null } : o.Item).ToList();
        return Task.FromResult(list);
    }

    public Task<DeviceMediaItem?> GetItemAsync(string objectId, CancellationToken cancellationToken)
    {
        Check();
        return Task.FromResult(_objects.TryGetValue(objectId, out var o) ? (HideSizes ? o.Item with { Size = null, Created = null } : o.Item) : null);
    }

    public Task<Stream> OpenReadAsync(string objectId, CancellationToken cancellationToken)
    {
        if (DisconnectOnRead is { } n && ++_reads >= n) Connected = false;
        Check();
        var (item, bytes) = _objects[objectId];
        Log.Add("read " + item.Name);
        var data = TruncateNames.Contains(item.Name) ? bytes[..(bytes.Length / 2)] : bytes;
        AfterRead?.Invoke(item.Name);
        return Task.FromResult<Stream>(new MemoryStream(data, writable: false));
    }

    public Task<byte[]?> GetThumbnailAsync(string objectId, int size, CancellationToken cancellationToken) => Task.FromResult<byte[]?>(null);

    public Task DeleteAsync(string objectId, CancellationToken cancellationToken)
    {
        Check();
        var name = _objects[objectId].Item.Name;
        BeforeDelete?.Invoke(name);
        Check();
        if (FailDeleteNames.Contains(name)) throw new IOException("delete refused");
        Log.Add("delete " + name);
        _objects.Remove(objectId);
        return Task.CompletedTask;
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    private void Check()
    {
        if (!Connected) throw new DeviceUnavailableException("The device was disconnected.");
    }
}

public sealed class PhoneImportTests : IAsyncLifetime
{
    private TestHost _host = null!;
    private FakeDevice _phone = null!;
    private string _a = null!, _b = null!, _live = null!, _clip = null!, _video = null!;

    public async Task InitializeAsync()
    {
        _host = new TestHost();
        await _host.CreateLibraryAsync();
        var dir = _host.SourceDir("phone-fixtures");
        _phone = new FakeDevice();
        _a = _phone.AddFile("DCIM/100APPLE", Fixtures.Jpeg(Path.Combine(dir, "IMG_0001.JPG"), new Fixtures.PhotoSpec { Seed = 1, Taken = new DateTime(2024, 7, 14, 10, 0, 0) }));
        _b = _phone.AddFile("DCIM/100APPLE", Fixtures.Jpeg(Path.Combine(dir, "IMG_0002.JPG"), new Fixtures.PhotoSpec { Seed = 2, Taken = new DateTime(2024, 7, 15, 10, 0, 0) }));
        // A Live Photo: still + short clip with the same stem in the same folder.
        _live = _phone.AddFile("DCIM/100APPLE", Fixtures.Jpeg(Path.Combine(dir, "IMG_0003.JPG"), new Fixtures.PhotoSpec { Seed = 3, Taken = new DateTime(2024, 7, 16, 10, 0, 0) }));
        _clip = _phone.AddFile("DCIM/100APPLE", Fixtures.QuickTime(Path.Combine(dir, "IMG_0003.MOV"), TimeSpan.FromSeconds(2), new DateTime(2024, 7, 16, 8, 0, 0, DateTimeKind.Utc)));
        // Same file name in another device folder, and a normal video.
        _phone.AddFile("DCIM/101APPLE", Fixtures.Jpeg(Path.Combine(dir, "other", "IMG_0001.JPG"), new Fixtures.PhotoSpec { Seed = 4, Taken = new DateTime(2024, 8, 1, 10, 0, 0) }));
        _video = _phone.AddFile("DCIM/101APPLE", Fixtures.QuickTime(Path.Combine(dir, "IMG_0009.MOV"), TimeSpan.FromSeconds(30), new DateTime(2024, 8, 2, 8, 0, 0, DateTimeKind.Utc)));
    }

    public async Task DisposeAsync() => await _host.DisposeAsync();

    private PhoneImportCoordinator Coordinator => _host.Get<PhoneImportCoordinator>();

    private async Task<PhoneImportResult> Import(PhoneImportMode mode, Func<DeviceMediaItem, bool>? pick = null, CancellationToken ct = default, bool consent = true)
    {
        var all = await _phone.ListMediaAsync(null, CancellationToken.None);
        var selection = all.Where(pick ?? (_ => true)).ToList();
        var count = PhoneImportCoordinator.WithCompanions(selection, all).Count;
        return await Coordinator.ImportAsync(_phone, selection, all, mode, mode == PhoneImportMode.DeleteAfterVerifiedCopy && consent ? new DeviceDeleteConsent(count, DateTime.UtcNow) : null, null, ct);
    }

    private async Task<string[]> LibraryNames() => (await _host.AllAsync()).Select(a => a.FileName).Order(StringComparer.Ordinal).ToArray();

    [Fact]
    public async Task Import_only_copies_everything_verifies_it_and_never_touches_the_device()
    {
        var before = _phone.Names;
        var result = await Import(PhoneImportMode.KeepOnDevice);
        Assert.Equal((6, 0, 0, 0, 0), (result.Imported, result.AlreadyInLibrary, result.Failed, result.Unavailable, result.Deleted));
        Assert.All(result.Items, i => Assert.True(i.Verified));
        Assert.Equal(before, _phone.Names);
        Assert.DoesNotContain(_phone.Log, l => l.StartsWith("delete", StringComparison.Ordinal));
        // Five assets: the Live Photo's clip is attached to its still, not a separate item.
        Assert.Equal(["IMG_0001.JPG", "IMG_0001.JPG", "IMG_0002.JPG", "IMG_0003.JPG", "IMG_0009.MOV"], await LibraryNames());
        var live = (await _host.AllAsync()).Single(a => a.FileName == "IMG_0003.JPG");
        Assert.True(live.HasLiveVideo);
        // Imported files are managed copies inside the library; staging is gone and was never inside the library.
        var library = _host.Get<PhotosWindows.Application.Abstractions.ILibraryContext>().RequireCurrent();
        Assert.All(await _host.Assets.GetAllFilesAsync(CancellationToken.None), f => Assert.Equal(StorageMode.Managed, f.StorageMode));
        Assert.False(Directory.Exists(Coordinator.StagingRoot) && Directory.EnumerateFileSystemEntries(Coordinator.StagingRoot).Any());
        Assert.False(PhotosWindows.Application.Common.PathSafety.IsUnder(Coordinator.StagingRoot, library.RootPath));
    }

    [Fact]
    public async Task Selecting_one_part_of_a_live_photo_brings_its_companion()
    {
        var result = await Import(PhoneImportMode.KeepOnDevice, i => i.ObjectId == _live);
        Assert.Equal(["IMG_0003.JPG", "IMG_0003.MOV"], result.Items.Select(i => i.Item.Name).Order());
        Assert.True((await _host.AllAsync()).Single().HasLiveVideo);
    }

    [Fact]
    public async Task Importing_again_reports_duplicates_verified_against_the_existing_files()
    {
        await Import(PhoneImportMode.KeepOnDevice);
        var again = await Import(PhoneImportMode.KeepOnDevice);
        Assert.Equal((0, 6), (again.Imported, again.AlreadyInLibrary));
        Assert.All(again.Items, i => Assert.True(i.Verified));
        Assert.Equal(5, (await _host.AllAsync()).Count);
    }

    [Fact]
    public async Task Delete_mode_removes_only_what_was_verified_and_reports_exactly_what_it_deleted()
    {
        var result = await Import(PhoneImportMode.DeleteAfterVerifiedCopy, i => i.FolderId == "DCIM/100APPLE");
        Assert.Equal((4, 4), (result.Imported, result.Deleted));
        Assert.Equal(["IMG_0001.JPG", "IMG_0009.MOV"], _phone.Names); // the other folder was not selected
        // Every delete happened after every read: nothing is deleted while copies are still unverified.
        Assert.True(_phone.Log.FindLastIndex(l => l.StartsWith("read", StringComparison.Ordinal)) < _phone.Log.FindIndex(l => l.StartsWith("delete", StringComparison.Ordinal)));
        Assert.Equal(4, (await _host.AllAsync()).Count == 3 ? 4 : 0); // 3 assets (one is a Live Photo) from 4 files
    }

    [Fact]
    public async Task Delete_mode_requires_capability_and_consent_for_exactly_this_selection()
    {
        await Assert.ThrowsAsync<InvalidOperationException>(() => Import(PhoneImportMode.DeleteAfterVerifiedCopy, consent: false));
        var all = await _phone.ListMediaAsync(null, CancellationToken.None);
        await Assert.ThrowsAsync<InvalidOperationException>(() => Coordinator.ImportAsync(_phone, all, all, PhoneImportMode.DeleteAfterVerifiedCopy, new DeviceDeleteConsent(2, DateTime.UtcNow), null, CancellationToken.None));
        _phone.CanDelete = false;
        await Assert.ThrowsAsync<InvalidOperationException>(() => Import(PhoneImportMode.DeleteAfterVerifiedCopy));
        Assert.Equal(6, _phone.Names.Count);
        Assert.Empty(_phone.Log); // refused before reading anything
        Assert.Empty(await _host.AllAsync());
    }

    [Fact]
    public async Task Truncated_stream_is_not_stored_and_its_whole_capture_stays_on_the_device()
    {
        _phone.TruncateNames.Add("IMG_0003.MOV");
        var result = await Import(PhoneImportMode.DeleteAfterVerifiedCopy);
        var clip = result.Items.Single(i => i.Item.Name == "IMG_0003.MOV");
        var still = result.Items.Single(i => i.Item.Name == "IMG_0003.JPG");
        Assert.Equal(PhoneItemStatus.Failed, clip.Status);
        Assert.True(still.Verified);
        Assert.False(still.DeletedFromDevice);
        Assert.Equal("incomplete-capture", still.KeptReason); // the still is safe, but its clip is not: both stay
        Assert.Contains("IMG_0003.JPG", _phone.Names);
        Assert.Contains("IMG_0003.MOV", _phone.Names);
        Assert.Equal(4, result.Deleted);
    }

    [Fact]
    public async Task Converted_content_is_imported_but_flagged_and_never_deleted()
    {
        // The device hands over JPEG bytes under an .HEIC name (automatic transfer conversion).
        var dir = _host.SourceDir("converted");
        var id = _phone.Add("DCIM/100APPLE", "IMG_0050.HEIC", File.ReadAllBytes(Fixtures.Jpeg(Path.Combine(dir, "x.jpg"), new Fixtures.PhotoSpec { Seed = 50 })));
        var result = await Import(PhoneImportMode.DeleteAfterVerifiedCopy, i => i.ObjectId == id);
        var item = Assert.Single(result.Items);
        Assert.True(item.FormatMismatch);
        Assert.Equal(1, result.Converted);
        Assert.False(item.DeletedFromDevice);
        if (item.Verified) Assert.Equal("converted", item.KeptReason);
        Assert.Contains("IMG_0050.HEIC", _phone.Names);
    }

    [Fact]
    public async Task Disconnect_while_copying_keeps_what_arrived_marks_the_rest_unavailable_and_deletes_nothing()
    {
        _phone.DisconnectOnRead = 3;
        var result = await Import(PhoneImportMode.DeleteAfterVerifiedCopy);
        Assert.True(result.DeviceLost);
        Assert.Equal((2, 4, 0), (result.Imported, result.Unavailable, result.Deleted));
        Assert.All(result.Items.Where(i => i.Verified), i => Assert.Equal("device-lost", i.KeptReason));
        Assert.Equal(2, (await _host.AllAsync()).Count);
        _phone.Connected = true;
        Assert.Equal(6, _phone.Names.Count);
    }

    [Fact]
    public async Task Disconnect_while_deleting_stops_at_once_and_says_what_was_and_was_not_deleted()
    {
        var deletes = 0;
        _phone.BeforeDelete = _ => { if (++deletes == 3) _phone.Connected = false; };
        var result = await Import(PhoneImportMode.DeleteAfterVerifiedCopy);
        Assert.True(result.DeviceLost);
        Assert.Equal(2, result.Deleted);
        Assert.Equal(4, result.Items.Count(i => i is { Verified: true, DeletedFromDevice: false, KeptReason: "device-lost" }));
        Assert.Equal(6, result.Imported); // every copy is safe in the library regardless
    }

    [Fact]
    public async Task Cancelling_during_the_copy_never_deletes_and_cancelling_during_deletion_stops_before_the_next_capture()
    {
        using var early = new CancellationTokenSource();
        _phone.AfterRead = name => { if (name == "IMG_0002.JPG") early.Cancel(); };
        var first = await Import(PhoneImportMode.DeleteAfterVerifiedCopy, ct: early.Token);
        Assert.True(first.Cancelled);
        Assert.Equal(0, first.Deleted);
        Assert.Equal(6, _phone.Names.Count);
        Assert.All(first.Items.Where(i => i.Verified), i => Assert.Equal("cancelled", i.KeptReason));

        _phone.AfterRead = null;
        using var late = new CancellationTokenSource();
        _phone.BeforeDelete = _ => late.Cancel();
        var second = await Import(PhoneImportMode.DeleteAfterVerifiedCopy, ct: late.Token);
        Assert.InRange(second.Deleted, 1, 2); // the capture being deleted finishes; no other capture is started
        Assert.All(second.Items.Where(i => !i.DeletedFromDevice), i => Assert.Equal("cancelled", i.KeptReason));
        Assert.Equal(6 - second.Deleted, _phone.Names.Count);
    }

    [Fact]
    public async Task An_object_that_changed_on_the_device_after_listing_is_never_deleted()
    {
        var all = await _phone.ListMediaAsync(null, CancellationToken.None);
        var selection = all.Where(i => i.ObjectId == _a || i.ObjectId == _b).ToList();
        // Between the copy and the delete, object _a becomes something else under the same id.
        _phone.AfterRead = name => { if (name == "IMG_0002.JPG") _phone.Rename(_a, "IMG_7777.JPG"); };
        var result = await Coordinator.ImportAsync(_phone, selection, all, PhoneImportMode.DeleteAfterVerifiedCopy, new DeviceDeleteConsent(2, DateTime.UtcNow), null, CancellationToken.None);
        var a = result.Items.Single(i => i.Item.ObjectId == _a);
        Assert.Equal((true, false, "changed-on-device"), (a.Verified, a.DeletedFromDevice, a.KeptReason));
        Assert.True(result.Items.Single(i => i.Item.ObjectId == _b).DeletedFromDevice);
        Assert.Contains("IMG_7777.JPG", _phone.Names);
    }

    [Fact]
    public async Task A_delete_that_fails_for_one_item_is_reported_and_the_others_continue()
    {
        _phone.FailDeleteNames.Add("IMG_0002.JPG");
        var result = await Import(PhoneImportMode.DeleteAfterVerifiedCopy);
        var failed = result.Items.Single(i => i.Item.Name == "IMG_0002.JPG");
        Assert.Equal((true, false, "delete-failed"), (failed.Verified, failed.DeletedFromDevice, failed.KeptReason));
        Assert.Equal(5, result.Deleted);
        Assert.Equal(["IMG_0002.JPG"], _phone.Names);
    }

    [Fact]
    public async Task Duplicate_already_in_the_library_is_deleted_from_the_device_only_after_verifying_the_existing_file()
    {
        await Import(PhoneImportMode.KeepOnDevice, i => i.ObjectId == _a);
        var library = _host.Get<PhotosWindows.Application.Abstractions.ILibraryContext>().RequireCurrent();
        var managed = library.Resolve((await _host.Assets.GetAllFilesAsync(CancellationToken.None)).Single());

        // The managed copy is damaged on disk: the duplicate must NOT count as safe.
        var good = File.ReadAllBytes(managed);
        File.WriteAllBytes(managed, good[..^10]);
        var damaged = await Import(PhoneImportMode.DeleteAfterVerifiedCopy, i => i.ObjectId == _a);
        Assert.False(Assert.Single(damaged.Items).Verified);
        Assert.Equal(0, damaged.Deleted);
        Assert.Contains("IMG_0001.JPG", _phone.Names.Take(2));

        File.WriteAllBytes(managed, good);
        var ok = await Import(PhoneImportMode.DeleteAfterVerifiedCopy, i => i.ObjectId == _a);
        var item = Assert.Single(ok.Items);
        Assert.Equal((PhoneItemStatus.AlreadyInLibrary, true, true), (item.Status, item.Verified, item.DeletedFromDevice));
    }

    [Fact]
    public async Task Devices_without_sizes_or_dates_and_locked_or_absent_devices_are_handled()
    {
        _phone.HideSizes = true;
        var result = await Import(PhoneImportMode.KeepOnDevice, i => i.ObjectId == _video);
        Assert.Equal(1, result.Imported);

        _phone.Access = DeviceAccess.NoAccess; // locked or not trusted: connected, but nothing is exposed
        Assert.Equal(DeviceAccess.NoAccess, (await _phone.GetDevicesAsync(CancellationToken.None)).Single().Access);
        Assert.Empty(await _phone.ListMediaAsync(null, CancellationToken.None));

        _phone.Connected = false;
        Assert.Empty(await _phone.GetDevicesAsync(CancellationToken.None));
        await Assert.ThrowsAsync<DeviceUnavailableException>(() => _phone.OpenAsync("fake", CancellationToken.None));
        var none = new NoPortableDeviceProvider();
        Assert.False(none.IsSupported);
        Assert.Empty(await none.GetDevicesAsync(CancellationToken.None));
    }

    [Fact]
    public async Task Simulated_folder_device_lists_reads_and_only_deletes_when_allowed()
    {
        var root = _host.SourceDir("sim-device");
        Fixtures.Jpeg(Path.Combine(root, "DCIM", "100APPLE", "IMG_0001.JPG"), new Fixtures.PhotoSpec { Seed = 70 });
        File.WriteAllText(Path.Combine(root, "DCIM", "notes.txt"), "not media");
        var readOnly = new SimulatedFolderDeviceProvider(root, canDelete: false);
        await using var session = await readOnly.OpenAsync("simulated", CancellationToken.None);
        var item = Assert.Single(await session.ListMediaAsync(null, CancellationToken.None));
        Assert.Equal(("DCIM/100APPLE", "IMG_0001.JPG", MediaType.Photo), (item.FolderId, item.Name, item.MediaType));
        Assert.False(session.CanDelete);
        await Assert.ThrowsAsync<NotSupportedException>(() => session.DeleteAsync(item.ObjectId, CancellationToken.None));
        await Assert.ThrowsAsync<ArgumentException>(() => session.OpenReadAsync("../../outside.jpg", CancellationToken.None));
        Assert.NotNull(await session.GetThumbnailAsync(item.ObjectId, 256, CancellationToken.None));
        Directory.Delete(root, recursive: true);
        await Assert.ThrowsAsync<DeviceUnavailableException>(() => session.ListMediaAsync(null, CancellationToken.None));
    }
}
