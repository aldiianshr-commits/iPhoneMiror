namespace iPhoneMiror.DeviceDiscovery.Mdns;

/// <summary>DNS-SD service types advertised by iOS devices on the local network.</summary>
public static class AppleServiceNames
{
    /// <summary>Advertised by iPhones with "Show this iPhone when on Wi-Fi" (Wi-Fi sync) enabled. Instance: "&lt;wifi-mac&gt;@&lt;ipv6&gt;".</summary>
    public const string MobileDeviceWifiSync = "_apple-mobdev2._tcp";

    /// <summary>Advertised by iOS 17+ devices for remote pairing (used by Xcode/CoreDevice).</summary>
    public const string RemotePairing = "_remotepairing._tcp";

    public static readonly IReadOnlyList<string> All = [MobileDeviceWifiSync, RemotePairing];

    /// <summary>Extracts the Wi-Fi MAC address from an _apple-mobdev2 instance name, if present.</summary>
    public static string? TryGetWifiMac(string instanceName)
    {
        var at = instanceName.IndexOf('@');
        if (at <= 0) return null;
        var mac = instanceName[..at];
        return mac.Count(c => c == ':') == 5 ? mac.ToLowerInvariant() : null;
    }
}
