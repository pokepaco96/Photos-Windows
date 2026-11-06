using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;
using PhotosWindows.App.ViewModels;

namespace PhotosWindows.App.Views;

public sealed partial class DuplicatesPage : Page
{
    public DuplicatesPage()
    {
        ViewModel = App.Services.GetRequiredService<DuplicatesViewModel>();
        InitializeComponent();
    }

    public DuplicatesViewModel ViewModel { get; }

    protected override async void OnNavigatedTo(NavigationEventArgs e) => await ViewModel.LoadAsync();

    private void OnKeep(object sender, RoutedEventArgs e) => ViewModel.KeepCommand.Execute((sender as FrameworkElement)?.Tag as DuplicateCandidateViewModel);

    private void OnDismiss(object sender, RoutedEventArgs e) => ViewModel.DismissCommand.Execute((sender as FrameworkElement)?.Tag as DuplicateGroupViewModel);
}
