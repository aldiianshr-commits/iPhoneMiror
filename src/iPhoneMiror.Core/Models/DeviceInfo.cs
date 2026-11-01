namespace iPhoneMiror.Core.Models;

/// <summary>Snapshot of what is known about one iPhone. Immutable; services publish new instances on change.</summary>
public sealed record DeviceInfo
{
    /// <summary>Unique device identifier (UDID / serial reported by usbmux).</summary>
    public required string Udid { get; init; }

    /// <summary>usbmux device id for the current attachment (changes on every re-plug), or 0.</summary>
    public int MuxDeviceId { get; init; }

    public string? Name { get; init; }
    /// <summary>Apple product identifier such as "iPhone17,1".</summary>
    public string? ProductType { get; init; }
    public string? ProductVersion { get; init; }
    public string? BuildVersion { get; init; }
    public string? DeviceClass { get; init; }
    public string? WifiAddress { get; init; }
    public string? NetworkAddress { get; init; }
    public int? BatteryLevel { get; init; }
    public bool? BatteryCharging { get; init; }

    public ConnectionType ConnectionType { get; init; }
    public DeviceConnectionState State { get; init; }
    public PairingState Pairing { get; init; }
    public string? LastError { get; init; }
    public DateTimeOffset LastSeen { get; init; } = DateTimeOffset.UtcNow;

    public string DisplayName => string.IsNullOrWhiteSpace(Name) ? ModelName : Name!;

    public string ModelName => DeviceModelCatalog.GetMarketingName(ProductType);

    public IosVersion? IosVersion => Models.IosVersion.TryParse(ProductVersion, out var v) ? v : null;
}
