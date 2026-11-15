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
    private readonly IAssetRepository _assets;
    private readonly ILogger<BackgroundProcessing> _logger;
    private readonly SemaphoreSlim _signal = new(0, int.MaxValue);
    private CancellationTokenSource? _cts;

    public BackgroundProcessing(ThumbnailService thumbnails, ImportService import, ILibraryContext context, IAssetRepository assets, ILogger<BackgroundProcessing> logger)
    {
        _thumbnails = thumbnails;
        _import = import;
        _assets = assets;
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
        _ = Task.Run(() => HeartbeatAsync(token), token);
    }

    /// <summary>
    /// Every 10 s, one log line with the queue and lane state, so a slow or busy app can be diagnosed from its log
    /// (counts and sizes only; no paths or names).
    /// </summary>
    private async Task HeartbeatAsync(CancellationToken token)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(10));
        var process = System.Diagnostics.Process.GetCurrentProcess();
        try
        {
            while (await timer.WaitForNextTickAsync(token).ConfigureAwait(false))
            {
                try
                {
                    var stats = await _assets.GetStatisticsAsync(token).ConfigureAwait(false);
                    var unhashed = (await _assets.GetPhotosWithoutPerceptualHashAsync(100_000, token).ConfigureAwait(false)).Count;
                    process.Refresh();
                    ThreadPool.GetAvailableThreads(out var freeWorkers, out _);
                    ThreadPool.GetMaxThreads(out var maxWorkers, out _);
                    _logger.LogInformation(
                        "Heartbeat: pendingThumbs={Pending} unhashed={Unhashed} import={Import} | {Stats} | cpuMs={Cpu} wsMiB={Ws} privateMiB={Private} gcHeapMiB={Heap} threads={Threads} poolBusy={PoolBusy} pending={PoolPending}",
                        stats.PendingThumbnails, unhashed, _import.IsRunning, _thumbnails.Stats,
                        (long)process.TotalProcessorTime.TotalMilliseconds, process.WorkingSet64 >> 20, process.PrivateMemorySize64 >> 20,
                        GC.GetTotalMemory(false) >> 20, process.Threads.Count, maxWorkers - freeWorkers, ThreadPool.PendingWorkItemCount);
                    _logger.LogInformation("Work: {Work}", _thumbnails.TakeWorkStats());
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    _logger.LogDebug(ex, "Heartbeat skipped");
                }
            }
        }
        catch (OperationCanceledException)
        {
            // library closed
        }
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
                // The viewer or the visible grid is waiting for decodes: leave the CPU to them.
                if (_import.IsRunning || _thumbnails.ForegroundBusy)
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
