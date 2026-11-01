using iPhoneMiror.Core.Account;

namespace iPhoneMiror.App.Services;

public sealed class ShellBrowserLauncher : IBrowserLauncher
{
    public async Task<bool> OpenAsync(Uri uri) => await Windows.System.Launcher.LaunchUriAsync(uri);
}
