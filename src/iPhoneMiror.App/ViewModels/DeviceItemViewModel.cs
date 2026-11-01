using CommunityToolkit.Mvvm.ComponentModel;
using iPhoneMiror.Core.Compatibility;
using iPhoneMiror.Core.Models;

namespace iPhoneMiror.App.ViewModels;

/// <summary>UI projection of a <see cref="DeviceInfo"/>.</summary>
public sealed partial class DeviceItemViewModel : ObservableObject
{
    private readonly CompatibilityEvaluator _compat;

    public DeviceItemViewModel(DeviceInfo info, CompatibilityEvaluator compat)
    {
        _compat = compat;
        Info = info;
        Apply(info);
    }

    public DeviceInfo Info { get; private set; }
    public string Udid => Info.Udid;

    [ObservableProperty]
    private string _name = "";
    [ObservableProperty]
    private string _model = "";
    [ObservableProperty]
    private string _iosVersionText = "";
    [ObservableProperty]
    private string _connectionText = "";
    [ObservableProperty]
    private string _batteryText = "";
    [ObservableProperty]
    private string _statusText = "";
    [ObservableProperty]
    private string _pairingText = "";
    [ObservableProperty]
    private string _detailText = "";
    [ObservableProperty]
    private string _compatibilitySummary = "";
    [ObservableProperty]
    private bool _isConnected;
    [ObservableProperty]
    private bool _isAttached;
    [ObservableProperty]
    private bool _needsTrust;
    [ObservableProperty]
    private string _statusGlyph = "";
    [ObservableProperty]
    private IReadOnlyList<FeatureCompatibility> _features = [];

    public void Apply(DeviceInfo info)
    {
        Info = info;
        Name = info.DisplayName;
        Model = info.ModelName;
        IosVersionText = info.ProductVersion is null ? "iOS —" : $"iOS {info.ProductVersion}";
        ConnectionText = info.ConnectionType switch
        {
            ConnectionType.Usb => "USB",
            ConnectionType.Network => info.NetworkAddress is null ? "Wi-Fi" : $"Wi-Fi · {info.NetworkAddress}",
            ConnectionType.Bluetooth => "Bluetooth",
            _ => "—",
        };
        BatteryText = info.BatteryLevel is int b ? $"{b}%" + (info.BatteryCharging == true ? " · charging" : "") : "—";
        IsConnected = info.State == DeviceConnectionState.Connected;
        IsAttached = info.MuxDeviceId != 0;
        NeedsTrust = info.State == DeviceConnectionState.AwaitingTrust && info.Pairing is not PairingState.DialogPending;
        StatusText = info.State switch
        {
            DeviceConnectionState.Connected => "Connected",
            DeviceConnectionState.Connecting => "Connecting…",
            DeviceConnectionState.AwaitingTrust => "Waiting for trust",
            DeviceConnectionState.Reconnecting => "Reconnecting…",
            DeviceConnectionState.Disconnected => "Disconnected",
            DeviceConnectionState.Error => "Error",
            _ => info.State.ToString(),
        };
        PairingText = info.Pairing switch
        {
            PairingState.Paired => "Trusted",
            PairingState.DialogPending => "Tap “Trust” on the iPhone",
            PairingState.DeviceLocked => "Unlock the iPhone",
            PairingState.UserDenied => "Trust declined",
            PairingState.NotPaired => "Not trusted yet",
            PairingState.Failed => "Pairing failed",
            _ => "Unknown",
        };
        StatusGlyph = info.ConnectionType == ConnectionType.Network ? "" : "";
        DetailText = info.LastError ?? "";
        var report = _compat.Evaluate(info);
        CompatibilitySummary = report.Summary;
        Features = report.Features;
    }
}

public sealed record RecentDeviceItem(string Udid, string Name, string Model, string Version, string LastSeen, string ConnectionText);
