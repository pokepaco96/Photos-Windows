using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Navigation;
using PhotosWindows.App.Services;
using PhotosWindows.App.ViewModels;
using Windows.System;

namespace PhotosWindows.App.Views;

/// <summary>Slideshow: Space plays/pauses, Left/Right step, Esc exits. Every control is also a named button.</summary>
public sealed partial class SlideshowPage : Page
{
    private Microsoft.UI.Dispatching.DispatcherQueueTimer? _timer;

    public SlideshowPage()
    {
        ViewModel = App.Services.GetRequiredService<SlideshowViewModel>();
        InitializeComponent();
        Loaded += (_, _) => PlayPauseButton.Focus(FocusState.Programmatic);
    }

    public SlideshowViewModel ViewModel { get; }

    protected override async void OnNavigatedTo(NavigationEventArgs e)
    {
        if (e.Parameter is not SlideshowNavigation target) return;
        await ViewModel.StartAsync(target);
        _timer = DispatcherQueue.CreateTimer();
        _timer.Interval = ViewModel.Interval;
        _timer.Tick += async (_, _) => await ViewModel.TickAsync();
        _timer.Start();
    }

    protected override void OnNavigatedFrom(NavigationEventArgs e)
    {
        _timer?.Stop();
        _timer = null;
    }

    private void OnKeyDown(object sender, KeyRoutedEventArgs e)
    {
        switch (e.Key)
        {
            case VirtualKey.Left:
                ViewModel.PreviousCommand.Execute(null);
                break;
            case VirtualKey.Right:
                ViewModel.NextCommand.Execute(null);
                break;
            case VirtualKey.Escape:
                ViewModel.ExitCommand.Execute(null);
                break;
            case VirtualKey.Space when e.OriginalSource is not Button: // a focused button already handles Space itself
                ViewModel.TogglePlayCommand.Execute(null);
                break;
            default:
                return;
        }

        e.Handled = true;
    }
}
