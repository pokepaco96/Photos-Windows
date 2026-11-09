using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.UI.Xaml.Media;
using PhotosWindows.App.Services;
using PhotosWindows.Application.Abstractions;
using PhotosWindows.Application.Library;
using PhotosWindows.Application.Organization;
using PhotosWindows.Application.Places;
using PhotosWindows.Application.Thumbnails;
using PhotosWindows.Domain;

namespace PhotosWindows.App.ViewModels;

/// <summary>A cluster shown in list mode or selected on the map.</summary>
public sealed partial class PlaceGroupViewModel(PlaceCluster cluster) : ObservableObject
{
    public PlaceCluster Cluster { get; } = cluster;
    public string Key => Cluster.Key;
    public int Count => Cluster.Count;
    public string CountText => Loc.Format(Cluster.Count == 1 ? "Places_OneItem" : "Places_Items", Cluster.Count);
    public string Coordinates => PlacesService.FormatCoordinate(Cluster.Latitude, Cluster.Longitude);

    public string Dates
    {
        get
        {
            var culture = CultureInfo.CurrentCulture;
            var first = Cluster.First.ToString("d", culture);
            var last = Cluster.Last.ToString("d", culture);
            return first == last ? first : $"{first} – {last}";
        }
    }

    /// <summary>Approximate area, never an address: "About 40.42° N, 3.70° W".</summary>
    public string Title => Loc.Format("Places_Near", Coordinates);

    public string AutomationName => $"{Title}, {CountText}, {Dates}";

    [ObservableProperty] public partial ImageSource? Cover { get; set; }
}

public sealed record PlacesAlbumOption(long? Id, string Name)
{
    public override string ToString() => Name;
}

/// <summary>
/// Places page state. Survives navigation to the viewer (the page is cached), so filters, map position and the
/// selected cluster are kept when the user comes back. Filters and view mode are also persisted in settings.
/// </summary>
public sealed partial class PlacesViewModel(
    PlacesService places,
    IMapRenderer renderer,
    IAssetRepository assets,
    OrganizationService organization,
    ThumbnailService thumbnails,
    ImageLoader images,
    ISettingsStore settings,
    NavigationService navigation,
    DialogService dialogs) : ObservableObject
{
    private PlacesSnapshot? _snapshot;
    private bool _restoring;
    private int _loadVersion;
    private string? _selectedKey;
    private bool _refit;
    private bool _syncingYear;
    private bool _restored;

    public IMapRenderer Renderer => renderer;

    public ObservableCollection<PlaceGroupViewModel> Groups { get; } = [];
    public ObservableCollection<AssetItemViewModel> Tray { get; } = [];
    public ObservableCollection<PlacesAlbumOption> Albums { get; } = [];

    /// <summary>"All dates" followed by every year that has located photos, newest first (quick date filter).</summary>
    public ObservableCollection<string> Years { get; } = [];

    /// <summary>Current camera; the page updates it while the user pans and zooms.</summary>
    public MapView? View { get; set; }

    /// <summary>Raised when the clusters or the view must be redrawn.</summary>
    public event EventHandler? MapInvalidated;

    [ObservableProperty] public partial bool IsLoading { get; set; }
    [ObservableProperty] public partial bool IsEmpty { get; set; }
    [ObservableProperty] public partial bool HasPoints { get; set; }
    [ObservableProperty] public partial string EmptyTitle { get; set; } = string.Empty;
    [ObservableProperty] public partial string EmptyMessage { get; set; } = string.Empty;
    [ObservableProperty] public partial string Summary { get; set; } = string.Empty;
    [ObservableProperty] public partial string WithoutLocationText { get; set; } = string.Empty;
    [ObservableProperty] public partial bool HasWithoutLocation { get; set; }
    [ObservableProperty] public partial bool HasFilters { get; set; }

    [ObservableProperty] public partial bool IsMapEnabled { get; set; } = true;
    [ObservableProperty] public partial bool IsListMode { get; set; }
    [ObservableProperty] public partial bool ShowMap { get; set; }
    [ObservableProperty] public partial bool ShowList { get; set; }
    [ObservableProperty] public partial bool MapUnavailable { get; set; }

    [ObservableProperty] public partial DateTimeOffset? FromDate { get; set; }
    [ObservableProperty] public partial DateTimeOffset? ToDate { get; set; }
    [ObservableProperty] public partial int MediaTypeIndex { get; set; }
    [ObservableProperty] public partial bool FavoritesOnly { get; set; }
    [ObservableProperty] public partial PlacesAlbumOption? SelectedAlbum { get; set; }

    /// <summary>0 = all dates, n = Years[n]; -1 when the date range is custom (shows the "Custom range" placeholder).</summary>
    [ObservableProperty] public partial int YearIndex { get; set; }

    [ObservableProperty] public partial PlaceGroupViewModel? Selected { get; set; }
    [ObservableProperty] public partial bool HasSelection { get; set; }

    public string Attribution => Loc.Format("Places_Attribution", renderer.Attribution);

    partial void OnFromDateChanged(DateTimeOffset? value)
    {
        SyncYear();
        FiltersChanged();
    }

    partial void OnToDateChanged(DateTimeOffset? value)
    {
        SyncYear();
        FiltersChanged();
    }

    partial void OnYearIndexChanged(int value)
    {
        if (_syncingYear || value < 0) return;
        _syncingYear = true;
        var restoring = _restoring;
        _restoring = true; // one reload for both dates
        if (value == 0 || value >= Years.Count)
        {
            FromDate = null;
            ToDate = null;
        }
        else
        {
            var year = int.Parse(Years[value], CultureInfo.InvariantCulture);
            FromDate = new DateTimeOffset(new DateTime(year, 1, 1));
            ToDate = new DateTimeOffset(new DateTime(year, 12, 31));
        }

        _restoring = restoring;
        _syncingYear = false;
        FiltersChanged();
    }

    private void SyncYear()
    {
        if (_syncingYear) return;
        _syncingYear = true;
        YearIndex = (FromDate, ToDate) switch
        {
            (null, null) => 0,
            ({ } f, { } t) when f.Month == 1 && f.Day == 1 && t.Month == 12 && t.Day == 31 && f.Year == t.Year
                && Years.IndexOf(f.Year.ToString(CultureInfo.InvariantCulture)) is var i and > 0 => i,
            _ => -1,
        };
        _syncingYear = false;
    }
    partial void OnMediaTypeIndexChanged(int value) => FiltersChanged();
    partial void OnFavoritesOnlyChanged(bool value) => FiltersChanged();
    partial void OnSelectedAlbumChanged(PlacesAlbumOption? value) => FiltersChanged();

    partial void OnIsMapEnabledChanged(bool value)
    {
        SavePreferences();
        UpdateMode();
    }

    partial void OnIsListModeChanged(bool value)
    {
        SavePreferences();
        UpdateMode();
    }

    partial void OnSelectedChanged(PlaceGroupViewModel? value)
    {
        HasSelection = value is not null;
        _selectedKey = value?.Key;
        _ = LoadTrayAsync(value);
        MapInvalidated?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Called on every navigation to the page: restores preferences once, then reloads data (cheap).</summary>
    public async Task ActivateAsync()
    {
        await RefreshChoicesAsync();
        await LoadAsync();
    }

    /// <summary>
    /// Refreshes the album and year choices (they change with imports and album edits), then applies the saved
    /// preferences the first time, or re-selects the current choices afterwards.
    /// </summary>
    private async Task RefreshChoicesAsync()
    {
        var prefs = _restored ? null : settings.Load().Places;
        var albumId = prefs?.AlbumId ?? SelectedAlbum?.Id;
        var albums = (await organization.GetAlbumsAsync(CancellationToken.None))
            .Where(a => a.Kind == AlbumKind.Manual)
            .OrderBy(a => a.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
        var all = await Task.Run(() => places.LoadAsync(new PlacesFilter(), CancellationToken.None));

        _restoring = true;
        try
        {
            Albums.Clear();
            Albums.Add(new PlacesAlbumOption(null, Loc.Get("Places_AllAlbums")));
            foreach (var album in albums) Albums.Add(new PlacesAlbumOption(album.Id, album.Name));
            Years.Clear();
            Years.Add(Loc.Get("Places_AllDates"));
            foreach (var y in all.Points.Select(p => p.CaptureLocal.Year).Distinct().OrderDescending()) Years.Add(y.ToString(CultureInfo.InvariantCulture));

            if (prefs is not null)
            {
                FromDate = prefs.From is { } f ? new DateTimeOffset(f) : null;
                ToDate = prefs.To is { } t ? new DateTimeOffset(t) : null;
                MediaTypeIndex = prefs.MediaType switch { MediaType.Photo => 1, MediaType.Video => 2, _ => 0 };
                FavoritesOnly = prefs.FavoritesOnly;
                IsMapEnabled = prefs.MapEnabled;
                IsListMode = prefs.ViewMode == PlacesViewMode.List;
            }

            // An album that no longer exists falls back to "all albums".
            SelectedAlbum = Albums.FirstOrDefault(a => a.Id == albumId) ?? Albums[0];
        }
        finally
        {
            _restoring = false;
            _restored = true;
        }

        SyncYear();
        UpdateMode();
    }

    public PlacesFilter CurrentFilter() => new()
    {
        From = FromDate?.Date,
        To = ToDate?.Date,
        MediaType = MediaTypeIndex switch { 1 => MediaType.Photo, 2 => MediaType.Video, _ => null },
        AlbumId = SelectedAlbum?.Id,
        FavoritesOnly = FavoritesOnly,
    };

    private void FiltersChanged()
    {
        if (_restoring) return;
        _refit = true; // new filters: frame the photos they select
        SavePreferences();
        _ = LoadAsync();
    }

    private void SavePreferences()
    {
        if (_restoring) return;
        var s = settings.Load();
        var prefs = CurrentFilter().ApplyTo(s.Places) with
        {
            ViewMode = IsListMode ? PlacesViewMode.List : PlacesViewMode.Map,
            MapEnabled = IsMapEnabled,
        };
        settings.Save(s with { Places = prefs });
    }

    private void UpdateMode()
    {
        MapUnavailable = IsMapEnabled && !renderer.IsAvailable;
        ShowMap = HasPoints && IsMapEnabled && !IsListMode && renderer.IsAvailable;
        ShowList = HasPoints && !ShowMap;
        MapInvalidated?.Invoke(this, EventArgs.Empty);
    }

    public async Task LoadAsync()
    {
        var version = ++_loadVersion;
        IsLoading = true;
        try
        {
            var filter = CurrentFilter();
            var snapshot = await Task.Run(() => places.LoadAsync(filter, CancellationToken.None));
            if (version != _loadVersion) return; // a newer filter change superseded this load
            var groups = await Task.Run(() => snapshot.ListGroups());
            if (version != _loadVersion) return;
            _snapshot = snapshot;
            HasFilters = !filter.IsEmpty;
            HasPoints = snapshot.Points.Count > 0;
            IsEmpty = !HasPoints;
            (EmptyTitle, EmptyMessage) = HasFilters
                ? (Loc.Get("Places_EmptyFilteredTitle"), Loc.Get("Places_EmptyFilteredMessage"))
                : (Loc.Get("Places_EmptyTitle"), Loc.Get("Places_EmptyMessage"));
            Summary = HasPoints ? Loc.Format("Places_Summary", snapshot.Points.Count, groups.Count) : string.Empty;
            HasWithoutLocation = snapshot.WithoutLocationCount > 0;
            WithoutLocationText = Loc.Format(snapshot.WithoutLocationCount == 1 ? "Places_WithoutLocationOne" : "Places_WithoutLocation", snapshot.WithoutLocationCount);

            Groups.Clear();
            foreach (var g in groups) Groups.Add(new PlaceGroupViewModel(g));
            foreach (var g in Groups.Take(60)) _ = LoadCoverAsync(g);

            UpdateMode();
            RestoreSelection();
        }
        finally
        {
            if (version == _loadVersion) IsLoading = false;
        }
    }

    /// <summary>Re-selects the previously selected cluster (by key at its level) after a reload.</summary>
    private void RestoreSelection()
    {
        if (_selectedKey is null || _snapshot is null)
        {
            Selected = null;
            return;
        }

        var level = int.Parse(_selectedKey.Split('/')[0], CultureInfo.InvariantCulture);
        var match = _snapshot.ClustersAt(level).FirstOrDefault(c => c.Key == _selectedKey);
        Selected = match is null ? null : new PlaceGroupViewModel(match);
    }

    /// <summary>Clusters for the current view, with screen positions. Only those inside the view (plus a margin).</summary>
    public IReadOnlyList<(PlaceCluster Cluster, double X, double Y)> VisibleClusters(MapView view)
    {
        if (_snapshot is null) return [];
        var result = new List<(PlaceCluster, double, double)>();
        foreach (var c in _snapshot.ClustersAt(PlaceClustering.LevelFor(view.Zoom)))
        {
            var (x, y) = view.Project(c.Latitude, c.Longitude);
            if (x >= -40 && y >= -40 && x <= view.Width + 40 && y <= view.Height + 40) result.Add((c, x, y));
        }

        return result;
    }

    public void Select(PlaceCluster cluster) => Selected = new PlaceGroupViewModel(cluster);

    /// <summary>
    /// The camera for a map of the given size: the previous one (e.g. when coming back from the viewer), resized;
    /// or, the first time and after a filter change, one that frames every photo.
    /// </summary>
    public MapView EnsureView(int width, int height)
    {
        if (_snapshot is not null && (View is null || _refit))
        {
            _refit = false;
            View = _snapshot.Bounds is { } b ? MapView.Fit(b, width, height) : MapView.World(width, height);
        }

        View ??= MapView.World(width, height);
        if (View.Width != width || View.Height != height) View = View.Resize(width, height);
        return View;
    }

    /// <summary>Frames the current photos (button "Show all").</summary>
    public void FitAll(int width, int height)
    {
        if (_snapshot?.Bounds is { } b) View = MapView.Fit(b, width, height);
        MapInvalidated?.Invoke(this, EventArgs.Empty);
    }

    [RelayCommand]
    private void ZoomToSelection()
    {
        if (Selected is null || View is null) return;
        View = MapView.Fit(Selected.Cluster.Bounds, View.Width, View.Height, maxZoom: Math.Min(MapView.MaxZoom, PlaceClustering.LevelFor(View.Zoom) + 3));
        IsListMode = false;
        MapInvalidated?.Invoke(this, EventArgs.Empty);
    }

    [RelayCommand]
    private void ClearFilters()
    {
        _restoring = true;
        FromDate = null;
        ToDate = null;
        YearIndex = 0;
        MediaTypeIndex = 0;
        FavoritesOnly = false;
        SelectedAlbum = Albums.FirstOrDefault();
        _restoring = false;
        FiltersChanged();
    }

    [RelayCommand]
    private void ClearSelection() => Selected = null;

    [RelayCommand]
    private void ShowWithoutLocation() => navigation.ToLibrary(new LibraryNavigation(LibraryScope.WithoutLocation));

    public void OpenInViewer(AssetItemViewModel item)
    {
        var ids = Tray.Select(t => t.Id).ToList();
        navigation.ToViewer(new ViewerNavigation(ids, Math.Max(0, ids.IndexOf(item.Id))));
    }

    [RelayCommand]
    private async Task RemoveLocationAsync()
    {
        if (Selected is null) return;
        var ids = Selected.Cluster.AssetIds;
        if (!await dialogs.ConfirmAsync(Loc.Get("Places_RemoveTitle"), Loc.Format("Places_RemoveMessage", ids.Count), Loc.Get("Places_RemoveConfirm"), destructive: true)) return;
        await places.RemoveLocationAsync(ids, CancellationToken.None);
        Selected = null;
        await LoadAsync();
    }

    private async Task LoadTrayAsync(PlaceGroupViewModel? group)
    {
        Tray.Clear();
        if (group is null) return;
        var summaries = await assets.QueryAsync(new AssetQuery { Ids = group.Cluster.AssetIds, Sort = AssetSort.CaptureDateDescending }, CancellationToken.None);
        if (!ReferenceEquals(group, Selected)) return;
        foreach (var s in summaries) Tray.Add(new AssetItemViewModel(s));
    }

    public Task LoadTrayThumbnailAsync(AssetItemViewModel item) => item.LoadThumbnailAsync(thumbnails, images, 240);

    private async Task LoadCoverAsync(PlaceGroupViewModel group)
    {
        var path = await thumbnails.GetOrCreateAsync(group.Cluster.CoverAssetId, ThumbnailSize.Small, CancellationToken.None);
        if (path is not null) group.Cover = await images.FromFileAsync(path, 160, CancellationToken.None);
    }
}
