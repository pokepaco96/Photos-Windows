using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;
using PhotosWindows.App.ViewModels;

namespace PhotosWindows.App.Views;

public sealed partial class AlbumsPage : Page
{
    public AlbumsPage()
    {
        ViewModel = App.Services.GetRequiredService<AlbumsViewModel>();
        InitializeComponent();
    }

    public AlbumsViewModel ViewModel { get; }

    protected override async void OnNavigatedTo(NavigationEventArgs e) => await ViewModel.LoadAsync();

    private async void OnItemClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is AlbumCardViewModel card) await ViewModel.OpenAsync(card);
    }

    /// <summary>Accessible name/id on each card container (a StackPanel template has no automation peer of its own).</summary>
    private void OnCardContainerChanging(ListViewBase sender, ContainerContentChangingEventArgs args)
    {
        if (args.Item is not AlbumCardViewModel card) return;
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(args.ItemContainer, $"{card.Name}, {card.Subtitle}");
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetAutomationId(args.ItemContainer, "AlbumCard_" + card.Name);
    }

    private static AlbumCardViewModel? CardOf(object sender) => (sender as FrameworkElement)?.Tag as AlbumCardViewModel;

    private void OnRename(object sender, RoutedEventArgs e) => ViewModel.RenameCommand.Execute(CardOf(sender));

    private void OnEditRules(object sender, RoutedEventArgs e) => ViewModel.EditSmartCommand.Execute(CardOf(sender));

    private void OnMove(object sender, RoutedEventArgs e) => ViewModel.MoveCommand.Execute(CardOf(sender));

    private void OnDelete(object sender, RoutedEventArgs e) => ViewModel.DeleteCommand.Execute(CardOf(sender));
}
