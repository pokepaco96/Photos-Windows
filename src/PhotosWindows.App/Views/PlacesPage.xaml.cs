using System.Runtime.InteropServices.WindowsRuntime;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Microsoft.UI.Xaml.Navigation;
using PhotosWindows.App.Services;
using PhotosWindows.App.ViewModels;
using PhotosWindows.Application.Places;
using PhotosWindows.Domain;
using Windows.System;

namespace PhotosWindows.App.Views;

/// <summary>
/// Places: the offline basemap is rendered off the UI thread into a bitmap; clusters are real buttons laid over it,
/// so they are keyboard- and screen-reader-accessible and addressable by UI Automation.
/// </summary>
public sealed partial class PlacesPage : Page
{
    private WriteableBitmap? _bitmap;
    private bool _rendering;
    private bool _renderPending;
    private Windows.Foundation.Point? _dragStart;
    private MapView? _dragView;
    private bool _dragged;

    public PlacesPage()
    {
        ViewModel = App.Services.GetRequiredService<PlacesViewModel>();
        InitializeComponent();
        ViewModel.MapInvalidated += (_, _) => Invalidate();
        ActualThemeChanged += (_, _) => Invalidate();
    }

    public PlacesViewModel ViewModel { get; }

    protected override async void OnNavigatedTo(NavigationEventArgs e)
    {
        await ViewModel.ActivateAsync();
        Invalidate();
    }

    private int MapWidth => Math.Max(1, (int)MapHost.ActualWidth);
    private int MapHeight => Math.Max(1, (int)MapHost.ActualHeight);

    private MapView CurrentView() => ViewModel.EnsureView(MapWidth, MapHeight);

    private void SetView(MapView view)
    {
        ViewModel.View = view;
        Invalidate();
    }

    private void Invalidate()
    {
        if (!ViewModel.ShowMap || MapHost.ActualWidth < 2 || MapHost.ActualHeight < 2)
        {
            ClusterLayer.Children.Clear();
            return;
        }

        PlaceClusters(CurrentView());
        _ = RenderAsync();
    }

    /// <summary>Renders the basemap; while a frame is being drawn further requests collapse into one.</summary>
    private async Task RenderAsync()
    {
        if (_rendering)
        {
            _renderPending = true;
            return;
        }

        _rendering = true;
        try
        {
            do
            {
                _renderPending = false;
                var view = CurrentView();
                var scale = XamlRoot?.RasterizationScale ?? 1.0;
                var device = view.Scaled(scale);
                var theme = ActualTheme == ElementTheme.Dark ? MapTheme.Dark : MapTheme.Light;
                var image = await Task.Run(() => ViewModel.Renderer.Render(device, theme));
                if (_bitmap is null || _bitmap.PixelWidth != image.Width || _bitmap.PixelHeight != image.Height)
                {
                    _bitmap = new WriteableBitmap(image.Width, image.Height);
                    MapImage.Source = _bitmap;
                }

                using (var stream = _bitmap.PixelBuffer.AsStream())
                {
                    await stream.WriteAsync(image.Pixels);
                }

                _bitmap.Invalidate();
                MapImage.Width = view.Width;
                MapImage.Height = view.Height;
            }
            while (_renderPending);
        }
        finally
        {
            _rendering = false;
        }
    }

    private void PlaceClusters(MapView view)
    {
        ClusterLayer.Children.Clear();
        var selectedKey = ViewModel.Selected?.Key;
        foreach (var (cluster, x, y) in ViewModel.VisibleClusters(view))
        {
            var size = Math.Clamp(26 + 6 * Math.Log10(cluster.Count) * 2, 26, 56);
            var selected = cluster.Key == selectedKey;
            var button = new Button
            {
                Width = size,
                Height = size,
                Padding = new Thickness(0),
                CornerRadius = new CornerRadius(size / 2),
                Content = new TextBlock { Text = cluster.Count > 999 ? "999+" : cluster.Count.ToString(System.Globalization.CultureInfo.CurrentCulture), FontSize = 12, Foreground = (Brush)Microsoft.UI.Xaml.Application.Current.Resources["TextOnAccentFillColorPrimaryBrush"], FontWeight = Microsoft.UI.Text.FontWeights.SemiBold },
                // Accent-filled markers stand out on both map themes; the selected one is inverted.
                Style = (Style)Microsoft.UI.Xaml.Application.Current.Resources["AccentButtonStyle"],
                BorderThickness = new Thickness(selected ? 3 : 2),
                BorderBrush = (Brush)Microsoft.UI.Xaml.Application.Current.Resources[selected ? "TextFillColorPrimaryBrush" : "TextOnAccentFillColorPrimaryBrush"],
                Tag = cluster,
            };
            var label = new PlaceGroupViewModel(cluster).AutomationName;
            AutomationProperties.SetAutomationId(button, "Places_Cluster");
            AutomationProperties.SetName(button, label);
            ToolTipService.SetToolTip(button, label);
            button.Click += OnClusterClicked;
            Canvas.SetLeft(button, x - size / 2);
            Canvas.SetTop(button, y - size / 2);
            ClusterLayer.Children.Add(button);
        }
    }

    private void OnClusterClicked(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is PlaceCluster cluster) ViewModel.Select(cluster);
    }

    private void OnMapSizeChanged(object sender, SizeChangedEventArgs e) => Invalidate();

    private void OnMapPointerPressed(object sender, PointerRoutedEventArgs e)
    {
        if (e.OriginalSource is not (Grid or Image or Canvas)) return;
        _dragStart = e.GetCurrentPoint(MapHost).Position;
        _dragView = CurrentView();
        _dragged = false;
        MapHost.CapturePointer(e.Pointer);
        MapHost.Focus(FocusState.Pointer);
    }

    private void OnMapPointerMoved(object sender, PointerRoutedEventArgs e)
    {
        if (_dragStart is not { } start || _dragView is null) return;
        var p = e.GetCurrentPoint(MapHost).Position;
        var (dx, dy) = (p.X - start.X, p.Y - start.Y);
        if (!_dragged && Math.Abs(dx) + Math.Abs(dy) < 4) return;
        _dragged = true;
        SetView(_dragView.PanBy(dx, dy));
    }

    private void OnMapPointerReleased(object sender, PointerRoutedEventArgs e)
    {
        _dragStart = null;
        _dragView = null;
        MapHost.ReleasePointerCapture(e.Pointer);
    }

    private void OnMapWheel(object sender, PointerRoutedEventArgs e)
    {
        var point = e.GetCurrentPoint(MapHost);
        var delta = point.Properties.MouseWheelDelta / 120.0 * 0.5;
        SetView(CurrentView().ZoomAt(delta, point.Position.X, point.Position.Y));
        e.Handled = true;
    }

    private void OnMapDoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
    {
        if (e.OriginalSource is not (Grid or Image or Canvas)) return;
        var p = e.GetPosition(MapHost);
        SetView(CurrentView().ZoomAt(1, p.X, p.Y));
    }

    private void OnMapKeyDown(object sender, KeyRoutedEventArgs e)
    {
        var view = CurrentView();
        MapView? next = e.Key switch
        {
            VirtualKey.Left => view.PanBy(80, 0),
            VirtualKey.Right => view.PanBy(-80, 0),
            VirtualKey.Up => view.PanBy(0, 80),
            VirtualKey.Down => view.PanBy(0, -80),
            VirtualKey.Add or (VirtualKey)187 => view.ZoomAt(1, view.Width / 2.0, view.Height / 2.0),      // + and =
            VirtualKey.Subtract or (VirtualKey)189 => view.ZoomAt(-1, view.Width / 2.0, view.Height / 2.0), // - and _
            _ => null,
        };
        if (e.Key == VirtualKey.Home)
        {
            ViewModel.FitAll(view.Width, view.Height);
            e.Handled = true;
            return;
        }

        if (next is null) return;
        SetView(next);
        e.Handled = true;
    }

    private void OnZoomIn(object sender, RoutedEventArgs e)
    {
        var v = CurrentView();
        SetView(v.ZoomAt(1, v.Width / 2.0, v.Height / 2.0));
    }

    private void OnZoomOut(object sender, RoutedEventArgs e)
    {
        var v = CurrentView();
        SetView(v.ZoomAt(-1, v.Width / 2.0, v.Height / 2.0));
    }

    private void OnFitAll(object sender, RoutedEventArgs e) => ViewModel.FitAll(MapWidth, MapHeight);

    private void OnGroupClicked(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is PlaceGroupViewModel group) ViewModel.Selected = group;
    }

    private void OnGroupContainerChanging(ListViewBase sender, ContainerContentChangingEventArgs args)
    {
        if (args.Item is not PlaceGroupViewModel group) return;
        AutomationProperties.SetName(args.ItemContainer, group.AutomationName);
        AutomationProperties.SetAutomationId(args.ItemContainer, "Places_Group");
    }

    private void OnTrayContainerChanging(ListViewBase sender, ContainerContentChangingEventArgs args)
    {
        if (args.Item is not AssetItemViewModel item) return;
        AutomationProperties.SetName(args.ItemContainer, item.AutomationName);
        AutomationProperties.SetAutomationId(args.ItemContainer, "PlacesTile_" + item.Summary.FileName);
        if (args.InRecycleQueue)
        {
            item.Unload();
            return;
        }

        _ = ViewModel.LoadTrayThumbnailAsync(item);
    }

    private void OnTraySelectionChanged(object sender, SelectionChangedEventArgs e) => OpenPhotoButton.IsEnabled = TrayGrid.SelectedItem is not null;

    private void OnTrayDoubleTapped(object sender, DoubleTappedRoutedEventArgs e) => OpenSelected();

    private void OnTrayKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key != VirtualKey.Enter) return;
        OpenSelected();
        e.Handled = true;
    }

    private void OnOpenPhoto(object sender, RoutedEventArgs e) => OpenSelected();

    private void OpenSelected()
    {
        if (TrayGrid.SelectedItem is AssetItemViewModel item) ViewModel.OpenInViewer(item);
    }
}
