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

        // XAML problems (missing resources, failed bindings) are reported with their details in the local log.
        DebugSettings.IsXamlResourceReferenceTracingEnabled = true;
        DebugSettings.XamlResourceReferenceFailed += (_, e) => _logger.LogError("XAML resource reference failed: {Message}", e.Message);
        DebugSettings.IsBindingTracingEnabled = true;
        DebugSettings.BindingFailed += (_, e) => _logger.LogWarning("Binding failed: {Message}", e.Message);

        try
        {
            _window = new MainWindow();
        }
        catch (Exception ex) when (ex is System.Runtime.InteropServices.COMException or Microsoft.UI.Xaml.Markup.XamlParseException)
        {
            _logger.LogCritical(ex, "The main window could not be created");
            _logger.LogCritical("Embedded XAML resources: {Names}", string.Join(", ", typeof(App).Assembly.GetManifestResourceNames()));
            _logger.LogCritical("XAML diagnosis: {Details}", XamlDiagnostics.Explain("MainWindow.xaml"));
            FatalStartupError(ex);
            return;
        }

        Services.GetRequiredService<WindowContext>().Attach(_window);
        _window.Activate();
        _ = StartAsync(_window);
    }

    private async Task StartAsync(MainWindow window)
    {
        try
        {
            await window.InitializeAsync();
        }
        catch (Exception ex) when (ex is System.Runtime.InteropServices.COMException or Microsoft.UI.Xaml.Markup.XamlParseException or InvalidOperationException)
        {
            _logger?.LogCritical(ex, "Start-up failed");
            FatalStartupError(ex);
        }
    }

    /// <summary>Last-resort message when the UI cannot be shown at all (instead of exiting silently).</summary>
    private static void FatalStartupError(Exception ex)
    {
        var logs = Services.GetRequiredService<PhotosWindows.Application.Abstractions.IAppPaths>().LogsPath;
        _ = NativeMethods.MessageBoxW(IntPtr.Zero,
            $"Photos Windows could not start.\n\n{ex.Message}\n\nDetails were written to:\n{logs}",
            "Photos Windows", 0x10);
        Current.Exit();
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
