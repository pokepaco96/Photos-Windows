using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;
using PhotosWindows.App.ViewModels;

namespace PhotosWindows.App.Views;

public sealed partial class ImportsPage : Page
{
    public ImportsPage()
    {
        ViewModel = App.Services.GetRequiredService<ImportsViewModel>();
        InitializeComponent();
    }

    public ImportsViewModel ViewModel { get; }

    protected override async void OnNavigatedTo(NavigationEventArgs e) => await ViewModel.LoadAsync();

    private void OnResume(object sender, RoutedEventArgs e) => ViewModel.ResumeCommand.Execute((sender as FrameworkElement)?.Tag as ImportJobRow);
}
