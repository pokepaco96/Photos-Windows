using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Data;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Navigation;
using PhotosWindows.App.Services;
using PhotosWindows.App.ViewModels;
using Windows.System;

namespace PhotosWindows.App.Views;

public sealed partial class LibraryPage : Page
{
    private readonly CollectionViewSource _source = new() { IsSourceGrouped = true };
    private readonly List<GroupStyle> _groupStyles;

    public LibraryPage()
    {
        ViewModel = App.Services.GetRequiredService<LibraryViewModel>();
        InitializeComponent();
        _source.Source = ViewModel.Groups;
        AssetsGrid.ItemsSource = _source.View;
        _groupStyles = [.. AssetsGrid.GroupStyle];
        ViewModel.Groups.CollectionChanged += (_, _) => ConfigureReorder();
        ViewModel.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(LibraryViewModel.ItemSize)) ApplyItemSize();
            if (e.PropertyName == nameof(LibraryViewModel.IsManualAlbum)) ConfigureReorder();
        };
        AssetsGrid.Loaded += (_, _) => ApplyItemSize();
    }

    public LibraryViewModel ViewModel { get; }

    public LibraryNavigation? Target { get; private set; }

    protected override async void OnNavigatedTo(NavigationEventArgs e)
    {
        Target = e.Parameter as LibraryNavigation ?? LibraryNavigation.AllPhotos;
        await ViewModel.LoadAsync(Target);
        ConfigureReorder();
    }

    protected override void OnNavigatedFrom(NavigationEventArgs e) => ViewModel.Dispose();

    private void ApplyItemSize()
    {
        if (AssetsGrid.ItemsPanelRoot is ItemsWrapGrid panel)
        {
            panel.ItemWidth = ViewModel.ItemSize;
            panel.ItemHeight = ViewModel.ItemSize;
        }
    }

    /// <summary>
    /// Manual albums can be reordered by drag &amp; drop, which needs an ungrouped source: the grid is bound to the
    /// album's single group. Every reload replaces that group (and unloads its tiles), so rebind each time;
    /// otherwise removed photos stayed on screen with blank thumbnails (UI run #12).
    /// </summary>
    private void ConfigureReorder()
    {
        var reorder = ViewModel.IsManualAlbum;
        AssetsGrid.CanDragItems = reorder;
        AssetsGrid.CanReorderItems = reorder;
        AssetsGrid.AllowDrop = reorder;
        if (reorder)
        {
            object source = ViewModel.Groups.Count == 1 ? ViewModel.Groups[0] : new List<AssetItemViewModel>();
            if (!ReferenceEquals(AssetsGrid.ItemsSource, source)) AssetsGrid.ItemsSource = source;
            AssetsGrid.GroupStyle.Clear();
        }
        else if (!ReferenceEquals(AssetsGrid.ItemsSource, _source.View))
        {
            AssetsGrid.ItemsSource = _source.View;
            AssetsGrid.GroupStyle.Clear();
            foreach (var style in _groupStyles) AssetsGrid.GroupStyle.Add(style);
        }
    }

    private async void OnDragItemsCompleted(ListViewBase sender, DragItemsCompletedEventArgs args)
    {
        if (!ViewModel.IsManualAlbum || ViewModel.Groups.Count != 1) return;
        foreach (var item in args.Items.OfType<AssetItemViewModel>())
        {
            var index = ViewModel.Groups[0].IndexOf(item);
            if (index >= 0) await ViewModel.MoveInAlbumAsync(item.Id, index);
        }
    }

    private void OnContainerContentChanging(ListViewBase sender, ContainerContentChangingEventArgs args)
    {
        if (args.Item is not AssetItemViewModel item) return;
        // Accessible name/id on the container itself, so screen readers and UI automation can identify each tile.
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(args.ItemContainer, item.AutomationName);
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetAutomationId(args.ItemContainer, "Tile_" + item.Summary.FileName);
        if (args.InRecycleQueue)
        {
            item.Unload();
            return;
        }

        if (args.Phase == 0)
        {
            // Phase 1 runs after the layout pass, so fast scrolling does not start loads for tiles that are only passing by.
            args.RegisterUpdateCallback(1, OnContainerContentChanging);
        }
        else
        {
            _ = ViewModel.LoadTileAsync(item);
        }
    }

    private void OnSelectionChanged(object sender, SelectionChangedEventArgs e) =>
        ViewModel.UpdateSelection(AssetsGrid.SelectedItems.OfType<AssetItemViewModel>().Select(i => i.Id).ToList());

    private void OnDoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
    {
        if ((e.OriginalSource as FrameworkElement)?.DataContext is AssetItemViewModel item) ViewModel.Open(item);
    }

    private void OnRightTapped(object sender, RightTappedRoutedEventArgs e)
    {
        // Right-clicking an unselected tile acts on that tile only.
        if ((e.OriginalSource as FrameworkElement)?.DataContext is AssetItemViewModel item && !AssetsGrid.SelectedItems.Contains(item))
        {
            AssetsGrid.SelectedItems.Clear();
            AssetsGrid.SelectedItems.Add(item);
        }
    }

    private void OnOpenSelected(object sender, RoutedEventArgs e)
    {
        if (AssetsGrid.SelectedItems.OfType<AssetItemViewModel>().FirstOrDefault() is { } item) ViewModel.Open(item);
    }

    /// <summary>PreviewKeyDown: the focused GridViewItem handles Enter itself, so KeyDown never saw it (UI run #12).</summary>
    private void OnGridKeyDown(object sender, KeyRoutedEventArgs e)
    {
        switch (e.Key)
        {
            case VirtualKey.Enter when AssetsGrid.SelectedItems.OfType<AssetItemViewModel>().FirstOrDefault() is { } item:
                ViewModel.Open(item);
                e.Handled = true;
                break;
            case VirtualKey.Delete when ViewModel.HasSelection:
                (ViewModel.IsTrash ? ViewModel.DeletePermanentlyCommand : ViewModel.TrashCommand).Execute(null);
                e.Handled = true;
                break;
            case VirtualKey.Escape:
                AssetsGrid.SelectedItems.Clear();
                e.Handled = true;
                break;
        }
    }
}
