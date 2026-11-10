using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;
using PhotosWindows.App.ViewModels;

namespace PhotosWindows.App.Views;

/// <summary>Backup: the originals backup (YEAR/MONTH folders) and a pointer to the restorable library backup.</summary>
public sealed partial class BackupPage : Page
{
    public BackupPage()
    {
        ViewModel = App.Services.GetRequiredService<BackupViewModel>();
        InitializeComponent();
    }

    public BackupViewModel ViewModel { get; }

    protected override async void OnNavigatedTo(NavigationEventArgs e) => await ViewModel.LoadAsync();
}
