using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using iPhoneMiror.App.Services;
using iPhoneMiror.Core.Abstractions;
using iPhoneMiror.Core.Models;
using iPhoneMiror.Core.Settings;
using Microsoft.Extensions.Logging;
using Microsoft.UI.Dispatching;

namespace iPhoneMiror.App.ViewModels;

public enum IndicatorKind { Idle, Good, Warn, Bad }

/// <summary>Owns the mirroring session (start/stop, quality, input) shared by the Mirroring page and the Display window.</summary>
public sealed partial class MirroringViewModel : ObservableObject
{
    private readonly IMirroringService _mirroring;
    private readonly IInputForwarder _input;
    private readonly ISettingsStore _settings;
    private readonly IDeviceConnectionService _devices;
    private readonly WindowService _windows;
    private readonly UiDispatcher _ui;
    private readonly ILogger<MirroringViewModel> _logger;
    private DispatcherQueueTimer? _statsTimer;
    private bool _initializing;

    public MirroringViewModel(IMirroringService mirroring, IInputForwarder input, ISettingsStore settings, IDeviceConnectionService devices,
        DeviceHub hub, WindowService windows, UiDispatcher ui, ILogger<MirroringViewModel> logger)
    {
        _mirroring = mirroring;
        _input = input;
        _settings = settings;
        _devices = devices;
        _windows = windows;
        _ui = ui;
        _logger = logger;
        Hub = hub;

        var perf = settings.Current.Performance;
        _initializing = true;
        FrameRate = perf.FrameRate;
        Quality = perf.JpegQuality;
        ScalingPercent = perf.ScalingPercent;
        BackendIndex = settings.Current.Mirroring.Backend == MirroringBackendKind.AirPlayUxPlay ? 1 : 0;
        IsInputEnabled = settings.Current.Mirroring.EnableInputForwarding;
        _initializing = false;

        _mirroring.StateChanged += (_, e) => _ui.Post(() => ApplyState(e.State, e.Message));
        _input.AvailabilityChanged += (_, a) => _ui.Post(() => ApplyInput(a));
        hub.DevicesChanged += (_, _) =>
        {
            if (SelectedDevice is null || !Hub.Devices.Contains(SelectedDevice)) SelectedDevice = Hub.FirstConnected;
            StartCommand.NotifyCanExecuteChanged();
        };
        SelectedDevice = hub.FirstConnected;
        ApplyState(_mirroring.State, null);
        ApplyInput(_input.Availability);
    }

    public DeviceHub Hub { get; }
    public IMirroringService Session => _mirroring;
    public IInputForwarder Input => _input;

    [ObservableProperty, NotifyCanExecuteChangedFor(nameof(StartCommand))]
    private DeviceItemViewModel? _selectedDevice;

    [ObservableProperty]
    private int _backendIndex;
    [ObservableProperty]
    private double _frameRate;
    [ObservableProperty]
    private double _quality;
    [ObservableProperty]
    private double _scalingPercent;
    [ObservableProperty]
    private bool _isInputEnabled;

    [ObservableProperty]
    private string _stateText = "Idle";
    [ObservableProperty]
    private string _stateDetail = "Choose an iPhone and start mirroring.";
    [ObservableProperty]
    private IndicatorKind _indicator;
    [ObservableProperty, NotifyCanExecuteChangedFor(nameof(StartCommand), nameof(StopCommand))]
    private bool _isRunning;
    [ObservableProperty]
    private bool _isStreaming;
    [ObservableProperty]
    private string _statsText = "—";
    [ObservableProperty]
    private string _inputStatusText = "Input not connected";
    [ObservableProperty]
    private bool _inputAvailable;

    public bool IsAirPlay => BackendIndex == 1;

    partial void OnBackendIndexChanged(int value)
    {
        OnPropertyChanged(nameof(IsAirPlay));
        if (_initializing) return;
        _ = _settings.UpdateAsync(s => s.Mirroring.Backend = value == 1 ? MirroringBackendKind.AirPlayUxPlay : MirroringBackendKind.WebDriverAgent);
        StartCommand.NotifyCanExecuteChanged();
    }

    partial void OnFrameRateChanged(double value) => SavePerformance();
    partial void OnQualityChanged(double value) => SavePerformance();
    partial void OnScalingPercentChanged(double value) => SavePerformance();
    partial void OnIsInputEnabledChanged(bool value)
    {
        if (!_initializing) _ = _settings.UpdateAsync(s => s.Mirroring.EnableInputForwarding = value);
    }

    private void SavePerformance()
    {
        if (_initializing) return;
        _ = _settings.UpdateAsync(s =>
    {
        s.Performance.FrameRate = (int)FrameRate;
        s.Performance.JpegQuality = (int)Quality;
        s.Performance.ScalingPercent = (int)ScalingPercent;
    });
    }

    private bool CanStart() => !IsRunning && (IsAirPlay || SelectedDevice is { IsAttached: true });

    [RelayCommand(CanExecute = nameof(CanStart))]
    private async Task StartAsync()
    {
        var cfg = _settings.Current.Mirroring;
        var options = new MirroringOptions
        {
            Backend = IsAirPlay ? MirroringBackendKind.AirPlayUxPlay : MirroringBackendKind.WebDriverAgent,
            FrameRate = (int)FrameRate,
            Quality = (int)Quality,
            ScalingPercent = (int)ScalingPercent,
            WdaPort = cfg.WdaPort,
            MjpegPort = cfg.MjpegPort,
            AutoReconnect = _settings.Current.Connection.AutoReconnect,
        };
        var udid = IsAirPlay ? (SelectedDevice?.Udid ?? "airplay") : SelectedDevice!.Udid;
        try
        {
            IsRunning = true;
            await _mirroring.StartAsync(udid, options);
            await _settings.UpdateAsync(s => s.Startup.LastDeviceUdid = udid);
            StartStatsTimer();
            if (!IsAirPlay && IsInputEnabled && _devices.Find(udid) is { } device)
            {
                await ConnectInputAsync(device, cfg.WdaPort);
            }
            else if (IsAirPlay)
            {
                ApplyInput(new InputAvailability(false, "AirPlay mirroring does not support input control.", DateTimeOffset.UtcNow));
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Starting mirroring failed.");
            ApplyState(MirroringState.Error, ex.Message);
        }
    }

    private async Task ConnectInputAsync(DeviceInfo device, int port)
    {
        InputStatusText = "Connecting to WebDriverAgent…";
        var a = await _input.ConnectAsync(device, port);
        ApplyInput(a);
    }

    [RelayCommand(CanExecute = nameof(IsRunning))]
    private async Task StopAsync()
    {
        await _mirroring.StopAsync();
        await _input.DisconnectAsync();
        _statsTimer?.Stop();
        StatsText = "—";
    }

    [RelayCommand]
    private async Task CheckInputAsync()
    {
        if (SelectedDevice is null || _devices.Find(SelectedDevice.Udid) is not { } device)
        {
            ApplyInput(new InputAvailability(false, "Connect an iPhone first.", DateTimeOffset.UtcNow));
            return;
        }
        await ConnectInputAsync(device, _settings.Current.Mirroring.WdaPort);
    }

    [RelayCommand] private void OpenWindow() => _windows.ShowDisplayWindow(fullscreen: false);
    [RelayCommand] private void OpenFullscreen() => _windows.ShowDisplayWindow(fullscreen: true);

    private void StartStatsTimer()
    {
        if (_statsTimer is null)
        {
            _statsTimer = DispatcherQueue.GetForCurrentThread().CreateTimer();
            _statsTimer.Interval = TimeSpan.FromSeconds(1);
            _statsTimer.Tick += (_, _) => UpdateStats();
        }
        _statsTimer.Start();
    }

    private void UpdateStats()
    {
        var s = _mirroring.Stats;
        StatsText = s.FramesReceived == 0
            ? (IsAirPlay ? "AirPlay video is rendered by UxPlay" : "Waiting for frames…")
            : string.Create(CultureInfo.CurrentCulture, $"{s.FramesPerSecond:0.0} fps · {s.Width}×{s.Height} px · {s.BytesReceived / 1024.0 / 1024.0:0.0} MB");
    }

    private void ApplyState(MirroringState state, string? message)
    {
        StateText = state switch
        {
            MirroringState.Idle => "Idle",
            MirroringState.Starting => "Starting",
            MirroringState.Streaming => "Streaming",
            MirroringState.Reconnecting => "Reconnecting",
            MirroringState.Stopped => "Stopped",
            MirroringState.Error => "Error",
            _ => state.ToString(),
        };
        if (message is not null) StateDetail = message;
        Indicator = state switch
        {
            MirroringState.Streaming => IndicatorKind.Good,
            MirroringState.Starting or MirroringState.Reconnecting => IndicatorKind.Warn,
            MirroringState.Error => IndicatorKind.Bad,
            _ => IndicatorKind.Idle,
        };
        IsRunning = state is MirroringState.Starting or MirroringState.Streaming or MirroringState.Reconnecting;
        IsStreaming = state == MirroringState.Streaming;
        if (!IsRunning) _statsTimer?.Stop();
    }

    private void ApplyInput(InputAvailability a)
    {
        InputAvailable = a.IsAvailable;
        InputStatusText = a.CheckedAt == DateTimeOffset.MinValue ? "Input not connected" : a.Message;
    }
}
