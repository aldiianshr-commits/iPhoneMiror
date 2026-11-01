using iPhoneMiror.Core.Abstractions;

namespace iPhoneMiror.DeviceDiscovery.Bluetooth;

public interface IBluetoothScanner : IAsyncDisposable
{
    bool IsScanning { get; }
    event EventHandler<DiscoveredDevice>? DeviceSeen;
    Task<BluetoothStatus> GetStatusAsync(CancellationToken ct);
    Task StartAsync(CancellationToken ct);
    Task StopAsync();
    Task<string> PairAsync(string deviceId, CancellationToken ct);
}

/// <summary>Used where Windows Bluetooth APIs are not available (non-Windows test runs).</summary>
public sealed class UnsupportedBluetoothScanner : IBluetoothScanner
{
    public bool IsScanning => false;
    public event EventHandler<DiscoveredDevice>? DeviceSeen { add { } remove { } }
    public Task<BluetoothStatus> GetStatusAsync(CancellationToken ct) =>
        Task.FromResult(new BluetoothStatus(false, false, false, "Bluetooth APIs are only available on Windows."));
    public Task StartAsync(CancellationToken ct) => throw new PlatformNotSupportedException("Bluetooth scanning requires Windows 10/11.");
    public Task StopAsync() => Task.CompletedTask;
    public Task<string> PairAsync(string deviceId, CancellationToken ct) => throw new PlatformNotSupportedException("Bluetooth pairing requires Windows 10/11.");
    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}

/// <summary>Apple's Bluetooth SIG company identifier, present in manufacturer data of Apple BLE advertisements.</summary>
public static class AppleBluetooth
{
    public const ushort CompanyId = 0x004C;
}
