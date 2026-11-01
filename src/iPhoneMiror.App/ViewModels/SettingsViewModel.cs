using System.Collections.ObjectModel;
using System.Diagnostics;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using iPhoneMiror.App.Services;
using iPhoneMiror.Core.Abstractions;
using iPhoneMiror.Core.Logging;
using iPhoneMiror.Core.Settings;
using iPhoneMiror.Core.Util;
using Microsoft.Extensions.Logging;

namespace iPhoneMiror.App.ViewModels;

public sealed partial class SettingsViewModel : ObservableObject
{
    private readonly ISettingsStore _store;
    private readonly AppPaths _paths;
    private readonly FileLoggerProvider _logs;
    private readonly IDeviceRepository _repository;
    private readonly UiDispatcher _ui;
    private readonly WindowService _windows;
    private bool _loading;

    public SettingsViewModel(ISettingsStore store, AppPaths paths, FileLoggerProvider logs, IDeviceRepository repository, UiDispatcher ui, WindowService windows)
    {
        _store = store;
        _paths = paths;
        _logs = logs;
        _repository = repository;
        _ui = ui;
        _windows = windows;
        Load();
        foreach (var l in _logs.RecentLines.TakeLast(200)) LogLines.Add(l);
        _logs.LineWritten += (_, line) => _ui.Post(() =>
        {
            LogLines.Add(line);
            while (LogLines.Count > 200) LogLines.RemoveAt(0);
        });
    }

    public ObservableCollection<string> LogLines { get; } = [];

    public string Version => typeof(SettingsViewModel).Assembly.GetName().Version?.ToString(3) ?? "0.1.0";
    public string DataFolder => _paths.Root;
    public string RuntimeInfo => $"{System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription} · {System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture} · {Environment.OSVersion.VersionString}";

    // General
    [ObservableProperty]
    private bool _confirmBeforeClosing;
    [ObservableProperty]
    private string _screenshotFolder = "";
    // Appearance
    [ObservableProperty]
    private int _themeIndex;
    [ObservableProperty]
    private bool _useMica;
    [ObservableProperty]
    private bool _reduceAnimations;
    // Connection
    [ObservableProperty]
    private bool _autoReconnect;
    [ObservableProperty]
    private bool _autoPair;
    [ObservableProperty]
    private bool _enableWifiDevices;
    [ObservableProperty]
    private string _usbmuxEndpoint = "";
    [ObservableProperty]
    private double _wdaPort;
    [ObservableProperty]
    private double _mjpegPort;
    [ObservableProperty]
    private string _uxPlayPath = "";
    [ObservableProperty]
    private string _airPlayName = "";
    // Startup
    [ObservableProperty]
    private bool _launchAtSignIn;
    [ObservableProperty]
    private bool _startMinimized;
    [ObservableProperty]
    private bool _autoStartMirroring;
    // Performance
    [ObservableProperty]
    private double _frameRate;
    [ObservableProperty]
    private double _quality;
    [ObservableProperty]
    private double _scaling;
    // Logging
    [ObservableProperty]
    private int _logLevelIndex;
    [ObservableProperty]
    private double _retainDays;

    [ObservableProperty]
    private string _message = "";

    private void Load()
    {
        _loading = true;
        var s = _store.Current;
        ConfirmBeforeClosing = s.General.ConfirmBeforeClosingWhileMirroring;
        ScreenshotFolder = string.IsNullOrWhiteSpace(s.General.ScreenshotFolder) ? _paths.DefaultScreenshotDirectory : s.General.ScreenshotFolder;
        ThemeIndex = (int)s.Appearance.Theme;
        UseMica = s.Appearance.UseMica;
        ReduceAnimations = s.Appearance.ReduceAnimations;
        AutoReconnect = s.Connection.AutoReconnect;
        AutoPair = s.Connection.AutoPairOnConnect;
        EnableWifiDevices = s.Connection.EnableWifiDevices;
        UsbmuxEndpoint = $"{s.Connection.UsbmuxHost}:{s.Connection.UsbmuxPort}";
        WdaPort = s.Mirroring.WdaPort;
        MjpegPort = s.Mirroring.MjpegPort;
        UxPlayPath = s.Mirroring.UxPlayPath;
        AirPlayName = s.Mirroring.AirPlayReceiverName;
        LaunchAtSignIn = SafeIsLaunchAtSignIn();
        StartMinimized = s.Startup.StartMinimized;
        AutoStartMirroring = s.Startup.AutoStartMirroringForLastDevice;
        FrameRate = s.Performance.FrameRate;
        Quality = s.Performance.JpegQuality;
        Scaling = s.Performance.ScalingPercent;
        LogLevelIndex = (int)s.Logging.MinimumLevel;
        RetainDays = s.Logging.RetainDays;
        _loading = false;
    }

    private static bool SafeIsLaunchAtSignIn()
    {
        try { return SystemIntegration.IsLaunchAtSignInEnabled(); }
        catch (Exception) { return false; }
    }

    protected override void OnPropertyChanged(System.ComponentModel.PropertyChangedEventArgs e)
    {
        base.OnPropertyChanged(e);
        if (_loading || e.PropertyName is nameof(Message)) return;
        _ = SaveAsync(e.PropertyName);
    }

    private async Task SaveAsync(string? property)
    {
        try
        {
            if (property is nameof(LaunchAtSignIn) or nameof(StartMinimized))
                SystemIntegration.SetLaunchAtSignIn(LaunchAtSignIn, StartMinimized);

            await _store.UpdateAsync(s =>
            {
                s.General.ConfirmBeforeClosingWhileMirroring = ConfirmBeforeClosing;
                s.General.ScreenshotFolder = ScreenshotFolder;
                s.Appearance.Theme = (AppTheme)Math.Clamp(ThemeIndex, 0, 2);
                s.Appearance.UseMica = UseMica;
                s.Appearance.ReduceAnimations = ReduceAnimations;
                s.Connection.AutoReconnect = AutoReconnect;
                s.Connection.AutoPairOnConnect = AutoPair;
                s.Connection.EnableWifiDevices = EnableWifiDevices;
                var parts = UsbmuxEndpoint.Split(':');
                if (parts.Length == 2 && int.TryParse(parts[1], out var port) && port is > 0 and < 65536)
                {
                    s.Connection.UsbmuxHost = parts[0];
                    s.Connection.UsbmuxPort = port;
                }
                s.Mirroring.WdaPort = (int)WdaPort;
                s.Mirroring.MjpegPort = (int)MjpegPort;
                s.Mirroring.UxPlayPath = UxPlayPath;
                s.Mirroring.AirPlayReceiverName = string.IsNullOrWhiteSpace(AirPlayName) ? "iPhoneMiror" : AirPlayName;
                s.Startup.StartMinimized = StartMinimized;
                s.Startup.LaunchAtSignIn = LaunchAtSignIn;
                s.Startup.AutoStartMirroringForLastDevice = AutoStartMirroring;
                s.Performance.FrameRate = (int)FrameRate;
                s.Performance.JpegQuality = (int)Quality;
                s.Performance.ScalingPercent = (int)Scaling;
                s.Logging.MinimumLevel = (AppLogLevel)Math.Clamp(LogLevelIndex, 0, 4);
                s.Logging.RetainDays = (int)RetainDays;
            });
            _logs.MinimumLevel = (LogLevel)Math.Clamp(LogLevelIndex, 0, 4);
            Message = property is nameof(UsbmuxEndpoint) or nameof(EnableWifiDevices) or nameof(AutoPair)
                ? "Saved. Connection changes take effect after restarting iPhoneMiror."
                : "Saved.";
        }
        catch (Exception ex)
        {
            Message = "Could not save settings: " + ex.Message;
        }
    }

    [RelayCommand]
    private void OpenLogFolder() => OpenFolder(_paths.LogDirectory);

    [RelayCommand]
    private void OpenDataFolder() => OpenFolder(_paths.Root);

    [RelayCommand]
    private void OpenScreenshotFolder()
    {
        Directory.CreateDirectory(ScreenshotFolder);
        OpenFolder(ScreenshotFolder);
    }

    private static void OpenFolder(string path)
    {
        Directory.CreateDirectory(path);
        Process.Start(new ProcessStartInfo("explorer.exe", $"\"{path}\"") { UseShellExecute = true });
    }

    [RelayCommand]
    private async Task BrowseUxPlayAsync()
    {
        var picker = new Windows.Storage.Pickers.FileOpenPicker();
        WinRT.Interop.InitializeWithWindow.Initialize(picker, _windows.MainWindowHandle);
        picker.FileTypeFilter.Add(".exe");
        var file = await picker.PickSingleFileAsync();
        if (file is not null) UxPlayPath = file.Path;
    }

    [RelayCommand]
    private async Task BrowseScreenshotFolderAsync()
    {
        var picker = new Windows.Storage.Pickers.FolderPicker();
        WinRT.Interop.InitializeWithWindow.Initialize(picker, _windows.MainWindowHandle);
        picker.FileTypeFilter.Add("*");
        var folder = await picker.PickSingleFolderAsync();
        if (folder is not null) ScreenshotFolder = folder.Path;
    }

    [RelayCommand]
    private async Task ClearRecentDevicesAsync()
    {
        foreach (var r in await _repository.GetRecentAsync(1000)) await _repository.RemoveAsync(r.Udid);
        Message = "Recent devices cleared.";
    }

    [RelayCommand]
    private void ResetDefaults()
    {
        var defaults = new AppSettings();
        _ = _store.UpdateAsync(s =>
        {
            s.General = defaults.General;
            s.Appearance = defaults.Appearance;
            s.Connection = defaults.Connection;
            s.Performance = defaults.Performance;
            s.Logging = defaults.Logging;
            s.Mirroring = defaults.Mirroring;
        });
        Load();
        Message = "Defaults restored.";
    }
}
