using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.UI.Dispatching;
using PhotosWindows.App.ViewModels;
using PhotosWindows.Imaging;
using PhotosWindows.Infrastructure;
using PhotosWindows.Infrastructure.Logging;
using PhotosWindows.Infrastructure.Settings;
using PhotosWindows.Platform.Windows;

namespace PhotosWindows.App.Services;

/// <summary>Composition root: the only place that knows every implementation.</summary>
public static class AppHost
{
    public static IServiceProvider Build(DispatcherQueue dispatcher)
    {
        var paths = new DefaultAppPaths();
        var services = new ServiceCollection();
        services.AddLogging(b =>
        {
            b.SetMinimumLevel(LogLevel.Information);
            b.AddProvider(new RollingFileLoggerProvider(paths.LogsPath));
        });

        services.AddPhotosWindowsCore(paths);
        services.AddPhotosWindowsImaging();
        services.AddPhotosWindowsPlatform();

        services.AddSingleton(new UiThread(dispatcher));
        services.AddSingleton<WindowContext>();
        services.AddSingleton<NavigationService>();
        services.AddSingleton<DialogService>();
        services.AddSingleton<ThemeService>();
        services.AddSingleton<ImageLoader>();
        services.AddSingleton<BackgroundProcessing>();
        services.AddSingleton<ActivityCenter>();
        services.AddSingleton<ImportCoordinator>();
        services.AddSingleton<SelectionActions>();

        services.AddSingleton<ShellViewModel>();
        services.AddTransient<WelcomeViewModel>();
        services.AddTransient<LibraryViewModel>();
        services.AddTransient<ViewerViewModel>();
        services.AddTransient<EditorViewModel>();
        services.AddTransient<AlbumsViewModel>();
        services.AddTransient<DuplicatesViewModel>();
        services.AddTransient<ImportsViewModel>();
        services.AddTransient<SettingsViewModel>();
        return services.BuildServiceProvider();
    }
}
