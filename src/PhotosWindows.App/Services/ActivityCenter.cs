using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using Microsoft.UI.Xaml.Controls;

namespace PhotosWindows.App.Services;

public readonly record struct ActivityProgress(double? Fraction, string Message);

public sealed record ActivityOutcome(string Message, InfoBarSeverity Severity, string? ActionText = null, Action? Action = null);

/// <summary>
/// Forwards reports synchronously on the reporting thread. Unlike <see cref="Progress{T}"/> created off the UI
/// thread, it does not hop through the thread pool, so reports keep their order.
/// </summary>
public sealed class InlineProgress<T>(Action<T> handler) : IProgress<T>
{
    public void Report(T value) => handler(value);
}

/// <summary>
/// Shows one long-running operation (import, export, backup, verification) in the shell's status bar with real
/// progress and a Cancel button. Operations run off the UI thread; progress is marshalled back.
/// </summary>
public sealed partial class ActivityCenter(UiThread ui, ILogger<ActivityCenter> logger) : ObservableObject
{
    private CancellationTokenSource? _cts;
    private Action? _action;
    private int _run;

    [ObservableProperty] public partial bool IsOpen { get; set; }
    [ObservableProperty] public partial bool IsBusy { get; set; }
    [ObservableProperty] public partial string Title { get; set; } = string.Empty;
    [ObservableProperty] public partial string Message { get; set; } = string.Empty;
    [ObservableProperty] public partial double Percent { get; set; }
    [ObservableProperty] public partial bool IsIndeterminate { get; set; }
    [ObservableProperty] public partial InfoBarSeverity Severity { get; set; }
    [ObservableProperty] public partial string? ActionText { get; set; }
    [ObservableProperty] public partial bool HasAction { get; set; }

    public event EventHandler? Completed;

    /// <summary>Runs <paramref name="work"/> unless another activity is running. Returns false if it could not start.</summary>
    public async Task<bool> RunAsync(string title, Func<IProgress<ActivityProgress>, CancellationToken, Task<ActivityOutcome>> work)
    {
        if (IsBusy) return false;
        _cts = new CancellationTokenSource();
        IsBusy = true;
        IsOpen = true;
        Title = title;
        Message = Loc.Get("Activity_Starting");
        IsIndeterminate = true;
        Percent = 0;
        Severity = InfoBarSeverity.Informational;
        HasAction = false;
        var run = ++_run;
        // Coalesced: workers only store the latest report; at most one dispatch to the UI thread is pending at a time,
        // so a burst of reports can never queue thousands of callbacks behind user input.
        ActivityProgress? latest = null;
        var gate = new Lock();
        var pending = false;
        var progress = new InlineProgress<ActivityProgress>(p =>
        {
            lock (gate)
            {
                latest = p;
                if (pending) return;
                pending = true;
            }

            ui.Post(() =>
            {
                ActivityProgress? value;
                lock (gate)
                {
                    value = latest;
                    pending = false;
                }

                // A report can be delivered after the run finished; it must not overwrite the outcome (UI run #10).
                if (value is not { } v || run != _run || !IsBusy) return;
                Message = v.Message;
                IsIndeterminate = v.Fraction is null;
                Percent = (v.Fraction ?? 0) * 100;
            });
        });

        ActivityOutcome outcome;
        try
        {
            var token = _cts.Token;
            outcome = await Task.Run(() => work(progress, token), token);
        }
        catch (OperationCanceledException)
        {
            outcome = new ActivityOutcome(Loc.Get("Activity_Cancelled"), InfoBarSeverity.Warning);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or ArgumentException or System.Data.Common.DbException)
        {
            logger.LogWarning(ex, "Activity {Title} failed", title);
            outcome = new ActivityOutcome(Loc.Format("Activity_Failed", ex.Message), InfoBarSeverity.Error);
        }
        finally
        {
            _cts.Dispose();
            _cts = null;
        }

        ui.Post(() =>
        {
            IsBusy = false;
            IsIndeterminate = false;
            Percent = 100;
            Message = outcome.Message;
            Severity = outcome.Severity;
            _action = outcome.Action;
            ActionText = outcome.ActionText;
            HasAction = outcome.Action is not null;
            Completed?.Invoke(this, EventArgs.Empty);
        });
        return true;
    }

    [RelayCommand]
    private void Cancel() => _cts?.Cancel();

    [RelayCommand]
    private void Dismiss()
    {
        if (!IsBusy) IsOpen = false;
    }

    [RelayCommand]
    private void InvokeAction()
    {
        IsOpen = false;
        _action?.Invoke();
    }
}
