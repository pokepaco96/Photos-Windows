using System.ComponentModel;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
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
    }

    public ViewerViewModel ViewModel { get; }

    protected override async void OnNavigatedTo(NavigationEventArgs e)
    {
        if (e.Parameter is ViewerNavigation target) await ViewModel.LoadAsync(target);
        Focus(FocusState.Programmatic);
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

    /// <summary>At zoom 1 the photo fits the stage; quarter-turn view rotation swaps the box.</summary>
    private void FitImage()
    {
        var w = Stage.ActualWidth;
        var h = Stage.ActualHeight;
        if (w <= 0 || h <= 0) return;
        var rotated = ViewModel.ViewRotation % 2 == 1;
        Photo.Width = rotated ? h : w;
        Photo.Height = rotated ? w : h;
        Photo.Margin = rotated ? new Thickness((w - h) / 2, (h - w) / 2, (w - h) / 2, (h - w) / 2) : new Thickness(0);
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
            var p = e.GetPosition(Photo);
            Zoomer.ChangeView(p.X * 2.5 - Zoomer.ViewportWidth / 2, p.Y * 2.5 - Zoomer.ViewportHeight / 2, 2.5f);
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
            case VirtualKey.Escape:
                if (App.Services.GetRequiredService<WindowContext>().IsFullScreen) ViewModel.ExitFullScreen();
                else Frame.GoBack();
                break;
            default: e.Handled = false; break;
        }
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
