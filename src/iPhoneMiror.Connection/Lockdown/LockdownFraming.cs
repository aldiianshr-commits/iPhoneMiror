using System.Buffers.Binary;
using iPhoneMiror.Core.Plist;

namespace iPhoneMiror.Connection.Lockdown;

/// <summary>lockdownd framing: 4-byte big-endian payload length followed by an XML property list.</summary>
public static class LockdownFraming
{
    public const int MaxMessageSize = 8 * 1024 * 1024;

    public static byte[] Encode(PlistDictionary message)
    {
        var payload = PropertyList.ToXml(message);
        var buffer = new byte[4 + payload.Length];
        BinaryPrimitives.WriteUInt32BigEndian(buffer, (uint)payload.Length);
        payload.CopyTo(buffer, 4);
        return buffer;
    }

    public static async Task WriteAsync(Stream stream, PlistDictionary message, CancellationToken ct)
    {
        await stream.WriteAsync(Encode(message), ct).ConfigureAwait(false);
        await stream.FlushAsync(ct).ConfigureAwait(false);
    }

    public static async Task<PlistDictionary> ReadAsync(Stream stream, CancellationToken ct)
    {
        var len = new byte[4];
        await stream.ReadExactlyAsync(len, ct).ConfigureAwait(false);
        var size = BinaryPrimitives.ReadUInt32BigEndian(len);
        if (size == 0 || size > MaxMessageSize) throw new LockdownException($"Invalid lockdown message size {size}.");
        var payload = new byte[size];
        await stream.ReadExactlyAsync(payload, ct).ConfigureAwait(false);
        return PropertyList.ParseDictionary(payload);
    }
}
