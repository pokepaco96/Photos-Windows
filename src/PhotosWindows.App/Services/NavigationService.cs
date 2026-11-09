using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media.Animation;
using PhotosWindows.App.Views;
using PhotosWindows.Domain;

namespace PhotosWindows.App.Services;

/// <summary>What a library grid should show.</summary>
public sealed record LibraryNavigation(LibraryScope Scope, long? AlbumId = null, string? SearchText = null, long? PersonId = null)
{
    public static LibraryNavigation AllPhotos { get; } = new(LibraryScope.All);
}

/// <summary>Opens the viewer on <see cref="Ids"/>[<see cref="Index"/>]; the list enables previous/next.</summary>
public sealed record ViewerNavigation(IReadOnlyList<long> Ids, int Index);

public sealed class NavigationService
{
    private Frame? _frame;

    /// <summary>Raised after navigation with the page type and the navigation parameter.</summary>
    public event EventHandler<(Type Page, object? Parameter)>? Navigated;

    public bool CanGoBack => _frame?.CanGoBack == true;

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
