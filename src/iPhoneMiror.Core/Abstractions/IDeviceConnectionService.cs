using iPhoneMiror.Core.Models;

namespace iPhoneMiror.Core.Abstractions;

public enum DeviceChangeKind { Added, Updated, Removed }

public sealed record DeviceChangedEventArgs(DeviceInfo Device, DeviceChangeKind Kind);

/// <summary>State of the Apple Mobile Device Service (usbmuxd) that brokers USB and Wi-Fi connections on Windows.</summary>
public sealed record MuxServiceStatus(bool IsAvailable, string Message, DateTimeOffset CheckedAt);

public interface IDeviceConnectionService : IAsyncDisposable
{
    IReadOnlyList<DeviceInfo> Devices { get; }
    MuxServiceStatus ServiceStatus { get; }

    event EventHandler<DeviceChangedEventArgs>? DeviceChanged;
    event EventHandler<MuxServiceStatus>? ServiceStatusChanged;

    /// <summary>Starts monitoring USB/Wi-Fi attach and detach events, with automatic reconnect.</summary>
    Task StartAsync(CancellationToken cancellationToken = default);
    Task StopAsync();

    /// <summary>Starts (or continues) pairing. Shows "Trust This Computer?" on the device.</summary>
    Task<PairingState> PairAsync(string udid, CancellationToken cancellationToken = default);

    /// <summary>Re-reads device information (name, iOS version, battery) from lockdownd.</summary>
    Task<DeviceInfo?> RefreshAsync(string udid, CancellationToken cancellationToken = default);

    /// <summary>Stops using the device until it is re-attached or <see cref="ReconnectAsync"/> is called.</summary>
    Task DisconnectAsync(string udid);

    Task ReconnectAsync(string udid, CancellationToken cancellationToken = default);

    DeviceInfo? Find(string udid);
}
