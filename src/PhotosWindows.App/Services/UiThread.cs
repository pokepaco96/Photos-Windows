using Microsoft.UI.Dispatching;

namespace PhotosWindows.App.Services;

/// <summary>Marshals work to the UI thread. Services raise events on worker threads; view models use this to update bindings.</summary>
public sealed class UiThread(DispatcherQueue dispatcher)
{
    public bool HasAccess => dispatcher.HasThreadAccess;

    public void Post(Action action)
    {
        if (dispatcher.HasThreadAccess) action();
        else dispatcher.TryEnqueue(() => action());
    }

    public Task RunAsync(Func<Task> action)
    {
        if (dispatcher.HasThreadAccess) return action();
        var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var queued = dispatcher.TryEnqueue(async () =>
        {
            try
            {
                await action();
                tcs.SetResult();
            }
            catch (Exception ex)
            {
                tcs.SetException(ex);
            }
        });
        if (!queued) tcs.SetCanceled();
        return tcs.Task;
    }
}
