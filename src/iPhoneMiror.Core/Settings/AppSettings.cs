using iPhoneMiror.Core.Models;

namespace iPhoneMiror.Core.Settings;

public sealed class AppSettings
{
    public GeneralSettings General { get; set; } = new();
    public AppearanceSettings Appearance { get; set; } = new();
    public ConnectionSettings Connection { get; set; } = new();
    public StartupSettings Startup { get; set; } = new();
    public PerformanceSettings Performance { get; set; } = new();
    public LoggingSettings Logging { get; set; } = new();
    public AppleAccountSettings AppleAccount { get; set; } = new();
    public MirroringSettings Mirroring { get; set; } = new();
}

public sealed class GeneralSettings
{
    public bool ConfirmBeforeClosingWhileMirroring { get; set; } = true;
    public string ScreenshotFolder { get; set; } = "";
}

public enum AppTheme { Dark, Light, System }

public sealed class AppearanceSettings
{
    public AppTheme Theme { get; set; } = AppTheme.Dark;
    public bool UseMica { get; set; } = true;
    public bool ReduceAnimations { get; set; }
}

public sealed class ConnectionSettings
{
    public bool AutoReconnect { get; set; } = true;
    public int ReconnectInitialDelayMs { get; set; } = 1000;
    public int ReconnectMaxDelayMs { get; set; } = 30000;
    /// <summary>Apple Mobile Device Service (usbmuxd) endpoint. Default on Windows: 127.0.0.1:27015.</summary>
    public string UsbmuxHost { get; set; } = "127.0.0.1";
    public int UsbmuxPort { get; set; } = 27015;
    public bool EnableWifiDevices { get; set; } = true;
    public bool AutoPairOnConnect { get; set; } = true;
}

public sealed class StartupSettings
{
    public bool LaunchAtSignIn { get; set; }
    public bool StartMinimized { get; set; }
    public bool AutoStartMirroringForLastDevice { get; set; }
    public string? LastDeviceUdid { get; set; }
}

public sealed class PerformanceSettings
{
    public int FrameRate { get; set; } = 30;
    public int JpegQuality { get; set; } = 60;
    public int ScalingPercent { get; set; } = 75;
}

public enum AppLogLevel { Trace, Debug, Information, Warning, Error }

public sealed class LoggingSettings
{
    public AppLogLevel MinimumLevel { get; set; } = AppLogLevel.Information;
    public int RetainDays { get; set; } = 7;
}

public sealed class AppleAccountSettings
{
    /// <summary>Services ID registered in the Apple Developer portal (used as OAuth client_id).</summary>
    public string ServicesId { get; set; } = "";
    /// <summary>HTTPS redirect URI registered for the Services ID. It must host the relay page in tools/apple-signin-relay.</summary>
    public string RedirectUri { get; set; } = "";
}

public sealed class MirroringSettings
{
    public MirroringBackendKind Backend { get; set; } = MirroringBackendKind.WebDriverAgent;
    public int WdaPort { get; set; } = 8100;
    public int MjpegPort { get; set; } = 9100;
    /// <summary>Full path to uxplay.exe (AirPlay receiver, installed separately; GPL-3.0).</summary>
    public string UxPlayPath { get; set; } = "";
    public string AirPlayReceiverName { get; set; } = "iPhoneMiror";
    public bool EnableInputForwarding { get; set; } = true;
}
