using Microsoft.Win32;

namespace iPhoneMiror.App.Services;

/// <summary>Per-user registry integration: launch at sign-in and the iphonemiror:// protocol (HKCU only, no admin rights).</summary>
public static class SystemIntegration
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string RunValue = "iPhoneMiror";

    public static string ExecutablePath => Environment.ProcessPath ?? Path.Combine(AppContext.BaseDirectory, "iPhoneMiror.exe");

    public static bool IsLaunchAtSignInEnabled()
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKey);
        return key?.GetValue(RunValue) is string;
    }

    public static void SetLaunchAtSignIn(bool enabled, bool minimized)
    {
        using var key = Registry.CurrentUser.CreateSubKey(RunKey);
        if (enabled) key.SetValue(RunValue, $"\"{ExecutablePath}\"" + (minimized ? " --minimized" : ""));
        else key.DeleteValue(RunValue, throwOnMissingValue: false);
    }

    /// <summary>Registers iphonemiror:// so the Sign in with Apple relay page can return to the app.</summary>
    public static void RegisterProtocol()
    {
        using var root = Registry.CurrentUser.CreateSubKey(@"Software\Classes\iphonemiror");
        root.SetValue("", "URL:iPhoneMiror");
        root.SetValue("URL Protocol", "");
        using (var icon = root.CreateSubKey("DefaultIcon")) icon.SetValue("", $"\"{ExecutablePath}\",0");
        using var cmd = root.CreateSubKey(@"shell\open\command");
        cmd.SetValue("", $"\"{ExecutablePath}\" \"%1\"");
    }
}
