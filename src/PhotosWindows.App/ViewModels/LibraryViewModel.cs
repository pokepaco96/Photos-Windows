using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PhotosWindows.App.Services;
using PhotosWindows.Application.Abstractions;
using PhotosWindows.Application.Editing;
using PhotosWindows.Application.Library;
using PhotosWindows.Application.Organization;
using PhotosWindows.Application.Search;
using PhotosWindows.Application.Thumbnails;
using PhotosWindows.Domain;

namespace PhotosWindows.App.ViewModels;

/// <summary>
/// Grid of assets for every library section (all photos, videos, favourites, albums, smart albums, search,
/// hidden, recently deleted). Loads lightweight rows only; bitmaps are loaded per visible tile.
/// </summary>
public sealed partial class LibraryViewModel : ObservableObject, IDisposable
{
    private readonly IAssetRepository _assets;
    private readonly OrganizationService _organization;
    private readonly ThumbnailService _thumbnails;
    private readonly ImageLoader _images;
    private readonly NavigationService _navigation;
    private readonly SelectionActions _actions;
    private readonly ImportCoordinator _import;
    private readonly EditingService _editing;
    private readonly ISettingsStore _settings;
    private readonly UiThread _ui;
    private readonly PeopleCoordinator _people;
    private Person? _person;
    private readonly Dictionary<long, AssetItemViewModel> _byId = [];
    private CancellationTokenSource? _loadCts;
    private LibraryNavigation _target = LibraryNavigation.AllPhotos;
    private Album? _album;
    private bool _refreshQueued;

    public LibraryViewModel(IAssetRepository assets, OrganizationService organization, ThumbnailService thumbnails, ImageLoader images,
        NavigationService navigation, SelectionActions actions, ImportCoordinator import, EditingService editing, ISettingsStore settings, UiThread ui, PeopleCoordinator people)
    {
        _people = people;
        _assets = assets;
        _organization = organization;
        _thumbnails = thumbnails;
        _images = images;
        _navigation = navigation;
        _actions = actions;
        _import = import;
        _editing = editing;
        _settings = settings;
        _ui = ui;
        ItemSize = settings.Load().GridItemSize;
        _organization.LibraryContentChanged += OnContentChanged;
        _import.ImportFinished += OnContentChanged;
        _editing.RecipeSaved += OnRecipeSaved;
        _thumbnails.ThumbnailReady += OnThumbnailReady;
    }

    public ObservableCollection<AssetGroup> Groups { get; } = [];

    public IReadOnlyList<long> SelectedIds { get; set; } = [];

    [ObservableProperty] public partial string Title { get; set; } = string.Empty;
    [ObservableProperty] public partial string Subtitle { get; set; } = string.Empty;
    [ObservableProperty] public partial bool IsLoading { get; set; }
    [ObservableProperty] public partial bool IsEmpty { get; set; }
    [ObservableProperty] public partial string EmptyTitle { get; set; } = string.Empty;
    [ObservableProperty] public partial string EmptyMessage { get; set; } = string.Empty;
    [ObservableProperty] public partial bool HasSelection { get; set; }
    [ObservableProperty] public partial string SelectionText { get; set; } = string.Empty;
    [ObservableProperty] public partial double ItemSize { get; set; }
    [ObservableProperty] public partial int GroupingIndex { get; set; }
    [ObservableProperty] public partial int MediaFilterIndex { get; set; }
    [ObservableProperty] public partial bool? FavoritesOnly { get; set; } = false;
    [ObservableProperty] public partial bool CanGroup { get; set; } = true;
    [ObservableProperty] public partial bool IsTrash { get; set; }
    [ObservableProperty] public partial bool IsHiddenScope { get; set; }
    [ObservableProperty] public partial bool IsManualAlbum { get; set; }
    [ObservableProperty] public partial bool IsSmartAlbum { get; set; }
    [ObservableProperty] public partial bool IsNormalScope { get; set; } = true;
    [ObservableProperty] public partial bool ShowImportHint { get; set; }

    /// <summary>A person's photos (People): person commands are shown.</summary>
    [ObservableProperty] public partial bool IsPerson { get; set; }

    /// <summary>People is on and working: photos can be excluded from it.</summary>
    [ObservableProperty] public partial bool PeopleAvailable { get; set; }

    public int DecodeWidth => (int)Math.Ceiling(ItemSize * 2);

    partial void OnItemSizeChanged(double value) => _settings.Save(_settings.Load() with { GridItemSize = (int)value });

    partial void OnGroupingIndexChanged(int value) => _ = ReloadAsync();

    partial void OnMediaFilterIndexChanged(int value) => _ = ReloadAsync();

    partial void OnFavoritesOnlyChanged(bool? value) => _ = ReloadAsync();

    public async Task LoadAsync(LibraryNavigation target)
    {
        _target = target;
        IsTrash = target.Scope == LibraryScope.RecentlyDeleted;
        IsHiddenScope = target.Scope == LibraryScope.Hidden;
        _album = target.AlbumId is { } id ? await _organization.GetAlbumAsync(id, CancellationToken.None) : null;
        _person = target.PersonId is { } pid ? await _people.People.GetPersonAsync(pid, CancellationToken.None) : null;
        IsPerson = _person is not null;
        PeopleAvailable = await _people.IsAvailableAsync();
        IsManualAlbum = _album?.Kind == AlbumKind.Manual;
        IsSmartAlbum = _album?.Kind == AlbumKind.Smart;
        IsNormalScope = !IsTrash && !IsHiddenScope;
        CanGroup = !IsManualAlbum && target.Scope is not LibraryScope.RecentlyAdded;
        GroupingIndex = CanGroup ? (int)GroupingMode.Days : (int)GroupingMode.None;
        Title = _album?.Name
            ?? (_person is not null ? PeopleCoordinator.DisplayName(_person) : null)
            ?? (target.SearchText is { } q ? Loc.Format("Search_Title", q) : Loc.Get("Scope_" + target.Scope));
        await ReloadAsync();
    }

    private AssetQuery BuildQuery()
    {
        var baseQuery = new AssetQuery
        {
            Scope = _target.Scope,
            AlbumId = IsManualAlbum ? _album!.Id : null,
            PersonId = _target.PersonId,
            Smart = IsSmartAlbum ? _album!.SmartDefinition : null,
            MediaType = MediaFilterIndex switch { 1 => MediaType.Photo, 2 => MediaType.Video, _ => null },
            Favorite = FavoritesOnly == true ? true : null,
            Sort = IsManualAlbum ? AssetSort.AlbumOrder
                : _target.Scope == LibraryScope.RecentlyAdded ? AssetSort.ImportDateDescending
                : _settings.Load().DefaultSort,
        };
        return _target.SearchText is { } text ? SearchService.Parse(text, baseQuery) with { MediaType = baseQuery.MediaType ?? SearchService.Parse(text).MediaType } : baseQuery;
    }

    public async Task ReloadAsync()
    {
        _loadCts?.Cancel();
        var cts = _loadCts = new CancellationTokenSource();
        IsLoading = true;
        try
        {
            var rows = await _assets.QueryAsync(BuildQuery(), cts.Token);
            if (cts.IsCancellationRequested) return;
            var mode = CanGroup ? (GroupingMode)GroupingIndex : GroupingMode.None;
            var groups = await Task.Run(() => BuildGroups(rows, mode), cts.Token);
            if (cts.IsCancellationRequested) return;

            foreach (var item in _byId.Values) item.Unload();
            _byId.Clear();
            Groups.Clear();
            foreach (var group in groups)
            {
                foreach (var item in group) _byId[item.Id] = item;
                Groups.Add(group);
            }

            var photos = rows.Count(r => r.MediaType == MediaType.Photo);
            var videos = rows.Count - photos;
            Subtitle = Loc.Format("Library_Counts",
                Loc.Format(photos == 1 ? "Library_PhotoOne" : "Library_PhotoMany", photos),
                Loc.Format(videos == 1 ? "Library_VideoOne" : "Library_VideoMany", videos));
            IsEmpty = rows.Count == 0;
            (EmptyTitle, EmptyMessage) = EmptyTexts();
            ShowImportHint = IsEmpty && _target.Scope == LibraryScope.All && _target.SearchText is null && MediaFilterIndex == 0 && FavoritesOnly != true;
        }
        catch (OperationCanceledException)
        {
            // A newer load replaced this one.
        }
        finally
        {
            if (_loadCts == cts) IsLoading = false;
        }
    }

    private static List<AssetGroup> BuildGroups(IReadOnlyList<AssetSummary> rows, GroupingMode mode)
    {
        var culture = CultureInfo.CurrentCulture;
        if (mode == GroupingMode.None)
        {
            return [new AssetGroup(string.Empty, rows.Select(r => new AssetItemViewModel(r)))];
        }

        string Key(DateTime d) => mode switch
        {
            GroupingMode.Years => d.ToString("yyyy", culture),
            GroupingMode.Months => culture.TextInfo.ToTitleCase(d.ToString("Y", culture)),
            _ => culture.TextInfo.ToTitleCase(d.ToString("D", culture)),
        };

        // Rows are already sorted; consecutive grouping keeps that order.
        var groups = new List<AssetGroup>();
        string? currentKey = null;
        var buffer = new List<AssetItemViewModel>();
        foreach (var row in rows)
        {
            var key = Key(row.CaptureLocal);
            if (key != currentKey && buffer.Count > 0)
            {
                groups.Add(new AssetGroup(currentKey!, buffer));
                buffer = [];
            }

            currentKey = key;
            buffer.Add(new AssetItemViewModel(row));
        }

        if (buffer.Count > 0) groups.Add(new AssetGroup(currentKey!, buffer));
        return groups;
    }

    private (string, string) EmptyTexts()
    {
        if (_target.SearchText is not null) return (Loc.Get("Empty_SearchTitle"), Loc.Get("Empty_SearchMessage"));
        if (IsSmartAlbum) return (Loc.Get("Empty_SmartTitle"), Loc.Get("Empty_SmartMessage"));
        if (IsManualAlbum) return (Loc.Get("Empty_AlbumTitle"), Loc.Get("Empty_AlbumMessage"));
        if (_target.PersonId is not null) return (Loc.Get("Empty_PersonTitle"), Loc.Get("Empty_PersonMessage"));
        return _target.Scope switch
        {
            LibraryScope.RecentlyDeleted => (Loc.Get("Empty_TrashTitle"), Loc.Get("Empty_TrashMessage")),
            LibraryScope.Hidden => (Loc.Get("Empty_HiddenTitle"), Loc.Get("Empty_HiddenMessage")),
            LibraryScope.Favorites => (Loc.Get("Empty_FavoritesTitle"), Loc.Get("Empty_FavoritesMessage")),
            _ => (Loc.Get("Empty_LibraryTitle"), Loc.Get("Empty_LibraryMessage")),
        };
    }

    public Task LoadTileAsync(AssetItemViewModel item) => item.LoadThumbnailAsync(_thumbnails, _images, DecodeWidth);

    public void UpdateSelection(IReadOnlyList<long> ids)
    {
        SelectedIds = ids;
        HasSelection = ids.Count > 0;
        SelectionText = ids.Count == 0 ? string.Empty : Loc.Format("Selection_Count", ids.Count);
    }

    public void Open(AssetItemViewModel item)
    {
        var ids = Groups.SelectMany(g => g).Select(i => i.Id).ToList();
        _navigation.ToViewer(new ViewerNavigation(ids, Math.Max(0, ids.IndexOf(item.Id))));
    }

    [RelayCommand]
    private async Task RenamePersonAsync()
    {
        if (_person is not null && await _people.RenameAsync(_person)) await LoadAsync(_target);
    }

    [RelayCommand]
    private async Task ManageFacesAsync()
    {
        if (_person is not null && await _people.ManageFacesAsync(_person)) await LoadAsync(_target);
    }

    [RelayCommand]
    private async Task MergePersonIntoAsync()
    {
        if (_person is not null && await _people.MergeIntoAsync(_person)) _navigation.ToPeople();
    }

    [RelayCommand]
    private async Task DeletePersonAsync()
    {
        if (_person is not null && await _people.DeleteAsync(_person)) _navigation.ToPeople();
    }

    /// <summary>Selected photos leave People: their faces and descriptors are deleted and they are not analysed again.</summary>
    [RelayCommand]
    private async Task ExcludeFromPeopleAsync()
    {
        if (SelectedIds.Count == 0) return;
        await _people.People.ExcludeAsync(SelectedIds.ToList(), CancellationToken.None);
        if (IsPerson) await ReloadAsync();
    }

    [RelayCommand] private Task FavoriteAsync() => _actions.SetFavoriteAsync(SelectedIds, true);
    [RelayCommand] private Task UnfavoriteAsync() => _actions.SetFavoriteAsync(SelectedIds, false);
    [RelayCommand] private Task AddToAlbumAsync() => _actions.AddToAlbumAsync(SelectedIds);
    [RelayCommand] private Task AddKeywordsAsync() => _actions.AddKeywordsAsync(SelectedIds);
    [RelayCommand] private Task HideAsync() => _actions.SetHiddenAsync(SelectedIds, true);
    [RelayCommand] private Task UnhideAsync() => _actions.SetHiddenAsync(SelectedIds, false);
    [RelayCommand] private Task TrashAsync() => _actions.MoveToTrashAsync(SelectedIds);
    [RelayCommand] private Task RestoreAsync() => _actions.RestoreAsync(SelectedIds);
    [RelayCommand] private Task DeletePermanentlyAsync() => _actions.DeletePermanentlyAsync(SelectedIds);
    [RelayCommand] private Task ExportAsync() => _actions.ExportAsync(SelectedIds);
    [RelayCommand] private Task ImportFolderAsync() => _import.ImportFolderAsync();

    [RelayCommand]
    private Task RemoveFromAlbumAsync() => _album is { Kind: AlbumKind.Manual } album ? _actions.RemoveFromAlbumAsync(album.Id, SelectedIds) : Task.CompletedTask;

    [RelayCommand]
    private async Task EmptyTrashAsync()
    {
        var all = Groups.SelectMany(g => g).Select(i => i.Id).ToList();
        await _actions.DeletePermanentlyAsync(all);
    }

    /// <summary>Moves an item inside a manual album (drag-reorder).</summary>
    public async Task MoveInAlbumAsync(long assetId, int newIndex)
    {
        if (_album is { Kind: AlbumKind.Manual } album) await _organization.MoveInAlbumAsync(album.Id, assetId, newIndex, CancellationToken.None);
    }

    private void OnContentChanged(object? sender, EventArgs e) => QueueRefresh();

    private void OnRecipeSaved(object? sender, long assetId) => QueueRefresh();

    private void QueueRefresh()
    {
        if (_refreshQueued) return;
        _refreshQueued = true;
        _ui.Post(async () =>
        {
            await Task.Delay(150);
            _refreshQueued = false;
            await ReloadAsync();
        });
    }

    private void OnThumbnailReady(object? sender, long assetId) => _ui.Post(() =>
    {
        if (_byId.TryGetValue(assetId, out var item) && item.ThumbnailFailed)
        {
            item.Reset();
        }
    });

    public void Dispose()
    {
        _organization.LibraryContentChanged -= OnContentChanged;
        _import.ImportFinished -= OnContentChanged;
        _editing.RecipeSaved -= OnRecipeSaved;
        _thumbnails.ThumbnailReady -= OnThumbnailReady;
        _loadCts?.Cancel();
        foreach (var item in _byId.Values) item.Unload();
    }
}
