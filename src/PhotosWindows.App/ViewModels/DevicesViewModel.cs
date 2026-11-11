using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using PhotosWindows.App.Services;
using PhotosWindows.Application.Abstractions;
using PhotosWindows.Application.Devices;
using PhotosWindows.Domain;

namespace PhotosWindows.App.ViewModels;

public sealed partial class DeviceItemViewModel(DeviceMediaItem item) : ObservableObject
{
    public DeviceMediaItem Item { get; } = item;
    public string Name => Item.Name;
    public bool IsVideo => Item.MediaType == MediaType.Video;
    public string Details => string.Join(" · ", new[]
    {
        Item.Created?.ToString("g", CultureInfo.CurrentCulture),
        Item.Size is { } s ? SelectionActions.FormatBytes(s) : null,
    }.Where(p => p is not null));
    public string AutomationName => $"{Name}, {Loc.Get(IsVideo ? "Tile_Video" : "Tile_Photo")}, {Details}";

    [ObservableProperty] public partial ImageSource? Thumbnail { get; set; }
}

public sealed record DeviceOption(PortableDevice Device)
{
    public override string ToString() => Device.Name;
}

/// <summary>
/// Devices page: browse a connected phone or camera without importing, preview, and import a selection.
/// Browsing and "import only" never write to the device. "Import and delete" is offered only when the session
/// reports verified delete support, after an explicit confirmation.
/// </summary>
public sealed partial class DevicesViewModel(
    IPortableMediaDeviceProvider provider,
    PhoneImportCoordinator coordinator,
    ILibraryContext library,
    DialogService dialogs) : ObservableObject
{
    private IPortableDeviceSession? _session;
    private IReadOnlyList<DeviceMediaItem> _all = [];
    private CancellationTokenSource? _cts;
    private int _loadVersion;

    public ObservableCollection<DeviceOption> Devices { get; } = [];
    public ObservableCollection<DeviceItemViewModel> Items { get; } = [];
    public ObservableCollection<string> ResultLines { get; } = [];

    [ObservableProperty] public partial DeviceOption? SelectedDevice { get; set; }
    [ObservableProperty] public partial bool IsLoading { get; set; }
    [ObservableProperty] public partial string Status { get; set; } = string.Empty;
    [ObservableProperty] public partial bool ShowHelp { get; set; } = true;
    [ObservableProperty] public partial bool HasItems { get; set; }
    [ObservableProperty] public partial string Summary { get; set; } = string.Empty;
    [ObservableProperty] public partial int TypeIndex { get; set; }
    [ObservableProperty] public partial int SortIndex { get; set; }
    [ObservableProperty] public partial int SelectedCount { get; set; }
    [ObservableProperty] public partial bool CanImportSelection { get; set; }
    [ObservableProperty] public partial bool CanImportAll { get; set; }
    /// <summary>False = keep on the device (default); true = delete after a verified copy.</summary>
    [ObservableProperty] public partial bool DeleteAfterImport { get; set; }
    [ObservableProperty] public partial bool DeleteAvailable { get; set; }
    [ObservableProperty] public partial bool DeleteUnavailable { get; set; } = true;
    [ObservableProperty] public partial bool IsImporting { get; set; }
    [ObservableProperty] public partial bool IsIdle { get; set; } = true;
    [ObservableProperty] public partial double Progress { get; set; }
    [ObservableProperty] public partial string ProgressText { get; set; } = string.Empty;
    [ObservableProperty] public partial bool HasResult { get; set; }
    [ObservableProperty] public partial string ResultTitle { get; set; } = string.Empty;
    [ObservableProperty] public partial string ResultMessage { get; set; } = string.Empty;
    [ObservableProperty] public partial InfoBarSeverity ResultSeverity { get; set; }
    [ObservableProperty] public partial bool HasResultLines { get; set; }
    [ObservableProperty] public partial ImageSource? Preview { get; set; }
    [ObservableProperty] public partial string PreviewTitle { get; set; } = string.Empty;
    [ObservableProperty] public partial string PreviewNote { get; set; } = string.Empty;
    [ObservableProperty] public partial bool HasPreview { get; set; }

    partial void OnSelectedDeviceChanged(DeviceOption? value) => _ = OpenDeviceAsync(value);
    partial void OnTypeIndexChanged(int value) => ApplyView();
    partial void OnSortIndexChanged(int value) => ApplyView();

    partial void OnIsImportingChanged(bool value)
    {
        IsIdle = !value;
        UpdateCommands();
    }

    [RelayCommand]
    public async Task RefreshAsync()
    {
        if (IsImporting) return;
        IsLoading = true;
        try
        {
            var previous = SelectedDevice?.Device.Id;
            IReadOnlyList<PortableDevice> devices = provider.IsSupported ? await provider.GetDevicesAsync(CancellationToken.None) : [];
            Devices.Clear();
            foreach (var d in devices) Devices.Add(new DeviceOption(d));
            if (Devices.Count == 0)
            {
                await CloseSessionAsync();
                Status = Loc.Get(provider.IsSupported ? "Devices_None" : "Devices_Unsupported");
                ShowHelp = true;
                return;
            }

            var pick = Devices.FirstOrDefault(d => d.Device.Id == previous) ?? Devices[0];
            if (ReferenceEquals(SelectedDevice, pick) || SelectedDevice?.Device.Id == pick.Device.Id) await OpenDeviceAsync(pick);
            else SelectedDevice = pick;
        }
        finally
        {
            IsLoading = false;
        }
    }

    private async Task OpenDeviceAsync(DeviceOption? option)
    {
        var version = ++_loadVersion;
        await CloseSessionAsync();
        if (option is null) return;
        IsLoading = true;
        try
        {
            if (option.Device.Access == DeviceAccess.NoAccess)
            {
                Status = Loc.Format("Devices_NoAccess", option.Device.Name);
                ShowHelp = true;
                return;
            }

            var session = await provider.OpenAsync(option.Device.Id, CancellationToken.None);
            var progress = new InlineProgress<int>(_ => { });
            var items = await Task.Run(() => session.ListMediaAsync(progress, CancellationToken.None));
            if (version != _loadVersion)
            {
                await session.DisposeAsync();
                return;
            }

            _session = session;
            _all = items;
            DeleteAvailable = session.CanDelete;
            DeleteUnavailable = !session.CanDelete;
            if (!session.CanDelete) DeleteAfterImport = false;
            ShowHelp = items.Count == 0;
            Status = items.Count == 0 ? Loc.Format("Devices_Empty", option.Device.Name) : Loc.Format("Devices_Connected", option.Device.Name);
            ApplyView();
        }
        catch (DeviceUnavailableException)
        {
            await DeviceLostAsync();
        }
        finally
        {
            if (version == _loadVersion) IsLoading = false;
        }
    }

    private void ApplyView()
    {
        IEnumerable<DeviceMediaItem> view = TypeIndex switch
        {
            1 => _all.Where(i => i.MediaType == MediaType.Photo),
            2 => _all.Where(i => i.MediaType == MediaType.Video),
            _ => _all,
        };
        view = SortIndex switch
        {
            1 => view.OrderBy(i => i.Created ?? DateTime.MaxValue).ThenBy(i => i.Name, StringComparer.OrdinalIgnoreCase),
            2 => view.OrderBy(i => i.Name, StringComparer.OrdinalIgnoreCase).ThenBy(i => i.FolderId, StringComparer.Ordinal),
            _ => view.OrderByDescending(i => i.Created ?? DateTime.MinValue).ThenBy(i => i.Name, StringComparer.OrdinalIgnoreCase),
        };
        Items.Clear();
        foreach (var i in view) Items.Add(new DeviceItemViewModel(i));
        HasItems = Items.Count > 0;
        Summary = _all.Count == 0 ? string.Empty : Loc.Format("Devices_Summary", _all.Count(i => i.MediaType == MediaType.Photo), _all.Count(i => i.MediaType == MediaType.Video));
        UpdateSelection([]);
    }

    public void UpdateSelection(IReadOnlyList<DeviceItemViewModel> selected)
    {
        _selection = selected;
        SelectedCount = selected.Count;
        UpdateCommands();
        _ = ShowPreviewAsync(selected.Count == 1 ? selected[0] : null);
    }

    private IReadOnlyList<DeviceItemViewModel> _selection = [];

    private void UpdateCommands()
    {
        var ready = !IsImporting && _session is not null && library.Current is not null;
        CanImportSelection = ready && SelectedCount > 0;
        CanImportAll = ready && _all.Count > 0;
    }

    public async Task LoadThumbnailAsync(DeviceItemViewModel item)
    {
        if (item.Thumbnail is not null || _session is not { } session) return;
        try
        {
            var bytes = await Task.Run(() => session.GetThumbnailAsync(item.Item.ObjectId, 256, CancellationToken.None));
            if (bytes is not null && ReferenceEquals(session, _session)) item.Thumbnail = await ImageLoader.FromBytesAsync(bytes, 256);
        }
        catch (Exception ex) when (ex is IOException or System.Runtime.InteropServices.COMException or ArgumentException)
        {
            // No preview for this item: the tile keeps its placeholder.
        }
    }

    /// <summary>Preview of one item read straight from the device. Nothing is written to the library or the device.</summary>
    private async Task ShowPreviewAsync(DeviceItemViewModel? item)
    {
        Preview = null;
        HasPreview = item is not null;
        if (item is null || _session is not { } session) return;
        PreviewTitle = item.Name;
        PreviewNote = item.IsVideo ? Loc.Get("Devices_PreviewVideoNote") : string.Empty;
        Preview = item.Thumbnail;
        if (item.IsVideo || item.Item.Size is > 80L * 1024 * 1024) return;
        try
        {
            var bytes = await Task.Run(async () =>
            {
                await using var stream = await session.OpenReadAsync(item.Item.ObjectId, CancellationToken.None);
                using var memory = new MemoryStream();
                await stream.CopyToAsync(memory);
                return memory.ToArray();
            });
            if (_selection.Count == 1 && ReferenceEquals(_selection[0], item)) Preview = await ImageLoader.FromBytesAsync(bytes, 1600);
        }
        catch (DeviceUnavailableException)
        {
            await DeviceLostAsync();
        }
        catch (Exception ex) when (ex is IOException or System.Runtime.InteropServices.COMException or ArgumentException)
        {
            // The format cannot be shown here (e.g. HEIC without the Windows extension): say so, keep the thumbnail.
            PreviewNote = Loc.Get("Devices_PreviewUnavailable");
        }
    }

    [RelayCommand]
    private Task ImportSelectionAsync() => ImportAsync(_selection.Select(i => i.Item).ToList());

    [RelayCommand]
    private Task ImportAllAsync() => ImportAsync(_all);

    [RelayCommand]
    private void CancelImport() => _cts?.Cancel();

    private async Task ImportAsync(IReadOnlyList<DeviceMediaItem> selection)
    {
        if (IsImporting || _session is not { } session || selection.Count == 0) return;
        var withCompanions = PhoneImportCoordinator.WithCompanions(selection, _all);
        var mode = DeleteAfterImport && session.CanDelete ? PhoneImportMode.DeleteAfterVerifiedCopy : PhoneImportMode.KeepOnDevice;
        DeviceDeleteConsent? consent = null;
        if (mode == PhoneImportMode.DeleteAfterVerifiedCopy)
        {
            // The destructive mode needs an explicit, informed confirmation for exactly this selection.
            if (!await dialogs.ConfirmAsync(Loc.Get("Devices_DeleteTitle"), Loc.Format("Devices_DeleteMessage", withCompanions.Count, session.Device.Name), Loc.Get("Devices_DeleteConfirm"), destructive: true)) return;
            consent = new DeviceDeleteConsent(withCompanions.Count, DateTime.UtcNow);
        }

        IsImporting = true;
        HasResult = false;
        Progress = 0;
        _cts = new CancellationTokenSource();
        var ui = Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread();
        var progress = new InlineProgress<PhoneImportProgress>(p => ui.TryEnqueue(() =>
        {
            Progress = p.Total > 0 ? 100.0 * p.Done / p.Total : 0;
            ProgressText = Loc.Format("Devices_Stage_" + p.Stage, p.Done, p.Total, p.CurrentName);
        }));
        try
        {
            var token = _cts.Token;
            var result = await Task.Run(() => coordinator.ImportAsync(session, selection, _all, mode, consent, progress, token));
            ShowResult(result, mode);
            if (result.DeviceLost) await DeviceLostAsync(keepResult: true);
            else if (result.Deleted > 0) await OpenDeviceAsync(SelectedDevice); // the device content changed
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or UnauthorizedAccessException)
        {
            ResultTitle = Loc.Get("Devices_ResultFailed");
            ResultMessage = ex.Message;
            ResultSeverity = InfoBarSeverity.Error;
            HasResult = true;
        }
        finally
        {
            _cts.Dispose();
            _cts = null;
            IsImporting = false;
        }
    }

    private void ShowResult(PhoneImportResult r, PhoneImportMode mode)
    {
        ResultTitle = Loc.Get(r.Cancelled ? "Devices_ResultCancelled" : r.DeviceLost ? "Devices_ResultLost" : "Devices_ResultDone");
        ResultMessage = Loc.Format("Devices_ResultCounts", r.Imported, r.AlreadyInLibrary, r.Failed, r.Unavailable)
            + (mode == PhoneImportMode.DeleteAfterVerifiedCopy ? " " + Loc.Format("Devices_ResultDeleted", r.Deleted, r.Items.Count(i => i is { Verified: true, DeletedFromDevice: false })) : " " + Loc.Get("Devices_ResultKept"))
            + (r.Converted > 0 ? " " + Loc.Format("Devices_ResultConverted", r.Converted) : string.Empty);
        ResultSeverity = r.Cancelled || r.DeviceLost || r.Failed > 0 || r.Unavailable > 0 || r.Converted > 0 ? InfoBarSeverity.Warning : InfoBarSeverity.Success;
        ResultLines.Clear();
        foreach (var i in r.Items.Where(i => i.Status is PhoneItemStatus.Failed or PhoneItemStatus.Unavailable || i.FormatMismatch || i.KeptReason is not null || i.DeletedFromDevice).Take(300))
        {
            var what = i.DeletedFromDevice ? Loc.Get("Devices_Line_Deleted")
                : i.Status == PhoneItemStatus.Failed ? Loc.Get("Devices_Line_Failed")
                : i.Status == PhoneItemStatus.Unavailable ? Loc.Get("Devices_Line_Unavailable")
                : i.KeptReason is { } reason ? Loc.Get("Devices_Kept_" + reason.Replace("-", string.Empty, StringComparison.Ordinal))
                : Loc.Get("Devices_Line_Converted");
            ResultLines.Add($"{i.Item.Name} — {what}");
        }

        HasResultLines = ResultLines.Count > 0;
        HasResult = true;
    }

    private async Task DeviceLostAsync(bool keepResult = false)
    {
        await CloseSessionAsync();
        Status = Loc.Get("Devices_Lost");
        ShowHelp = true;
        if (!keepResult) HasResult = false;
    }

    private async Task CloseSessionAsync()
    {
        var session = _session;
        _session = null;
        _all = [];
        Items.Clear();
        HasItems = false;
        Summary = string.Empty;
        Preview = null;
        HasPreview = false;
        DeleteAvailable = false;
        DeleteUnavailable = true;
        UpdateSelection([]);
        if (session is not null) await session.DisposeAsync();
    }

    /// <summary>Called when the page is left: streams and handles of the device are released.</summary>
    public Task LeaveAsync() => IsImporting ? Task.CompletedTask : CloseSessionAsync();
}
