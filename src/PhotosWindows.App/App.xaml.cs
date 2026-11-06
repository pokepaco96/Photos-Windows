using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.UI.Xaml;
using PhotosWindows.App.Services;

namespace PhotosWindows.App;

public partial class App : Microsoft.UI.Xaml.Application
{
    private MainWindow? _window;
    private ILogger<App>? _logger;

    public App()
    {
        InitializeComponent();
        UnhandledException += OnUnhandledException;
        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            _logger?.LogError(e.Exception, "Unobserved task exception");
            e.SetObserved();
        };
    }

    /// <summary>Application services. Available after <see cref="OnLaunched"/>.</summary>
    public static IServiceProvider Services { get; private set; } = null!;

    public static MainWindow MainWindow => ((App)Current)._window ?? throw new InvalidOperationException("Main window not created yet.");

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        Services = AppHost.Build(Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread());
        _logger = Services.GetRequiredService<ILogger<App>>();
        _logger.LogInformation("Photos Windows {Version} starting", typeof(App).Assembly.GetName().Version);
        _window = new MainWindow();
        Services.GetRequiredService<WindowContext>().Attach(_window);
        _window.Activate();
        _ = _window.InitializeAsync();
    }

    private void OnUnhandledException(object sender, Microsoft.UI.Xaml.UnhandledExceptionEventArgs e)
    {
        _logger?.LogCritical(e.Exception, "Unhandled UI exception");
        // Keep the app alive for recoverable errors and tell the user; the catalog is transactional, so no partial writes remain.
        e.Handled = true;
        if (_window is not null)
        {
            _ = Services.GetRequiredService<DialogService>().ShowErrorAsync(Loc.Get("Error_Unexpected"), e.Exception);
        }
    }
}
