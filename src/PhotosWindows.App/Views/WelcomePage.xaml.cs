using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;
using PhotosWindows.App.ViewModels;

namespace PhotosWindows.App.Views;

public sealed partial class WelcomePage : Page
{
    public WelcomePage()
    {
        ViewModel = App.Services.GetRequiredService<WelcomeViewModel>();
        InitializeComponent();
    }

    public WelcomeViewModel ViewModel { get; }

    protected override void OnNavigatedTo(NavigationEventArgs e) => ViewModel.Load();

    private void OnOpenRecent(object sender, RoutedEventArgs e) => ViewModel.OpenRecentCommand.Execute((sender as FrameworkElement)?.Tag as RecentLibrary);

    private void OnForgetRecent(object sender, RoutedEventArgs e) => ViewModel.ForgetCommand.Execute((sender as FrameworkElement)?.Tag as RecentLibrary);
}
