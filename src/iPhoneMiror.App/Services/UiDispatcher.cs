using Microsoft.UI.Dispatching;

namespace iPhoneMiror.App.Services;

/// <summary>Marshals work to the UI thread.</summary>
public sealed class UiDispatcher
{
    private DispatcherQueue? _queue;

    public void Initialize(DispatcherQueue queue) => _queue = queue;

    public bool HasThreadAccess => _queue?.HasThreadAccess ?? false;

    public void Post(Action action)
    {
        if (_queue is null || _queue.HasThreadAccess) { action(); return; }
        _queue.TryEnqueue(() => action());
    }

    public Task InvokeAsync(Func<Task> action)
    {
        if (_queue is null || _queue.HasThreadAccess) return action();
        var tcs = new TaskCompletionSource();
        _queue.TryEnqueue(async () =>
        {
            try { await action(); tcs.SetResult(); }
            catch (Exception ex) { tcs.SetException(ex); }
        });
        return tcs.Task;
    }
}
