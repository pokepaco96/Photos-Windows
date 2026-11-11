using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.UI.Dispatching;
using PhotosWindows.App.ViewModels;
using PhotosWindows.Imaging;
using PhotosWindows.Infrastructure;
using PhotosWindows.Infrastructure.Logging;
using PhotosWindows.Infrastructure.Settings;
using PhotosWindows.MachineLearning;
using PhotosWindows.Platform.Windows;

namespace PhotosWindows.App.Services;

/// <summary>Composition root: the only place that knows every implementation.</summary>
public static class AppHost
{
    /// <summary>Environment variable that points the app to an alternate profile folder (portable use, isolated QA runs).</summary>
    public const string DataDirVariable = "PHOTOSWINDOWS_DATA_DIR";

    public static IServiceProvider Build(DispatcherQueue dispatcher)
    {
        // Optional portable/alternate profile (settings, recent libraries, logs). Default: %LOCALAPPDATA%\PhotosWindows.
        var dataDir = Environment.GetEnvironmentVariable(DataDirVariable);
        var paths = new DefaultAppPaths(string.IsNullOrWhiteSpace(dataDir) ? null : Path.GetFullPath(dataDir));
        var services = new ServiceCollection();
        services.AddLogging(b =>
        {
            b.SetMinimumLevel(LogLevel.Information);
            b.AddProvider(new RollingFileLoggerProvider(paths.LogsPath));
        });

        services.AddPhotosWindowsCore(paths);
        services.AddPhotosWindowsImaging();
        services.AddPhotosWindowsPlatform();
        // QA only: a folder plays the role of a connected device so the Devices page can be driven by UI Automation.
        if (Environment.GetEnvironmentVariable(PhotosWindows.Application.Devices.SimulatedFolderDeviceProvider.EnvironmentVariable) is { Length: > 0 } simulated)
        {
            var canDelete = Environment.GetEnvironmentVariable(PhotosWindows.Application.Devices.SimulatedFolderDeviceProvider.EnvironmentVariable + "_DELETE") == "1";
            Microsoft.Extensions.DependencyInjection.Extensions.ServiceCollectionDescriptorExtensions.Replace(services,
                ServiceDescriptor.Singleton<PhotosWindows.Application.Devices.IPortableMediaDeviceProvider>(new PhotosWindows.Application.Devices.SimulatedFolderDeviceProvider(simulated, canDelete)));
        }

        services.AddPhotosWindowsMachineLearning();

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
        services.AddSingleton<PeopleCoordinator>();

        services.AddSingleton<ShellViewModel>();
        services.AddTransient<WelcomeViewModel>();
        services.AddTransient<LibraryViewModel>();
        services.AddTransient<ViewerViewModel>();
        services.AddTransient<EditorViewModel>();
        services.AddTransient<AlbumsViewModel>();
        services.AddTransient<PlacesViewModel>();
        services.AddTransient<MemoriesViewModel>();
        services.AddTransient<DevicesViewModel>();
        services.AddTransient<BackupViewModel>();
        services.AddTransient<SlideshowViewModel>();
        services.AddTransient<DuplicatesViewModel>();
        services.AddTransient<ImportsViewModel>();
        services.AddTransient<SettingsViewModel>();
        services.AddTransient<PeopleViewModel>();
        return services.BuildServiceProvider();
    }
}
