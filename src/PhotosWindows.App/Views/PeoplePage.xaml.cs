using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;
using PhotosWindows.App.Services;
using PhotosWindows.App.ViewModels;

namespace PhotosWindows.App.Views;

public sealed partial class PeoplePage : Page
{
    public PeoplePage()
    {
        ViewModel = App.Services.GetRequiredService<PeopleViewModel>();
        InitializeComponent();
        AutomationProperties.SetName(PeopleGrid, Title);
        AutomationProperties.SetName(UngroupedGrid, Loc.Get("People_UngroupedName"));
    }

    public PeopleViewModel ViewModel { get; }

    public string Title { get; } = Loc.Get("People_Title");

    protected override async void OnNavigatedTo(NavigationEventArgs e) => await ViewModel.LoadAsync();

    protected override void OnNavigatedFrom(NavigationEventArgs e) => ViewModel.Dispose();

    private void OnPersonClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is PersonCardViewModel card) ViewModel.Open(card);
    }

    private void OnSelectionChanged(object sender, SelectionChangedEventArgs e) =>
        ViewModel.UpdateSelection(PeopleGrid.SelectedItems.OfType<PersonCardViewModel>().ToList());

    /// <summary>Accessible name and id on the container itself, so screen readers and UI automation can identify each card.</summary>
    private void OnPersonContainerChanging(ListViewBase sender, ContainerContentChangingEventArgs args)
    {
        if (args.Item is not PersonCardViewModel card) return;
        AutomationProperties.SetName(args.ItemContainer, card.AutomationName);
        AutomationProperties.SetAutomationId(args.ItemContainer, card.IsNamed ? "PersonCard_" + card.Name : $"PersonCard_{card.Person.Id}");
    }

    private void OnFaceContainerChanging(ListViewBase sender, ContainerContentChangingEventArgs args)
    {
        if (args.Item is not FaceTileViewModel tile) return;
        AutomationProperties.SetName(args.ItemContainer, Loc.Format("People_FaceName", tile.Face.Id));
        AutomationProperties.SetAutomationId(args.ItemContainer, $"UngroupedFace_{tile.Face.Id}");
    }

    private async void OnRenamePerson(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is PersonCardViewModel card) await ViewModel.RenamePersonAsync(card);
    }

    private async void OnDeletePerson(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is PersonCardViewModel card) await ViewModel.DeletePersonAsync(card);
    }

    private async void OnNameFace(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is FaceTileViewModel tile) await ViewModel.NameFaceAsync(tile);
    }

    private async void OnAddFace(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is FaceTileViewModel tile) await ViewModel.AddFaceToPersonAsync(tile);
    }

    private async void OnIgnoreFace(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is FaceTileViewModel tile) await ViewModel.IgnoreFaceAsync(tile);
    }
}
