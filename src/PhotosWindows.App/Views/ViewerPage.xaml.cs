using System.ComponentModel;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media.Imaging;
using Microsoft.UI.Xaml.Navigation;
using PhotosWindows.App.Services;
using PhotosWindows.App.ViewModels;
using Windows.Media.Core;
using Windows.Media.Playback;
using Windows.System;

namespace PhotosWindows.App.Views;

public sealed partial class ViewerPage : Page
{
    public ViewerPage()
    {
        ViewModel = App.Services.GetRequiredService<ViewerViewModel>();
        InitializeComponent();
        ViewModel.PropertyChanged += OnViewModelPropertyChanged;
        Loaded += (_, _) => TakeFocus();
        // Clicking the photo (not focusable itself) keeps the viewer's shortcuts working. The ScrollViewer marks
        // pointer events handled, so listen to handled events too (UI run #17: a click left focus outside).
        Stage.AddHandler(PointerPressedEvent, new PointerEventHandler((_, _) => TakeFocus()), handledEventsToo: true);
        Stage.AddHandler(TappedEvent, new TappedEventHandler((_, _) => TakeFocus()), handledEventsToo: true);
    }

    public ViewerViewModel ViewModel { get; }

    protected override async void OnNavigatedTo(NavigationEventArgs e)
    {
        if (e.Parameter is ViewerNavigation target) await ViewModel.LoadAsync(target);
        TakeFocus();
    }

    /// <summary>
    /// Keyboard focus must be inside the viewer or its keys (arrows, Escape, F, I…) are never seen. Focus used to
    /// stay outside after opening from a click or returning from the editor: a Page is not a tab stop by default,
    /// so Focus() silently failed (UI run #15). The page is now a tab stop and takes focus once loaded.
    /// </summary>
    private void TakeFocus()
    {
        if (IsLoaded) Focus(FocusState.Programmatic);
    }

    protected override void OnNavigatedFrom(NavigationEventArgs e)
    {
        ViewModel.PropertyChanged -= OnViewModelPropertyChanged;
        ViewModel.Cancel();
        ViewModel.ExitFullScreen();
        StopVideo();
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(ViewerViewModel.VideoSource):
                StopVideo();
                if (ViewModel.VideoSource is { } uri)
                {
                    Player.Source = MediaSource.CreateFromUri(uri);
                    if (Player.MediaPlayer is { } player) player.MediaFailed += OnMediaFailed;
                }

                break;
            case nameof(ViewerViewModel.Image):
            case nameof(ViewerViewModel.ViewRotation):
                Zoomer.ChangeView(null, null, 1f, disableAnimation: true);
                FitImage();
                break;
        }
    }

    private void OnMediaFailed(MediaPlayer sender, MediaPlayerFailedEventArgs args) =>
        DispatcherQueue.TryEnqueue(() => _ = App.Services.GetRequiredService<DialogService>().ShowMessageAsync(Loc.Get("Viewer_VideoErrorTitle"), Loc.Get("Viewer_VideoError")));

    private void StopVideo()
    {
        if (Player.MediaPlayer is { } player)
        {
            player.MediaFailed -= OnMediaFailed;
            player.Pause();
        }

        Player.Source = null;
    }

    private void OnStageSizeChanged(object sender, SizeChangedEventArgs e) => FitImage();

    private void OnPhotoOpened(object sender, RoutedEventArgs e) => FitImage();

    /// <summary>
    /// At zoom 1 the photo fits the stage, centred. The Image gets the photo's fitted size (not the whole stage):
    /// with a stage-sized box, Stretch=Uniform drew the photo at the top of the box and left an empty band
    /// below it (UI run #12, Info pane open). Quarter-turn view rotation swaps the visual box.
    /// </summary>
    private void FitImage()
    {
        var w = Stage.ActualWidth;
        var h = Stage.ActualHeight;
        if (w <= 0 || h <= 0) return;
        var rotated = ViewModel.ViewRotation % 2 == 1;
        // Read the view model, not Photo.Source: x:Bind may not have pushed the new image yet when this runs from
        // PropertyChanged, which sized the next photo with the previous one's dimensions (UI run #14).
        var (iw, ih) = ViewModel.Image is BitmapSource { PixelWidth: > 0, PixelHeight: > 0 } bitmap
            ? (bitmap.PixelWidth, (double)bitmap.PixelHeight)
            : (rotated ? h : w, rotated ? w : h);
        var (visualW, visualH) = rotated ? (ih, iw) : (iw, ih);
        var scale = Math.Min(w / visualW, h / visualH);
        var pw = iw * scale;
        var ph = ih * scale;
        Photo.Width = pw;
        Photo.Height = ph;
        // The layout slot is exactly the stage, so the (rotated) photo is centred and the zoom extent matches the stage.
        Photo.Margin = new Thickness((w - pw) / 2, (h - ph) / 2, (w - pw) / 2, (h - ph) / 2);
    }

    private void OnImageDoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
    {
        var zoomed = Zoomer.ZoomFactor > 1.01f;
        if (zoomed)
        {
            Zoomer.ChangeView(null, null, 1f);
        }
        else
        {
            // Scroll offsets include the photo's centring margin.
            var p = e.GetPosition(Photo);
            var x = p.X + Photo.Margin.Left;
            var y = p.Y + Photo.Margin.Top;
            Zoomer.ChangeView(x * 2.5 - Zoomer.ViewportWidth / 2, y * 2.5 - Zoomer.ViewportHeight / 2, 2.5f);
        }
    }

    /// <summary>
    /// Arrow keys are taken before child controls see them: the photo's ScrollViewer consumed Left/Right, so they
    /// never reached <see cref="OnKeyDown"/> (UI run #14).
    /// When zoomed in, arrows keep panning the photo.
    /// </summary>
    private void OnPreviewKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.OriginalSource is TextBox or Slider) return; // typing, or seeking a video with the arrows
        var zoomed = Zoomer.ZoomFactor > 1.01f;
        switch (e.Key)
        {
            case VirtualKey.Left when !zoomed:
            case VirtualKey.Right when !zoomed:
                OnKeyDown(sender, e);
                break;
        }
    }

    private void OnKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.OriginalSource is TextBox) return;
        e.Handled = true;
        switch (e.Key)
        {
            case VirtualKey.Left: ViewModel.PreviousCommand.Execute(null); break;
            case VirtualKey.Right: ViewModel.NextCommand.Execute(null); break;
            case VirtualKey.F: ViewModel.ToggleFullScreenCommand.Execute(null); break;
            case VirtualKey.I: ViewModel.ToggleInfoCommand.Execute(null); break;
            case VirtualKey.E: ViewModel.EditCommand.Execute(null); break;
            case VirtualKey.R: ViewModel.RotateViewCommand.Execute(null); break;
            case VirtualKey.M: ViewModel.CompareCommand.Execute(null); break;
            case (VirtualKey)190: ViewModel.ToggleFavoriteCommand.Execute(null); break; // '.' key
            case VirtualKey.Delete: ViewModel.DeleteCommand.Execute(null); break;
            default: e.Handled = false; break; // Escape: see OnEscapeInvoked
        }
    }

    private void OnEscapeInvoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        if (FocusManager.GetFocusedElement(XamlRoot) is TextBox) return; // Escape while typing a keyword
        if (Microsoft.UI.Xaml.Media.VisualTreeHelper.GetOpenPopupsForXamlRoot(XamlRoot).Count > 0) return; // a dialog or menu closes first
        args.Handled = true;
        if (App.Services.GetRequiredService<WindowContext>().IsFullScreen) ViewModel.ExitFullScreen();
        else if (Frame.CanGoBack) Frame.GoBack();
    }

    private void OnKeywordKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key != VirtualKey.Enter) return;
        ViewModel.AddKeywordCommand.Execute(NewKeyword.Text);
        NewKeyword.Text = string.Empty;
        e.Handled = true;
    }

    private void OnRemoveKeyword(object sender, RoutedEventArgs e) => ViewModel.RemoveKeywordCommand.Execute((sender as FrameworkElement)?.Tag as string);
}
