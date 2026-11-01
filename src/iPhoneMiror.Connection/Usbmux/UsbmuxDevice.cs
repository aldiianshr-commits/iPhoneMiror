using System.Net;
using iPhoneMiror.Core.Models;
using iPhoneMiror.Core.Plist;

namespace iPhoneMiror.Connection.Usbmux;

/// <summary>One attachment of a device as reported by usbmuxd ("Attached" event or ListDevices entry).</summary>
public sealed record UsbmuxDevice(
    int DeviceId,
    string Udid,
    ConnectionType ConnectionType,
    int? ProductId,
    long? LocationId,
    long? ConnectionSpeed,
    IPAddress? NetworkAddress,
    string? EscapedFullServiceName)
{
    public static UsbmuxDevice FromProperties(PlistDictionary props, long? fallbackDeviceId = null)
    {
        var id = props.GetInt64("DeviceID") ?? fallbackDeviceId ?? throw new UsbmuxException("Device record without DeviceID.");
        var serial = props.GetString("SerialNumber") ?? throw new UsbmuxException("Device record without SerialNumber.");
        var type = props.GetString("ConnectionType") switch
        {
            "USB" => ConnectionType.Usb,
            "Network" => ConnectionType.Network,
            _ => ConnectionType.Unknown,
        };
        return new UsbmuxDevice(
            (int)id,
            NormalizeUdid(serial),
            type,
            (int?)props.GetInt64("ProductID"),
            props.GetInt64("LocationID"),
            props.GetInt64("ConnectionSpeed"),
            ParseNetworkAddress(props.GetData("NetworkAddress")),
            props.GetString("EscapedFullServiceName"));
    }

    /// <summary>
    /// Older devices report a 40-hex-digit UDID; devices since iPhone XS report "XXXXXXXX-XXXXXXXXXXXXXXXX".
    /// usbmuxd on some systems omits the dash; it is restored so pair records can be found.
    /// </summary>
    public static string NormalizeUdid(string serial)
    {
        var s = serial.Trim();
        if (s.Length == 24 && !s.Contains('-') && s.All(Uri.IsHexDigit)) return $"{s[..8]}-{s[8..]}";
        return s;
    }

    /// <summary>
    /// NetworkAddress is a raw BSD sockaddr: byte 0 = length, byte 1 = family (0x02 = IPv4; 0x1E or 0x17 = IPv6),
    /// IPv4 address at bytes 4..7, IPv6 address at bytes 8..23.
    /// </summary>
    public static IPAddress? ParseNetworkAddress(byte[]? sockaddr)
    {
        if (sockaddr is null || sockaddr.Length < 8) return null;
        var family = sockaddr[1];
        if (family == 0x02) return new IPAddress(sockaddr.AsSpan(4, 4));
        if ((family == 0x1E || family == 0x17) && sockaddr.Length >= 24)
        {
            var scope = sockaddr.Length >= 28 ? BitConverter.ToUInt32(sockaddr, 24) : 0;
            return new IPAddress(sockaddr.AsSpan(8, 16).ToArray(), scope);
        }
        return null;
    }
}

public abstract record UsbmuxEvent;
public sealed record UsbmuxAttachedEvent(UsbmuxDevice Device) : UsbmuxEvent;
public sealed record UsbmuxDetachedEvent(int DeviceId) : UsbmuxEvent;
public sealed record UsbmuxPairedEvent(int DeviceId) : UsbmuxEvent;
