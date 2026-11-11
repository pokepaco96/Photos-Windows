using Microsoft.Extensions.Logging;
using PhotosWindows.Application.Abstractions;
using PhotosWindows.Application.Import;
using PhotosWindows.Application.Thumbnails;

namespace PhotosWindows.App.Services;

/// <summary>
/// Generates pending thumbnails and perceptual hashes while the app is idle, so a freshly imported library fills in
/// progressively. Restarted whenever the open library changes.
/// </summary>
public sealed class BackgroundProcessing : IDisposable
{
    private readonly ThumbnailService _thumbnails;
    private readonly ImportService _import;
    private readonly ILogger<BackgroundProcessing> _logger;
    private readonly SemaphoreSlim _signal = new(0, int.MaxValue);
    private CancellationTokenSource? _cts;

    public BackgroundProcessing(ThumbnailService thumbnails, ImportService import, ILibraryContext context, ILogger<BackgroundProcessing> logger)
    {
        _thumbnails = thumbnails;
        _import = import;
        _logger = logger;
        context.LibraryChanged += (_, library) =>
        {
            Stop();
            if (library is not null) Start();
        };
    }

    /// <summary>Wakes the worker (e.g. after an import).</summary>
    public void Signal() => _signal.Release();

    private void Start()
    {
        _cts = new CancellationTokenSource();
        var token = _cts.Token;
        _ = Task.Run(() => RunAsync(token), token);
    }

    private void Stop()
    {
        _cts?.Cancel();
        _cts?.Dispose();
        _cts = null;
    }

    private async Task RunAsync(CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            try
            {
                // Imports have priority: copying and hashing originals is the work the user is waiting for. Thumbnails
                // the grid shows are still generated on demand; the rest of the queue resumes when the import ends.
                if (_import.IsRunning)
                {
                    await Task.Delay(TimeSpan.FromSeconds(1), token).ConfigureAwait(false);
                    continue;
                }

                var processed = await _thumbnails.ProcessPendingAsync(40, token).ConfigureAwait(false);
                if (processed == 0)
                {
                    await _signal.WaitAsync(TimeSpan.FromSeconds(30), token).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception ex) when (ex is IOException or InvalidOperationException or System.Data.Common.DbException)
            {
                _logger.LogWarning(ex, "Background processing paused after an error");
                await Task.Delay(TimeSpan.FromSeconds(10), token).ConfigureAwait(false);
            }
        }
    }

    public void Dispose()
    {
        Stop();
        _signal.Dispose();
    }
}
