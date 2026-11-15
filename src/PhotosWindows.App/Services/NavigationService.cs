using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media.Animation;
using PhotosWindows.App.Views;
using PhotosWindows.Domain;

namespace PhotosWindows.App.Services;

/// <summary>What a library grid should show.</summary>
public sealed record LibraryNavigation(LibraryScope Scope, long? AlbumId = null, string? SearchText = null, long? PersonId = null, IReadOnlyList<long>? Ids = null, string? Title = null)
{
    public static LibraryNavigation AllPhotos { get; } = new(LibraryScope.All);
}

/// <summary>Opens the viewer on <see cref="Ids"/>[<see cref="Index"/>]; the list enables previous/next.</summary>
public sealed record ViewerNavigation(IReadOnlyList<long> Ids, int Index);

/// <summary>Plays <see cref="Ids"/> as a slideshow titled <see cref="Title"/>.</summary>
public sealed record SlideshowNavigation(IReadOnlyList<long> Ids, string Title);

public sealed class NavigationService
{
    private Frame? _frame;

    /// <summary>Raised after navigation with the page type and the navigation parameter.</summary>
    public event EventHandler<(Type Page, object? Parameter)>? Navigated;

    public bool CanGoBack => _frame?.CanGoBack == true;

    /// <summary>
    /// The photo the viewer was showing when the user went back. The library grid scrolls to it, so going back
    /// returns to where the user was instead of the top of the library (the grid page is rebuilt on every visit).
    /// </summary>
    public long? ReturnToAssetId { get; set; }

    /// <summary>
    /// View options (media filter, favourites, grouping) of library grids left for another page, by back-stack depth.
    /// The grid page is rebuilt when the user comes back, so without them a "Videos" view came back as "Photos and
    /// videos" and the video the user had opened was lost in the whole library.
    /// </summary>
    public Dictionary<int, ViewModels.LibraryViewState> LibraryViewStates { get; } = [];

    public Type? CurrentPage => _frame?.Content?.GetType();

    public void Attach(Frame frame)
    {
        _frame = frame;
        _frame.Navigated += (_, e) => Navigated?.Invoke(this, (e.SourcePageType, e.Parameter));
    }

    public void ToLibrary(LibraryNavigation target) => Navigate(typeof(LibraryPage), target);

    public void ToViewer(ViewerNavigation target) => Navigate(typeof(ViewerPage), target, new DrillInNavigationTransitionInfo());

    public void ToEditor(long assetId) => Navigate(typeof(EditorPage), assetId, new DrillInNavigationTransitionInfo());

    public void ToAlbums() => Navigate(typeof(AlbumsPage), null);

    public void ToPlaces() => Navigate(typeof(PlacesPage), null);

    public void ToMemories() => Navigate(typeof(MemoriesPage), null);

    public void ToDevices() => Navigate(typeof(DevicesPage), null);

    public void ToBackup() => Navigate(typeof(BackupPage), null);

    public void ToSlideshow(SlideshowNavigation target) => Navigate(typeof(SlideshowPage), target, new DrillInNavigationTransitionInfo());

    public void ToDuplicates() => Navigate(typeof(DuplicatesPage), null);

    public void ToPeople() => Navigate(typeof(PeoplePage), null);

    public void ToPerson(long personId) => ToLibrary(new LibraryNavigation(LibraryScope.All, PersonId: personId));

    public void ToImports() => Navigate(typeof(ImportsPage), null);

    public void ToSettings() => Navigate(typeof(SettingsPage), null);

    public void ToWelcome()
    {
        Navigate(typeof(WelcomePage), null);
        _frame?.BackStack.Clear();
    }

    public void GoBack()
    {
        if (_frame?.CanGoBack == true) _frame.GoBack();
    }

    public void ClearHistory() => _frame?.BackStack.Clear();

    private void Navigate(Type page, object? parameter, NavigationTransitionInfo? transition = null)
    {
        if (_frame is null) return;
        _frame.Navigate(page, parameter, transition ?? new EntranceNavigationTransitionInfo());
    }
}
