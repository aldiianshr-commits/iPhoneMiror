namespace iPhoneMiror.Core.Abstractions;

public enum DiscoverySource { Mdns, BluetoothLe, BluetoothClassic }

/// <summary>A device seen by passive discovery (not necessarily connectable).</summary>
public sealed record DiscoveredDevice(
    string Id,
    string DisplayName,
    DiscoverySource Source,
    string? Address,
    int? Rssi,
    string? ServiceType,
    bool IsPaired,
    DateTimeOffset LastSeen);

public sealed record BluetoothStatus(bool AdapterPresent, bool IsEnabled, bool SupportsLowEnergy, string Message);

public interface IDeviceDiscoveryService : IAsyncDisposable
{
    IReadOnlyList<DiscoveredDevice> Discovered { get; }
    event EventHandler<DiscoveredDevice>? DeviceDiscovered;

    bool IsNetworkDiscoveryRunning { get; }
    bool IsBluetoothScanRunning { get; }

    Task StartNetworkDiscoveryAsync(CancellationToken cancellationToken = default);
    Task StopNetworkDiscoveryAsync();

    Task<BluetoothStatus> GetBluetoothStatusAsync(CancellationToken cancellationToken = default);
    Task StartBluetoothScanAsync(CancellationToken cancellationToken = default);
    Task StopBluetoothScanAsync();

    /// <summary>Requests Windows Bluetooth pairing for a classic Bluetooth device. Returns a status message.</summary>
    Task<string> PairBluetoothAsync(string deviceId, CancellationToken cancellationToken = default);
}
