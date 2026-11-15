using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using Microsoft.UI.Xaml.Media;
using PhotosWindows.App.Services;
using PhotosWindows.Application.Abstractions;
using PhotosWindows.Application.Editing;
using PhotosWindows.Application.Organization;
using PhotosWindows.Application.Thumbnails;
using PhotosWindows.Domain;

namespace PhotosWindows.App.ViewModels;

public sealed record InfoRow(string Label, string Value)
{
    /// <summary>What a screen reader announces for the value: "Camera: QA Cam 1", not just "Camera".</summary>
    public string AccessibleText => $"{Label}: {Value}";
}

/// <summary>Full-screen-capable viewer for photos and videos with an information/inspector pane.</summary>
public sealed partial class ViewerViewModel(
    IAssetRepository assets,
    ILibraryContext context,
    EditingService editing,
    OrganizationService organization,
    ThumbnailService thumbnails,
    ImageLoader images,
    SelectionActions actions,
    NavigationService navigation,
    WindowContext window,
    PeopleCoordinator people,
    UiThread ui,
    ILogger<ViewerViewModel> logger) : ObservableObject
{
    /// <summary>After this long without any image, the viewer says so instead of showing an endless spinner.</summary>
    private static readonly TimeSpan SlowLoadNotice = TimeSpan.FromSeconds(6);
    private long _loadId;

    /// <summary>Upper bound for the viewer's own renders (zoomed in on a 4K screen); never more than the original.</summary>
    private const int MaxViewerRenderSize = 3840;

    /// <summary>
    /// One full-quality viewer render at a time across the app. The slot is released when the render has really
    /// finished, not when the user navigates away, so fast navigation cannot pile up native decodes.
    /// </summary>
    private static readonly SemaphoreSlim RenderSlot = new(1, 1);

    private string _stateWhenOpened = string.Empty;
    private int _shownLongSide;
    private IDisposable? _foreground;
    private CancellationTokenSource? _idle;

    /// <summary>Long side of the stage in physical pixels, set by the page (window size × display scale).</summary>
    public int StageLongSidePx { get; set; } = 1920;

    /// <summary>True while an image change only raises quality (zoom); the page keeps the current zoom and scroll.</summary>
    public bool PreserveView { get; private set; }
    private IReadOnlyList<long> _ids = [];
    private CancellationTokenSource? _cts;

    public ObservableCollection<InfoRow> Details { get; } = [];
    public ObservableCollection<string> Keywords { get; } = [];

    [ObservableProperty] public partial int Index { get; set; }
    [ObservableProperty] public partial string Position { get; set; } = string.Empty;
    [ObservableProperty] public partial Asset? Asset { get; set; }
    [ObservableProperty] public partial ImageSource? Image { get; set; }
    [ObservableProperty] public partial Uri? VideoSource { get; set; }
    [ObservableProperty] public partial bool IsVideo { get; set; }
    [ObservableProperty] public partial bool IsPhoto { get; set; }
    [ObservableProperty] public partial bool IsLoading { get; set; }
    [ObservableProperty] public partial bool HasError { get; set; }

    /// <summary>What the viewer is doing now ("Loading photo…", "Improving quality…"); empty when idle.</summary>
    [ObservableProperty] public partial string LoadingMessage { get; set; } = string.Empty;

    /// <summary>
    /// Machine-readable state of the current load, exposed to UI Automation: Loading, Preview, Ready, Error,
    /// Unavailable or Cancelled. Every load ends in Preview/Ready (an image is shown), Error or Unavailable.
    /// </summary>
    [ObservableProperty] public partial string LoadState { get; set; } = string.Empty;
    [ObservableProperty] public partial string ErrorMessage { get; set; } = string.Empty;
    [ObservableProperty] public partial bool IsInfoOpen { get; set; }
    [ObservableProperty] public partial bool IsFavorite { get; set; }
    [ObservableProperty] public partial bool HasEdits { get; set; }
    [ObservableProperty] public partial bool ShowingOriginal { get; set; }
    [ObservableProperty] public partial bool HasLiveVideo { get; set; }
    [ObservableProperty] public partial int ViewRotation { get; set; }
    [ObservableProperty] public partial string EditTitle { get; set; } = string.Empty;
    [ObservableProperty] public partial string EditDescription { get; set; } = string.Empty;
    [ObservableProperty] public partial double Rating { get; set; }
    [ObservableProperty] public partial string AlbumsText { get; set; } = string.Empty;

    /// <summary>People in the photo (only when People is on); the user's names, never guesses.</summary>
    [ObservableProperty] public partial bool ShowPeople { get; set; }
    [ObservableProperty] public partial string PeopleText { get; set; } = string.Empty;
    [ObservableProperty] public partial bool IsExcludedFromPeople { get; set; }
    [ObservableProperty] public partial bool CanGoPrevious { get; set; }
    [ObservableProperty] public partial bool CanGoNext { get; set; }

    public double RotationAngle => ViewRotation * 90;

    partial void OnViewRotationChanged(int value) => OnPropertyChanged(nameof(RotationAngle));

    public Task LoadAsync(ViewerNavigation target)
    {
        _ids = target.Ids;
        return ShowAsync(Math.Clamp(target.Index, 0, Math.Max(0, _ids.Count - 1)));
    }

    private async Task ShowAsync(int index)
    {
        if (_ids.Count == 0) return;
        _cts?.Cancel();
        var cts = _cts = new CancellationTokenSource();
        var loadId = ++_loadId;
        var timing = new LoadTiming(loadId);
        Index = index;
        Position = Loc.Format("Viewer_Position", index + 1, _ids.Count);
        CanGoPrevious = index > 0;
        CanGoNext = index < _ids.Count - 1;
        IsLoading = true;
        HasError = false;
        LoadState = "Loading";
        LoadingMessage = Loc.Get("Viewer_LoadingPhoto");
        ShowingOriginal = false;
        ViewRotation = 0;
        VideoSource = null;
        _shownLongSide = 0;
        SetImage(null, string.Empty); // never leave the previous photo on screen as if it were this one
        HoldForeground();
        _ = WatchForSlowLoadAsync(loadId, cts.Token);
        try
        {
            var asset = await assets.GetAsync(_ids[index], cts.Token);
            timing.Mark("asset");
            if (asset is null || cts.IsCancellationRequested) return;
            Asset = asset;
            IsVideo = asset.MediaType == MediaType.Video;
            IsPhoto = !IsVideo;
            IsFavorite = asset.IsFavorite;
            HasEdits = asset.HasEdits;
            HasLiveVideo = asset.HasLiveVideo;
            timing.Format = asset.Format;

            // 1. Whatever is already cached, best first: no decoding at all.
            if (IsPhoto)
            {
                if (await TryShowCachedAsync(asset, ThumbnailSize.Large, cts.Token)) timing.Mark("large-cached");
                else if (await TryShowCachedAsync(asset, ThumbnailSize.Small, cts.Token)) timing.Mark("small-cached");
            }

            var original = await editing.ResolveOriginalAsync(asset.Id, cts.Token);
            timing.Mark("resolve");
            if (original is null)
            {
                ShowError(Loc.Get("Viewer_Unavailable"), "Unavailable");
                await LoadInspectorAsync(asset, cts.Token);
                return;
            }

            if (IsVideo)
            {
                SetImage(null, string.Empty);
                VideoSource = new Uri(original);
                LoadState = "Ready";
                await LoadInspectorAsync(asset, cts.Token);
                return;
            }

            // 2. Without a large preview, make one on the viewer's own lane. It is decoded once and, when the window
            //    does not need more than 2048 px, it is also the final image: no second full decode of the original.
            var target = TargetLongSide(asset, 1.0);
            if (_shownLongSide < (int)ThumbnailSize.Large)
            {
                LoadingMessage = Loc.Get(IsHeif(asset) ? "Viewer_DecodingHeic" : "Viewer_LoadingPreview");
                var path = await thumbnails.GetOrCreateAsync(asset.Id, ThumbnailSize.Large, ThumbnailPriority.Viewer, cts.Token);
                timing.Mark("large");
                if (path is not null) await ShowFileAsync(path, (int)ThumbnailSize.Large, target <= (int)ThumbnailSize.Large ? "Ready" : "Preview", cts.Token);
                else if (Image is null) throw new MediaDecodeException(Loc.Get(IsHeif(asset) ? "Viewer_HeicFailed" : "Viewer_LoadFailed"));
            }
            else if (target <= _shownLongSide)
            {
                _stateWhenOpened = "Ready";
                if (Image is not null && LoadState == "Preview") LoadState = "Ready";
            }

            // 3. The inspector loads after the first image.
            await LoadInspectorAsync(asset, cts.Token);
            timing.Mark("inspector");

            // 4. Full quality only when the window really needs more pixels than the preview has.
            if (target > _shownLongSide)
            {
                LoadingMessage = Loc.Get("Viewer_ImprovingQuality");
                await RenderAsync(asset, target, preserveView: false, cts.Token);
                timing.Mark("render");
            }
        }
        catch (OperationCanceledException)
        {
            // Navigated to another item; that load owns the state now.
            timing.Outcome = "cancelled";
        }
        catch (MediaDecodeException ex)
        {
            ShowError(ex.Message, Image is null ? "Error" : "Preview");
            timing.Outcome = "decode-error";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or ArgumentException
                                       or System.Runtime.InteropServices.ExternalException or OutOfMemoryException or TimeoutException)
        {
            logger.LogWarning(ex, "Viewer load {LoadId} failed", loadId);
            ShowError(Loc.Get(ex is OutOfMemoryException ? "Viewer_OutOfMemory" : "Viewer_LoadFailed"), Image is null ? "Error" : "Preview");
            timing.Outcome = "error";
        }
        finally
        {
            if (_cts == cts)
            {
                IsLoading = false;
                LoadingMessage = string.Empty;
                if (LoadState == "Loading" && Image is null) LoadState = "Error";
                ReleaseForegroundWhenIdle();
            }

            timing.Log(logger, Asset, thumbnails.Stats);
        }
    }

    private static bool IsHeif(Asset asset) => asset.Format.Contains("HEI", StringComparison.OrdinalIgnoreCase);

    /// <summary>Pixels the stage needs at this zoom, never more than the original nor the viewer's cap.</summary>
    private int TargetLongSide(Asset asset, double zoom)
    {
        var (w, h) = asset.DisplaySize;
        var original = Math.Max(w, h);
        var needed = (int)Math.Ceiling(Math.Max(320, StageLongSidePx) * Math.Max(1, zoom));
        needed = Math.Min(needed, MaxViewerRenderSize);
        return original > 0 ? Math.Min(needed, original) : needed;
    }

    /// <summary>Called by the page when the user zooms: loads more pixels if the shown image is too small.</summary>
    public async Task EnsureResolutionAsync(double zoom)
    {
        if (Asset is not { MediaType: MediaType.Photo } asset || IsLoading || _cts is not { } cts) return;
        var target = TargetLongSide(asset, zoom);
        if (target <= _shownLongSide * 1.1) return;
        IsLoading = true;
        LoadingMessage = Loc.Get("Viewer_ImprovingQuality");
        try
        {
            await RenderAsync(asset, target, preserveView: true, cts.Token);
        }
        catch (OperationCanceledException)
        {
            // navigated away
        }
        catch (Exception ex) when (ex is MediaDecodeException or IOException or System.Runtime.InteropServices.ExternalException or OutOfMemoryException)
        {
            logger.LogInformation(ex, "Higher-resolution render failed; keeping the current image");
        }
        finally
        {
            if (_cts == cts)
            {
                IsLoading = false;
                LoadingMessage = string.Empty;
            }
        }
    }

    /// <summary>Shows a cached thumbnail if it exists, without generating anything. True when an image was shown.</summary>
    private async Task<bool> TryShowCachedAsync(Asset asset, ThumbnailSize size, CancellationToken ct)
    {
        var path = await thumbnails.TryGetCachedAsync(asset.Id, size, ct);
        if (path is null) return false;
        try
        {
            return await ShowFileAsync(path, (int)size, "Preview", ct);
        }
        catch (Exception ex) when (ex is IOException or System.Runtime.InteropServices.ExternalException)
        {
            // A damaged cache file: removed so the next step regenerates it once.
            logger.LogDebug(ex, "Cached thumbnail could not be shown");
            TryDelete(path);
            return false;
        }
    }

    private async Task<bool> ShowFileAsync(string path, int longSide, string state, CancellationToken ct)
    {
        var bitmap = await images.FromFileAsync(path, 0, ct);
        ct.ThrowIfCancellationRequested();
        if (bitmap is null) return false;
        _shownLongSide = longSide;
        SetImage(bitmap, state);
        return true;
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // regenerated later
        }
    }

    /// <summary>
    /// The state applies only once WinUI has decoded the image: a bitmap that already has pixels (SetSourceAsync has
    /// finished) counts at once — WinUI does not raise ImageOpened for those (UI run #56) — otherwise ImageOpened does.
    /// A source that is set but not decoded never counts as shown.
    /// </summary>
    private void SetImage(ImageSource? image, string stateWhenOpened)
    {
        _stateWhenOpened = stateWhenOpened;
        Image = image;
        if (image is Microsoft.UI.Xaml.Media.Imaging.BitmapSource { PixelWidth: > 0, PixelHeight: > 0 }) ApplyOpenedState();
    }

    private void ApplyOpenedState()
    {
        if (_stateWhenOpened.Length > 0 && LoadState is "Loading" or "Preview") LoadState = _stateWhenOpened;
    }

    private async Task RenderAsync(Asset asset, int longSide, bool preserveView, CancellationToken ct)
    {
        var recipe = ShowingOriginal ? EditRecipe.Identity : await editing.GetRecipeAsync(asset.Id, ct);

        // Wait for the app-wide render slot; the slot is given back only when the render has really finished.
        await RenderSlot.WaitAsync(ct);
        Task<RenderedImage?> running;
        try
        {
            // Off the UI thread: the catalog lookups and the WIC StorageFile call must not run (and pump) on it.
            running = Task.Run(() => editing.RenderFromOriginalAsync(asset.Id, recipe, longSide, EncodeOptions.PreviewJpeg, ct), CancellationToken.None);
        }
        catch
        {
            RenderSlot.Release();
            throw;
        }

        _ = running.ContinueWith(_ => RenderSlot.Release(), CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        var rendered = await running.WaitAsync(ct);
        ct.ThrowIfCancellationRequested();
        if (rendered is null)
        {
            // Keep whatever preview is on screen; say that full quality is not available.
            ShowError(Loc.Get("Viewer_LoadFailed"), Image is null ? "Error" : "Preview");
            return;
        }

        var bitmap = await ImageLoader.FromBytesAsync(rendered.Encoded, 0);
        ct.ThrowIfCancellationRequested();
        _shownLongSide = longSide;
        PreserveView = preserveView;
        try
        {
            SetImage(bitmap, "Ready");
        }
        finally
        {
            PreserveView = false;
        }
    }

    /// <summary>Background thumbnail work yields while the viewer loads and for a few seconds after (the user may go on).</summary>
    private void HoldForeground()
    {
        _idle?.Cancel();
        _foreground ??= thumbnails.BeginForeground();
    }

    private void ReleaseForegroundWhenIdle()
    {
        _idle?.Cancel();
        var idle = _idle = new CancellationTokenSource();
        _ = ReleaseLaterAsync(idle.Token);
    }

    private async Task ReleaseLaterAsync(CancellationToken ct)
    {
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(5), ct);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        _foreground?.Dispose();
        _foreground = null;
    }

    /// <summary>If nothing is on screen after a few seconds, say so (the load keeps going and can still finish).</summary>
    private async Task WatchForSlowLoadAsync(long loadId, CancellationToken ct)
    {
        try
        {
            await Task.Delay(SlowLoadNotice, ct);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        ui.Post(() =>
        {
            if (loadId != _loadId || !IsLoading) return;
            var stats = thumbnails.Stats;
            LoadingMessage = Image is not null ? Loc.Get("Viewer_ImprovingQuality")
                : stats.DecodesPastTimeout > 0 ? Loc.Get("Viewer_DecoderSlow")
                : Loc.Get("Viewer_SlowLoad");
            logger.LogWarning("Viewer load {LoadId} still running after {Seconds} s (image shown: {Shown}; {Stats})", loadId, SlowLoadNotice.TotalSeconds, Image is not null, stats);
        });
    }

    /// <summary>Called by the page when the Image element has decoded and shown its source.</summary>
    public void OnImageOpened()
    {
        ApplyOpenedState();
        logger.LogDebug("Viewer load {LoadId}: image opened ({State})", _loadId, LoadState);
    }

    /// <summary>Called by the page when WinUI could not show the image source.</summary>
    public void OnImageFailed(string message)
    {
        logger.LogWarning("Viewer load {LoadId}: image failed to open ({Message})", _loadId, message);
        ShowError(Loc.Get("Viewer_LoadFailed"), "Error");
    }

    [RelayCommand]
    private Task RetryAsync() => ShowAsync(Index);

    /// <summary>Monotonic per-stage timings for one load, written to the local log. No paths or names.</summary>
    private sealed class LoadTiming(long id)
    {
        private readonly System.Diagnostics.Stopwatch _sw = System.Diagnostics.Stopwatch.StartNew();
        private readonly List<string> _marks = [];

        public string Outcome { get; set; } = "completed";

        public string? Format { get; set; }

        public void Mark(string stage) => _marks.Add($"{stage}={_sw.ElapsedMilliseconds}ms");

        public void Log(ILogger logger, Asset? asset, ThumbnailStats stats) => logger.LogInformation(
            "Viewer load {LoadId} {Outcome} in {Ms} ms: {Stages} (asset {AssetId}, {Format}, {Bytes} bytes, {Width}x{Height}, edited {Edited}; {Stats})",
            id, Outcome, _sw.ElapsedMilliseconds, string.Join(", ", _marks), asset?.Id, asset?.Format, asset?.FileSize, asset?.PixelWidth, asset?.PixelHeight, asset?.HasEdits, stats);
    }

    private void ShowError(string message, string state = "Error")
    {
        HasError = true;
        ErrorMessage = message;
        LoadState = state;
    }

    private async Task LoadInspectorAsync(Asset asset, CancellationToken ct)
    {
        EditTitle = asset.Title ?? string.Empty;
        EditDescription = asset.Description ?? string.Empty;
        Rating = asset.Rating == 0 ? -1 : asset.Rating;
        Keywords.Clear();
        foreach (var k in await organization.GetKeywordsForAsync(asset.Id, ct)) Keywords.Add(k);
        var albums = await organization.GetAlbumsContainingAsync(asset.Id, ct);
        AlbumsText = albums.Count == 0 ? Loc.Get("Info_NoAlbums") : string.Join(", ", albums.Select(a => a.Name));
        await LoadPeopleAsync(asset, ct);

        var culture = CultureInfo.CurrentCulture;
        var file = await assets.GetOriginalFileAsync(asset.Id, ct);
        Details.Clear();
        void Add(string key, string? value)
        {
            if (!string.IsNullOrWhiteSpace(value)) Details.Add(new InfoRow(Loc.Get(key), value));
        }

        Add("Info_FileName", asset.FileName);
        var date = asset.CaptureDate.LocalDateTime.ToString("F", culture);
        if (asset.CaptureDate.UtcOffset is { } off) date += $" (UTC{(off < TimeSpan.Zero ? "-" : "+")}{off:hh\\:mm})";
        Add("Info_Captured", date + " · " + Loc.Get("DateSource_" + asset.CaptureDate.Source));
        Add("Info_Imported", asset.ImportedUtc.ToLocalTime().ToString("g", culture));
        var (w, h) = asset.DisplaySize;
        Add("Info_Dimensions", w > 0 ? $"{w} × {h} ({w * (double)h / 1_000_000:0.#} MP)" : null);
        Add("Info_Format", $"{asset.Format} · {SelectionActions.FormatBytes(asset.FileSize)}");
        if (asset.Duration is { } d) Add("Info_Duration", d.ToString(@"h\:mm\:ss", culture));
        Add("Info_Camera", asset.Camera.DisplayName);
        Add("Info_Lens", asset.Camera.Lens);
        var exposure = new List<string>();
        if (asset.Camera.FNumber is { } f) exposure.Add($"ƒ/{f:0.#}");
        if (asset.Camera.ExposureTimeSeconds is { } t) exposure.Add(t < 1 ? $"1/{Math.Round(1 / t)} s" : $"{t:0.#} s");
        if (asset.Camera.Iso is { } iso) exposure.Add($"ISO {iso}");
        if (asset.Camera.FocalLengthMm is { } fl) exposure.Add($"{fl:0} mm");
        Add("Info_Exposure", string.Join("  ", exposure));
        if (asset.Location is { } loc)
        {
            Add("Info_Location", string.Create(culture, $"{loc.Latitude:0.00000}, {loc.Longitude:0.00000}") + (loc.Altitude is { } alt ? string.Create(culture, $" · {alt:0} m") : string.Empty));
        }

        if (file is not null)
        {
            Add("Info_Storage", file.StorageMode == StorageMode.Managed ? Loc.Get("Info_StorageManaged") : Loc.Get("Info_StorageReferenced"));
            Add("Info_Path", context.Current?.Resolve(file));
            if (!file.IsAvailable) Add("Info_Status", Loc.Get("Viewer_Unavailable"));
        }
    }

    [RelayCommand]
    private Task NextAsync() => CanGoNext ? ShowAsync(Index + 1) : Task.CompletedTask;

    [RelayCommand]
    private Task PreviousAsync() => CanGoPrevious ? ShowAsync(Index - 1) : Task.CompletedTask;

    [RelayCommand]
    private void RotateView() => ViewRotation = (ViewRotation + 1) % 4;

    [RelayCommand]
    private void ToggleInfo() => IsInfoOpen = !IsInfoOpen;

    [RelayCommand]
    private void ToggleFullScreen() => window.SetFullScreen(!window.IsFullScreen);

    public void ExitFullScreen()
    {
        if (window.IsFullScreen) window.SetFullScreen(false);
    }

    [RelayCommand]
    private async Task ToggleFavoriteAsync()
    {
        if (Asset is null) return;
        IsFavorite = !IsFavorite;
        await actions.SetFavoriteAsync([Asset.Id], IsFavorite);
    }

    /// <summary>Shows the unedited original (or back to the edit) without changing anything.</summary>
    [RelayCommand]
    private async Task CompareAsync()
    {
        if (Asset is null || !HasEdits) return;
        ShowingOriginal = !ShowingOriginal;
        _cts?.Cancel();
        _cts = new CancellationTokenSource();
        IsLoading = true;
        try
        {
            await RenderAsync(Asset, TargetLongSide(Asset, 1.0), preserveView: true, _cts.Token);
        }
        catch (OperationCanceledException)
        {
            // superseded
        }
        finally
        {
            IsLoading = false;
        }
    }

    [RelayCommand]
    private async Task PlayLiveVideoAsync()
    {
        if (Asset is null || context.Current is not { } library) return;
        var files = await assets.GetFilesAsync(Asset.Id, CancellationToken.None);
        var live = files.FirstOrDefault(f => f.Role == FileRole.LiveVideo);
        if (live is null) return;
        var path = library.Resolve(live);
        if (File.Exists(path))
        {
            IsVideo = true;
            VideoSource = new Uri(path);
        }
    }

    [RelayCommand]
    private void Edit()
    {
        if (Asset is { MediaType: MediaType.Photo } a && !HasError) navigation.ToEditor(a.Id);
    }

    [RelayCommand]
    private async Task DeleteAsync()
    {
        if (Asset is null) return;
        if (Asset.IsInTrash) return;
        if (!await actions.MoveToTrashAsync([Asset.Id])) return;
        var remaining = _ids.Where(id => id != Asset.Id).ToList();
        if (remaining.Count == 0)
        {
            navigation.GoBack();
            return;
        }

        _ids = remaining;
        await ShowAsync(Math.Min(Index, remaining.Count - 1));
    }

    [RelayCommand] private Task ExportAsync() => Asset is null ? Task.CompletedTask : actions.ExportAsync([Asset.Id]);

    [RelayCommand] private Task AddToAlbumAsync() => Asset is null ? Task.CompletedTask : actions.AddToAlbumAsync([Asset.Id]);

    [RelayCommand]
    private async Task SaveDetailsAsync()
    {
        if (Asset is null) return;
        await organization.UpdateDetailsAsync(Asset.Id, new AssetDetailsUpdate(EditTitle, EditDescription, Rating < 0 ? 0 : (int)Rating, null), CancellationToken.None);
    }

    [RelayCommand]
    private async Task AddKeywordAsync(string? keyword)
    {
        if (Asset is null || string.IsNullOrWhiteSpace(keyword)) return;
        await organization.AddKeywordsAsync([Asset.Id], [keyword.Trim()], CancellationToken.None);
        if (!Keywords.Contains(keyword.Trim(), StringComparer.OrdinalIgnoreCase)) Keywords.Add(keyword.Trim());
    }

    [RelayCommand]
    private async Task RemoveKeywordAsync(string? keyword)
    {
        if (Asset is null || keyword is null) return;
        await organization.RemoveKeywordAsync([Asset.Id], keyword, CancellationToken.None);
        Keywords.Remove(keyword);
    }

    [RelayCommand]
    private async Task ClearLocationAsync()
    {
        if (Asset is null) return;
        await organization.UpdateDetailsAsync(Asset.Id, new AssetDetailsUpdate(EditTitle, EditDescription, Rating < 0 ? 0 : (int)Rating, null, ClearLocation: true), CancellationToken.None);
        await ShowAsync(Index);
    }

    public void Cancel()
    {
        _cts?.Cancel();
        _idle?.Cancel();
        _foreground?.Dispose();
        _foreground = null;
    }

    private async Task LoadPeopleAsync(Asset asset, CancellationToken ct)
    {
        ShowPeople = asset.MediaType == MediaType.Photo && await people.IsAvailableAsync();
        if (!ShowPeople) return;
        IsExcludedFromPeople = await people.People.IsExcludedAsync(asset.Id, ct);
        var faces = await people.People.GetFacesOfAssetAsync(asset.Id, ct);
        var names = new List<string>();
        foreach (var face in faces.Where(f => f.Review != FaceReview.Ignored))
        {
            var person = face.PersonId is { } id ? await people.People.GetPersonAsync(id, ct) : null;
            names.Add(person is null ? Loc.Get("People_UngroupedFace") : PeopleCoordinator.DisplayName(person));
        }

        PeopleText = IsExcludedFromPeople ? Loc.Get("Info_PeopleExcluded")
            : names.Count == 0 ? Loc.Get("Info_NoPeople")
            : string.Join(", ", names);
    }

    [RelayCommand]
    private async Task ExcludeFromPeopleAsync()
    {
        if (Asset is not { } asset) return;
        await people.People.ExcludeAsync([asset.Id], CancellationToken.None);
        await LoadPeopleAsync(asset, CancellationToken.None);
    }

    [RelayCommand]
    private async Task IncludeInPeopleAsync()
    {
        if (Asset is not { } asset) return;
        await people.People.IncludeAsync([asset.Id], CancellationToken.None);
        await people.AnalyzeAsync([asset.Id]);
        await LoadPeopleAsync(asset, CancellationToken.None);
    }

    /// <summary>Runs the analysis of this photo again; names given to its faces are kept.</summary>
    [RelayCommand]
    private async Task AnalyzePeopleAgainAsync()
    {
        if (Asset is not { } asset) return;
        await people.People.ResetAnalysisAsync([asset.Id], CancellationToken.None);
        await people.AnalyzeAsync([asset.Id]);
        await LoadPeopleAsync(asset, CancellationToken.None);
    }
}
