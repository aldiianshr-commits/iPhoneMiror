using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using iPhoneMiror.App.Services;
using iPhoneMiror.Connection.Usbmux;
using iPhoneMiror.Core.Abstractions;
using iPhoneMiror.Core.Models;

namespace iPhoneMiror.App.ViewModels;

public sealed record DiscoveredItem(string Id, string Name, string Source, string Detail, bool CanPair);

public sealed partial class ConnectViewModel : ObservableObject
{
    private readonly IDeviceConnectionService _connections;
    private readonly IDeviceDiscoveryService _discovery;
    private readonly IUsbmuxClient _usbmux;
    private readonly UiDispatcher _ui;

    public ConnectViewModel(DeviceHub hub, IDeviceConnectionService connections, IDeviceDiscoveryService discovery, IUsbmuxClient usbmux, UiDispatcher ui)
    {
        Hub = hub;
        _connections = connections;
        _discovery = discovery;
        _usbmux = usbmux;
        _ui = ui;
        hub.DevicesChanged += (_, _) => RefreshLists();
        _discovery.DeviceDiscovered += (_, d) => _ui.Post(() => AddDiscovered(d));
        RefreshLists();
    }

    public DeviceHub Hub { get; }

    public ObservableCollection<DeviceItemViewModel> UsbDevices { get; } = [];
    public ObservableCollection<DeviceItemViewModel> WifiDevices { get; } = [];
    public ObservableCollection<DiscoveredItem> NetworkDiscovered { get; } = [];
    public ObservableCollection<DiscoveredItem> BluetoothDiscovered { get; } = [];

    [ObservableProperty]
    private int _selectedMethod;
    [ObservableProperty]
    private string _usbStatus = "Checking Apple Mobile Device Service…";
    [ObservableProperty]
    private string _wifiStatus = "Wi-Fi discovery is off.";
    [ObservableProperty]
    private string _bluetoothStatus = "Bluetooth status not checked.";
    [ObservableProperty]
    private string _pairingStatus = "";
    [ObservableProperty]
    private bool _isWifiDiscoveryRunning;
    [ObservableProperty]
    private bool _isBluetoothScanning;
    [ObservableProperty]
    private bool _hasUsbDevices;
    [ObservableProperty]
    private bool _hasWifiDevices;

    public bool IsUsbSelected => SelectedMethod == 0;
    public bool IsWifiSelected => SelectedMethod == 1;
    public bool IsBluetoothSelected => SelectedMethod == 2;

    partial void OnSelectedMethodChanged(int value)
    {
        OnPropertyChanged(nameof(IsUsbSelected));
        OnPropertyChanged(nameof(IsWifiSelected));
        OnPropertyChanged(nameof(IsBluetoothSelected));
    }

    private void RefreshLists()
    {
        Sync(UsbDevices, Hub.Devices.Where(d => d.Info.ConnectionType == ConnectionType.Usb || (d.Info.ConnectionType != ConnectionType.Network && !d.IsAttached)));
        Sync(WifiDevices, Hub.Devices.Where(d => d.Info.ConnectionType == ConnectionType.Network));
        HasUsbDevices = UsbDevices.Count > 0;
        HasWifiDevices = WifiDevices.Count > 0;
        var pending = Hub.Devices.FirstOrDefault(d => d.Info.Pairing is PairingState.DialogPending or PairingState.DeviceLocked or PairingState.UserDenied);
        PairingStatus = pending is null
            ? (Hub.Devices.Any(d => d.Info.Pairing == PairingState.Paired) ? "All attached iPhones trust this computer." : "")
            : $"{pending.Name}: {pending.PairingText}";
        UsbStatus = Hub.ServiceAvailable
            ? (HasUsbDevices ? $"{UsbDevices.Count} iPhone(s) on USB." : "Connect your iPhone with a USB cable and unlock it.")
            : Hub.ServiceMessage;
    }

    private static void Sync(ObservableCollection<DeviceItemViewModel> target, IEnumerable<DeviceItemViewModel> source)
    {
        var list = source.ToList();
        foreach (var gone in target.Where(t => !list.Contains(t)).ToList()) target.Remove(gone);
        foreach (var item in list.Where(i => !target.Contains(i))) target.Add(item);
    }

    [RelayCommand]
    private async Task CheckServiceAsync()
    {
        UsbStatus = "Checking Apple Mobile Device Service…";
        try
        {
            var devices = await _usbmux.ListDevicesAsync();
            UsbStatus = string.Create(CultureInfo.CurrentCulture,
                $"Apple Mobile Device Service is running · {devices.Count(d => d.ConnectionType == ConnectionType.Usb)} USB, {devices.Count(d => d.ConnectionType == ConnectionType.Network)} Wi-Fi attachment(s).");
        }
        catch (UsbmuxException ex)
        {
            UsbStatus = ex.Message;
        }
    }

    [RelayCommand]
    private async Task PairAsync(DeviceItemViewModel? device)
    {
        if (device is null) return;
        PairingStatus = $"Requesting trust from {device.Name}… Unlock the iPhone and tap “Trust”.";
        try
        {
            var state = await _connections.PairAsync(device.Udid);
            PairingStatus = state switch
            {
                PairingState.Paired => $"{device.Name} now trusts this computer.",
                PairingState.UserDenied => $"Trust was declined on {device.Name}. Unplug and reconnect to be asked again.",
                _ => $"Pairing did not complete ({state}).",
            };
        }
        catch (Exception ex)
        {
            PairingStatus = ex.Message;
        }
    }

    [RelayCommand]
    private async Task ToggleWifiDiscoveryAsync()
    {
        try
        {
            if (_discovery.IsNetworkDiscoveryRunning)
            {
                await _discovery.StopNetworkDiscoveryAsync();
                WifiStatus = "Wi-Fi discovery stopped.";
            }
            else
            {
                NetworkDiscovered.Clear();
                await _discovery.StartNetworkDiscoveryAsync();
                WifiStatus = "Searching the local network (mDNS) for iPhones with Wi-Fi sync enabled…";
            }
        }
        catch (Exception ex)
        {
            WifiStatus = ex.Message;
        }
        IsWifiDiscoveryRunning = _discovery.IsNetworkDiscoveryRunning;
    }

    [RelayCommand]
    private async Task RefreshBluetoothStatusAsync()
    {
        try
        {
            var s = await _discovery.GetBluetoothStatusAsync();
            BluetoothStatus = s.Message;
        }
        catch (Exception ex)
        {
            BluetoothStatus = ex.Message;
        }
    }

    [RelayCommand]
    private async Task ToggleBluetoothScanAsync()
    {
        try
        {
            if (_discovery.IsBluetoothScanRunning)
            {
                await _discovery.StopBluetoothScanAsync();
                BluetoothStatus = "Bluetooth scan stopped.";
            }
            else
            {
                BluetoothDiscovered.Clear();
                await _discovery.StartBluetoothScanAsync();
                BluetoothStatus = "Scanning for Apple Bluetooth devices…";
            }
        }
        catch (Exception ex)
        {
            BluetoothStatus = ex.Message;
        }
        IsBluetoothScanning = _discovery.IsBluetoothScanRunning;
    }

    [RelayCommand]
    private async Task PairBluetoothAsync(DiscoveredItem? item)
    {
        if (item is null) return;
        try { BluetoothStatus = await _discovery.PairBluetoothAsync(item.Id); }
        catch (Exception ex) { BluetoothStatus = ex.Message; }
    }

    private void AddDiscovered(DiscoveredDevice d)
    {
        var detail = d.Source switch
        {
            DiscoverySource.Mdns => $"{d.ServiceType?.Replace(".local", "")} · {d.Address ?? "resolving…"}",
            DiscoverySource.BluetoothLe => $"Bluetooth LE · {d.Address} · {d.Rssi} dBm",
            _ => d.IsPaired ? "Bluetooth · paired" : $"Bluetooth{(d.Rssi is int r ? $" · {r} dBm" : "")}",
        };
        var item = new DiscoveredItem(d.Id, d.DisplayName, d.Source.ToString(), detail, d.Source == DiscoverySource.BluetoothClassic && !d.IsPaired);
        var target = d.Source == DiscoverySource.Mdns ? NetworkDiscovered : BluetoothDiscovered;
        var existing = target.FirstOrDefault(x => x.Id == d.Id);
        if (existing is not null) target[target.IndexOf(existing)] = item;
        else if (target.Count < 100) target.Add(item);
    }
}
