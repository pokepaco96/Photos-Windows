using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;
using PhotosWindows.App.ViewModels;

namespace PhotosWindows.App.Views;

/// <summary>Memories: cards with a cover, the reason each one is shown, and actions (open, slideshow, album, hide).</summary>
public sealed partial class MemoriesPage : Page
{
    public MemoriesPage()
    {
        ViewModel = App.Services.GetRequiredService<MemoriesViewModel>();
        InitializeComponent();
    }

    public MemoriesViewModel ViewModel { get; }

    // Coming back from a memory or the slideshow keeps today's result (and the scroll position); entering from the
    // sidebar recomputes it, so new imports are taken into account.
    protected override async void OnNavigatedTo(NavigationEventArgs e) => await ViewModel.LoadAsync(regenerate: e.NavigationMode != NavigationMode.Back);

    private void OnCardContainerChanging(ListViewBase sender, ContainerContentChangingEventArgs args)
    {
        if (args.Item is not MemoryCardViewModel card) return;
        AutomationProperties.SetName(args.ItemContainer, card.AutomationName);
        AutomationProperties.SetAutomationId(args.ItemContainer, "Memory_" + card.Key);
    }

    private static MemoryCardViewModel? Card(object sender) => (sender as FrameworkElement)?.Tag as MemoryCardViewModel;

    private void OnOpen(object sender, RoutedEventArgs e)
    {
        if (Card(sender) is { } card) ViewModel.Open(card);
    }

    private void OnSlideshow(object sender, RoutedEventArgs e)
    {
        if (Card(sender) is { } card) ViewModel.Slideshow(card);
    }

    private async void OnCreateAlbum(object sender, RoutedEventArgs e)
    {
        if (Card(sender) is { } card) await ViewModel.CreateAlbumAsync(card);
    }

    private async void OnHide(object sender, RoutedEventArgs e)
    {
        if (Card(sender) is { } card) await ViewModel.HideAsync(card);
    }

    private async void OnRestore(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is MemoryDecisionViewModel decision) await ViewModel.RestoreAsync(decision);
    }
}
