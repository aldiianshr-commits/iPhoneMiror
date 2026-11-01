using System.Buffers.Binary;
using iPhoneMiror.Core.Plist;

namespace iPhoneMiror.Connection.Usbmux;

/// <summary>
/// Wire format of the usbmux protocol spoken by usbmuxd / Apple Mobile Device Service.
/// Header (16 bytes, little-endian): length (including header), version (1 = plist), message type (8 = plist), tag.
/// Reference: libusbmuxd (LGPL-2.1), include/usbmuxd-proto.h.
/// </summary>
public static class UsbmuxProtocol
{
    public const int HeaderSize = 16;
    public const uint VersionPlist = 1;
    public const uint MessagePlist = 8;
    public const uint MessageResult = 1;
    public const int LibUsbmuxVersion = 3;
    public const string ClientVersion = "iPhoneMiror-0.1";
    public const string ProgramName = "iPhoneMiror";
    public const int MaxPacketSize = 4 * 1024 * 1024;

    /// <summary>Default endpoint of Apple Mobile Device Service on Windows.</summary>
    public const string DefaultHost = "127.0.0.1";
    public const int DefaultPort = 27015;

    public const int LockdownPort = 62078;

    public static byte[] Encode(PlistDictionary message, uint tag)
    {
        var payload = PropertyList.ToXml(message);
        var packet = new byte[HeaderSize + payload.Length];
        BinaryPrimitives.WriteUInt32LittleEndian(packet.AsSpan(0), (uint)packet.Length);
        BinaryPrimitives.WriteUInt32LittleEndian(packet.AsSpan(4), VersionPlist);
        BinaryPrimitives.WriteUInt32LittleEndian(packet.AsSpan(8), MessagePlist);
        BinaryPrimitives.WriteUInt32LittleEndian(packet.AsSpan(12), tag);
        payload.CopyTo(packet, HeaderSize);
        return packet;
    }

    public static (uint Length, uint Version, uint Message, uint Tag) DecodeHeader(ReadOnlySpan<byte> header) => (
        BinaryPrimitives.ReadUInt32LittleEndian(header),
        BinaryPrimitives.ReadUInt32LittleEndian(header[4..]),
        BinaryPrimitives.ReadUInt32LittleEndian(header[8..]),
        BinaryPrimitives.ReadUInt32LittleEndian(header[12..]));

    public static PlistDictionary CreateMessage(string messageType)
    {
        var d = new PlistDictionary
        {
            ["MessageType"] = messageType,
            ["ClientVersionString"] = ClientVersion,
            ["ProgName"] = ProgramName,
            ["kLibUSBMuxVersion"] = (long)LibUsbmuxVersion,
        };
        return d;
    }

    /// <summary>usbmuxd expects the port in network byte order stored in a host-order integer.</summary>
    public static long EncodePort(int port) => (long)(ushort)(((port & 0xFF) << 8) | ((port >> 8) & 0xFF));

    public static int DecodePort(long encoded) => (int)((((ushort)encoded & 0xFF) << 8) | (((ushort)encoded >> 8) & 0xFF));

    public static async Task<(uint Tag, PlistDictionary Payload)> ReadPacketAsync(Stream stream, CancellationToken ct)
    {
        var header = new byte[HeaderSize];
        await stream.ReadExactlyAsync(header, ct).ConfigureAwait(false);
        var (length, version, message, tag) = DecodeHeader(header);
        if (length < HeaderSize || length > MaxPacketSize) throw new UsbmuxException($"Invalid usbmux packet length {length}.");
        var payload = new byte[length - HeaderSize];
        await stream.ReadExactlyAsync(payload, ct).ConfigureAwait(false);
        if (version != VersionPlist || message != MessagePlist)
        {
            // Binary protocol (version 0) result packets carry a single uint32 result code.
            if (message == MessageResult && payload.Length >= 4)
            {
                return (tag, new PlistDictionary { ["MessageType"] = "Result", ["Number"] = (long)BinaryPrimitives.ReadUInt32LittleEndian(payload) });
            }
            throw new UsbmuxException($"Unsupported usbmux packet (version {version}, message {message}).");
        }
        return (tag, PropertyList.ParseDictionary(payload));
    }
}

public enum UsbmuxResult
{
    Ok = 0,
    BadCommand = 1,
    BadDevice = 2,
    ConnectionRefused = 3,
    BadVersion = 6,
}

public sealed class UsbmuxException : IOException
{
    public UsbmuxException(string message, UsbmuxResult? result = null) : base(message) { Result = result; }
    public UsbmuxException(string message, Exception inner) : base(message, inner) { }
    public UsbmuxResult? Result { get; }
}
