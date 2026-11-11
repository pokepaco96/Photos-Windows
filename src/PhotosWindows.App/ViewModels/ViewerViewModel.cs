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
    private const int ViewerRenderSize = 3840;
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
        Image = null; // never leave the previous photo on screen as if it were this one
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

            // 1. The grid thumbnail is usually on disk already: show it at once, before any other work.
            if (IsPhoto && await TryShowCachedAsync(asset, ThumbnailSize.Small, cts.Token)) timing.Mark("small");

            var original = await editing.ResolveOriginalAsync(asset.Id, cts.Token);
            timing.Mark("resolve");
            if (original is null)
            {
                await ShowPreviewAsync(asset, cts.Token);
                ShowError(Loc.Get("Viewer_Unavailable"), "Unavailable");
                await LoadInspectorAsync(asset, cts.Token);
                return;
            }

            if (IsVideo)
            {
                Image = null;
                VideoSource = new Uri(original);
                LoadState = "Ready";
                await LoadInspectorAsync(asset, cts.Token);
                return;
            }

            // 2. Better preview (2048 px), generated on the interactive lane if needed: background thumbnail work
            //    for the rest of the library never delays it.
            await ShowPreviewAsync(asset, cts.Token);
            timing.Mark("preview");

            // 3. Full quality; the inspector loads meanwhile and never delays the first image.
            LoadingMessage = Loc.Get("Viewer_ImprovingQuality");
            var render = RenderAsync(asset, cts.Token);
            await LoadInspectorAsync(asset, cts.Token);
            timing.Mark("inspector");
            await render;
            timing.Mark("render");
        }
        catch (OperationCanceledException)
        {
            // Navigated to another item; that load owns the state now.
            timing.Outcome = "cancelled";
        }
        catch (MediaDecodeException ex)
        {
            ShowError(ex.Message, "Error");
            timing.Outcome = "decode-error";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or ArgumentException
                                       or System.Runtime.InteropServices.ExternalException or OutOfMemoryException or TimeoutException)
        {
            logger.LogWarning(ex, "Viewer load {LoadId} failed", loadId);
            ShowError(Loc.Get("Viewer_LoadFailed"), "Error");
            timing.Outcome = "error";
        }
        finally
        {
            if (_cts == cts)
            {
                IsLoading = false;
                LoadingMessage = string.Empty;
                if (LoadState == "Loading") LoadState = Image is null ? "Error" : "Preview";
            }

            timing.Log(logger, Asset);
        }
    }

    /// <summary>Shows a cached thumbnail if it exists, without generating anything. True when an image was shown.</summary>
    private async Task<bool> TryShowCachedAsync(Asset asset, ThumbnailSize size, CancellationToken ct)
    {
        var path = await thumbnails.TryGetCachedAsync(asset.Id, size, ct);
        if (path is null) return false;
        try
        {
            Image = await images.FromFileAsync(path, 0, ct);
            if (LoadState == "Loading") LoadState = "Preview";
            return true;
        }
        catch (Exception ex) when (ex is IOException or System.Runtime.InteropServices.ExternalException)
        {
            // A damaged cache file is regenerated by the next step.
            logger.LogDebug(ex, "Cached thumbnail could not be shown");
            return false;
        }
    }

    private async Task ShowPreviewAsync(Asset asset, CancellationToken ct)
    {
        if (await TryShowCachedAsync(asset, ThumbnailSize.Large, ct)) return;
        LoadingMessage = Loc.Get("Viewer_LoadingPreview");
        var path = await thumbnails.GetOrCreateAsync(asset.Id, ThumbnailSize.Large, ThumbnailPriority.Interactive, ct);
        if (path is not null)
        {
            Image = await images.FromFileAsync(path, 0, ct);
            if (LoadState == "Loading") LoadState = "Preview";
        }
    }

    private async Task RenderAsync(Asset asset, CancellationToken ct)
    {
        var recipe = ShowingOriginal ? EditRecipe.Identity : await editing.GetRecipeAsync(asset.Id, ct);
        var rendered = await editing.RenderFromOriginalAsync(asset.Id, recipe, ViewerRenderSize, EncodeOptions.PreviewJpeg, ct);
        ct.ThrowIfCancellationRequested();
        if (rendered is null)
        {
            // Keep whatever preview is on screen; say that full quality is not available.
            ShowError(Loc.Get("Viewer_LoadFailed"), Image is null ? "Error" : "Preview");
            return;
        }

        Image = await ImageLoader.FromBytesAsync(rendered.Encoded, 0);
        LoadState = "Ready";
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
            LoadingMessage = Image is null ? Loc.Get("Viewer_SlowLoad") : Loc.Get("Viewer_ImprovingQuality");
            logger.LogWarning("Viewer load {LoadId} still running after {Seconds} s (image shown: {Shown})", loadId, SlowLoadNotice.TotalSeconds, Image is not null);
        });
    }

    /// <summary>Called by the page when the Image element has decoded and shown its source.</summary>
    public void OnImageOpened() => logger.LogDebug("Viewer load {LoadId}: image opened ({State})", _loadId, LoadState);

    /// <summary>Monotonic per-stage timings for one load, written to the local log. No paths or names.</summary>
    private sealed class LoadTiming(long id)
    {
        private readonly System.Diagnostics.Stopwatch _sw = System.Diagnostics.Stopwatch.StartNew();
        private readonly List<string> _marks = [];

        public string Outcome { get; set; } = "completed";

        public void Mark(string stage) => _marks.Add($"{stage}={_sw.ElapsedMilliseconds}ms");

        public void Log(ILogger logger, Asset? asset) => logger.LogInformation(
            "Viewer load {LoadId} {Outcome} in {Ms} ms: {Stages} (asset {AssetId}, {Format}, {Bytes} bytes, edited {Edited})",
            id, Outcome, _sw.ElapsedMilliseconds, string.Join(", ", _marks), asset?.Id, asset?.Format, asset?.FileSize, asset?.HasEdits);
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
            await RenderAsync(Asset, _cts.Token);
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

    public void Cancel() => _cts?.Cancel();

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
