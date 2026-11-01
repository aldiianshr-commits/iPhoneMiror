using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.Windows.AppLifecycle;

namespace iPhoneMiror.App;

/// <summary>
/// Custom entry point: keeps a single running instance and forwards "iphonemiror://" protocol activations
/// (the Sign in with Apple callback) to it.
/// </summary>
public static class Program
{
    public const string InstanceKey = "iPhoneMiror.Main";

    [STAThread]
    public static int Main(string[] args)
    {
        WinRT.ComWrappersSupport.InitializeComWrappers();

        var screenshotMode = args.Contains("--capture-screenshots");
        if (!screenshotMode && RedirectToExistingInstance())
        {
            return 0;
        }

        Application.Start(_ =>
        {
            var context = new DispatcherQueueSynchronizationContext(DispatcherQueue.GetForCurrentThread());
            SynchronizationContext.SetSynchronizationContext(context);
            _ = new App(args);
        });
        return 0;
    }

    private static bool RedirectToExistingInstance()
    {
        var activation = AppInstance.GetCurrent().GetActivatedEventArgs();
        var main = AppInstance.FindOrRegisterForKey(InstanceKey);
        if (main.IsCurrent)
        {
            main.Activated += (_, e) => App.Current?.OnRedirectedActivation(e);
            return false;
        }

        // Redirect must not block the STA thread's message pump: run it on a worker thread.
        using var done = new ManualResetEventSlim(false);
        Task.Run(async () =>
        {
            try { await main.RedirectActivationToAsync(activation); }
            finally { done.Set(); }
        });
        done.Wait(TimeSpan.FromSeconds(10));
        return true;
    }
}
