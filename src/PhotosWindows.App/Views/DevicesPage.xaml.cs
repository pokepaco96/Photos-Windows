using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;
using PhotosWindows.App.ViewModels;

namespace PhotosWindows.App.Views;

/// <summary>Devices: browse and preview what a connected phone or camera exposes, and import a selection.</summary>
public sealed partial class DevicesPage : Page
{
    public DevicesPage()
    {
        ViewModel = App.Services.GetRequiredService<DevicesViewModel>();
        InitializeComponent();
    }

    public DevicesViewModel ViewModel { get; }

    protected override async void OnNavigatedTo(NavigationEventArgs e) => await ViewModel.RefreshAsync();

    protected override async void OnNavigatedFrom(NavigationEventArgs e) => await ViewModel.LeaveAsync();

    private void OnContainerContentChanging(ListViewBase sender, ContainerContentChangingEventArgs args)
    {
        if (args.Item is not DeviceItemViewModel item) return;
        AutomationProperties.SetName(args.ItemContainer, item.AutomationName);
        AutomationProperties.SetAutomationId(args.ItemContainer, "DeviceTile_" + item.Item.FolderId + "/" + item.Name);
        if (!args.InRecycleQueue) _ = ViewModel.LoadThumbnailAsync(item);
    }

    private void OnSelectionChanged(object sender, SelectionChangedEventArgs e) =>
        ViewModel.UpdateSelection(ItemsGrid.SelectedItems.OfType<DeviceItemViewModel>().ToList());
}
