namespace iPhoneMiror.Core.Util;

/// <summary>Per-user data locations (%LOCALAPPDATA%\iPhoneMiror on Windows).</summary>
public sealed class AppPaths
{
    public AppPaths(string? root = null)
    {
        Root = root ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "iPhoneMiror");
    }

    public string Root { get; }
    public string SettingsFile => Path.Combine(Root, "settings.json");
    public string DatabaseFile => Path.Combine(Root, "iphonemiror.db");
    public string LogDirectory => Path.Combine(Root, "logs");
    public string PairRecordDirectory => Path.Combine(Root, "pairing");

    public string DefaultScreenshotDirectory =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyPictures), "iPhoneMiror");

    public void EnsureCreated()
    {
        Directory.CreateDirectory(Root);
        Directory.CreateDirectory(LogDirectory);
        Directory.CreateDirectory(PairRecordDirectory);
    }
}
