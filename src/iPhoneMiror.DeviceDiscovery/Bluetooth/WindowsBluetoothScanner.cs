using System.Collections.Concurrent;
using System.Globalization;
using iPhoneMiror.Core.Abstractions;
using Microsoft.Extensions.Logging;
using Windows.Devices.Bluetooth;
using Windows.Devices.Bluetooth.Advertisement;
using Windows.Devices.Enumeration;
using Windows.Devices.Radios;

namespace iPhoneMiror.DeviceDiscovery.Bluetooth;

/// <summary>
/// Bluetooth discovery using documented Windows APIs:
/// BLE advertisements with Apple manufacturer data (company id 0x004C) and classic Bluetooth device enumeration.
/// Bluetooth is used for presence/pairing only; it cannot carry screen video.
/// </summary>
public sealed class WindowsBluetoothScanner : IBluetoothScanner
{
    private readonly ILogger<WindowsBluetoothScanner> _logger;
    private readonly ConcurrentDictionary<string, DateTimeOffset> _lastReported = new();
    private BluetoothLEAdvertisementWatcher? _watcher;
    private DeviceWatcher? _classicWatcher;

    public WindowsBluetoothScanner(ILogger<WindowsBluetoothScanner> logger)
    {
        _logger = logger;
    }

    public bool IsScanning => _watcher?.Status == BluetoothLEAdvertisementWatcherStatus.Started;

    public event EventHandler<DiscoveredDevice>? DeviceSeen;

    public async Task<BluetoothStatus> GetStatusAsync(CancellationToken ct)
    {
        var adapter = await BluetoothAdapter.GetDefaultAsync();
        if (adapter is null) return new BluetoothStatus(false, false, false, "No Bluetooth adapter found on this PC.");
        var radio = await adapter.GetRadioAsync();
        var on = radio?.State == RadioState.On;
        return new BluetoothStatus(true, on, adapter.IsLowEnergySupported,
            on ? (adapter.IsLowEnergySupported ? "Bluetooth is on (Bluetooth LE supported)." : "Bluetooth is on (no Bluetooth LE support).")
               : "Bluetooth is turned off. Turn it on in Windows Settings > Bluetooth & devices.");
    }

    public async Task StartAsync(CancellationToken ct)
    {
        var status = await GetStatusAsync(ct).ConfigureAwait(false);
        if (!status.AdapterPresent || !status.IsEnabled) throw new InvalidOperationException(status.Message);

        if (status.SupportsLowEnergy && _watcher is null)
        {
            _watcher = new BluetoothLEAdvertisementWatcher { ScanningMode = BluetoothLEScanningMode.Active };
            _watcher.Received += OnAdvertisement;
            _watcher.Stopped += (_, e) => _logger.LogInformation("BLE watcher stopped: {Error}", e.Error);
            _watcher.Start();
        }

        if (_classicWatcher is null)
        {
            var selector = BluetoothDevice.GetDeviceSelectorFromPairingState(false);
            _classicWatcher = DeviceInformation.CreateWatcher(selector, ["System.Devices.Aep.IsPaired", "System.Devices.Aep.SignalStrength"], DeviceInformationKind.AssociationEndpoint);
            _classicWatcher.Added += (_, info) => ReportClassic(info);
            _classicWatcher.Updated += (_, _) => { };
            _classicWatcher.Start();
            foreach (var paired in await DeviceInformation.FindAllAsync(BluetoothDevice.GetDeviceSelectorFromPairingState(true)))
            {
                ReportClassic(paired, isPaired: true);
            }
        }
    }

    private void ReportClassic(DeviceInformation info, bool isPaired = false)
    {
        if (string.IsNullOrWhiteSpace(info.Name)) return;
        int? rssi = info.Properties.TryGetValue("System.Devices.Aep.SignalStrength", out var s) && s is int i ? i : null;
        DeviceSeen?.Invoke(this, new DiscoveredDevice(info.Id, info.Name, DiscoverySource.BluetoothClassic, null, rssi, null,
            isPaired || info.Pairing.IsPaired, DateTimeOffset.UtcNow));
    }

    private void OnAdvertisement(BluetoothLEAdvertisementWatcher sender, BluetoothLEAdvertisementReceivedEventArgs args)
    {
        var apple = args.Advertisement.GetManufacturerDataByCompanyId(AppleBluetooth.CompanyId);
        if (apple.Count == 0) return;
        var address = args.BluetoothAddress.ToString("X12", CultureInfo.InvariantCulture);
        var id = "ble:" + address;
        var now = DateTimeOffset.UtcNow;
        // Throttle: one report per address every 2 seconds.
        if (_lastReported.TryGetValue(id, out var last) && now - last < TimeSpan.FromSeconds(2)) return;
        _lastReported[id] = now;

        var name = string.IsNullOrWhiteSpace(args.Advertisement.LocalName) ? "Apple device (Bluetooth LE)" : args.Advertisement.LocalName;
        var formatted = string.Join(':', Enumerable.Range(0, 6).Select(k => address.Substring(k * 2, 2)));
        DeviceSeen?.Invoke(this, new DiscoveredDevice(id, name, DiscoverySource.BluetoothLe, formatted, args.RawSignalStrengthInDBm, null, false, now));
    }

    public Task StopAsync()
    {
        if (_watcher is not null)
        {
            _watcher.Received -= OnAdvertisement;
            _watcher.Stop();
            _watcher = null;
        }
        if (_classicWatcher is not null)
        {
            if (_classicWatcher.Status is DeviceWatcherStatus.Started or DeviceWatcherStatus.EnumerationCompleted) _classicWatcher.Stop();
            _classicWatcher = null;
        }
        return Task.CompletedTask;
    }

    public async Task<string> PairAsync(string deviceId, CancellationToken ct)
    {
        if (deviceId.StartsWith("ble:", StringComparison.Ordinal))
            return "Bluetooth LE advertisements from iPhones use rotating private addresses and cannot be paired directly. Make the iPhone discoverable (Settings > Bluetooth) and pair it from the classic Bluetooth list.";
        var info = await DeviceInformation.CreateFromIdAsync(deviceId);
        if (info.Pairing.IsPaired) return $"{info.Name} is already paired with this PC.";
        if (!info.Pairing.CanPair) return $"{info.Name} cannot be paired right now. Open Settings > Bluetooth on the iPhone to make it discoverable.";
        var result = await info.Pairing.PairAsync(DevicePairingProtectionLevel.Default);
        return result.Status switch
        {
            DevicePairingResultStatus.Paired => $"{info.Name} paired successfully.",
            DevicePairingResultStatus.AlreadyPaired => $"{info.Name} is already paired.",
            _ => $"Pairing {info.Name} failed: {result.Status}.",
        };
    }

    public async ValueTask DisposeAsync() => await StopAsync().ConfigureAwait(false);
}
