using System.Net.Sockets;
using System.Runtime.CompilerServices;
using iPhoneMiror.Core.Plist;

namespace iPhoneMiror.Connection.Usbmux;

public interface IUsbmuxClient
{
    Task<IReadOnlyList<UsbmuxDevice>> ListDevicesAsync(CancellationToken ct = default);
    Task<string> ReadBuidAsync(CancellationToken ct = default);
    Task<byte[]?> ReadPairRecordAsync(string udid, CancellationToken ct = default);
    Task SavePairRecordAsync(string udid, int deviceId, byte[] pairRecordPlist, CancellationToken ct = default);
    Task<Stream> ConnectAsync(int deviceId, int port, CancellationToken ct = default);
    IAsyncEnumerable<UsbmuxEvent> ListenAsync(CancellationToken ct = default);
}

/// <summary>
/// Client for usbmuxd. On Windows the daemon is "Apple Mobile Device Service" (installed with the Apple Devices
/// app or iTunes) listening on 127.0.0.1:27015. Each request uses its own socket, as libusbmuxd does.
/// </summary>
public sealed class UsbmuxClient : IUsbmuxClient
{
    private readonly string _host;
    private readonly int _port;
    private readonly TimeSpan _timeout;
    private int _tag;

    public UsbmuxClient(string host = UsbmuxProtocol.DefaultHost, int port = UsbmuxProtocol.DefaultPort, TimeSpan? timeout = null)
    {
        _host = host;
        _port = port;
        _timeout = timeout ?? TimeSpan.FromSeconds(10);
    }

    private uint NextTag() => (uint)Interlocked.Increment(ref _tag);

    private async Task<NetworkStream> OpenSocketAsync(CancellationToken ct)
    {
        var socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(_timeout);
            await socket.ConnectAsync(_host, _port, timeout.Token).ConfigureAwait(false);
            return new NetworkStream(socket, ownsSocket: true);
        }
        catch (Exception ex) when (ex is SocketException or OperationCanceledException && !ct.IsCancellationRequested)
        {
            socket.Dispose();
            throw new UsbmuxException(
                $"Apple Mobile Device Service is not reachable at {_host}:{_port}. Install the Apple Devices app (Microsoft Store) or iTunes and make sure the service is running.", ex);
        }
    }

    private async Task<PlistDictionary> RequestAsync(PlistDictionary message, CancellationToken ct)
    {
        await using var stream = await OpenSocketAsync(ct).ConfigureAwait(false);
        return await SendAndReceiveAsync(stream, message, ct).ConfigureAwait(false);
    }

    private async Task<PlistDictionary> SendAndReceiveAsync(Stream stream, PlistDictionary message, CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(_timeout);
        var tag = NextTag();
        await stream.WriteAsync(UsbmuxProtocol.Encode(message, tag), timeout.Token).ConfigureAwait(false);
        await stream.FlushAsync(timeout.Token).ConfigureAwait(false);
        while (true)
        {
            var (replyTag, payload) = await UsbmuxProtocol.ReadPacketAsync(stream, timeout.Token).ConfigureAwait(false);
            if (replyTag == tag) return payload;
        }
    }

    private static void EnsureOk(PlistDictionary reply, string operation)
    {
        if (reply.GetString("MessageType") != "Result") return;
        var number = (int)(reply.GetInt64("Number") ?? 0);
        if (number != 0)
        {
            var result = (UsbmuxResult)number;
            throw new UsbmuxException($"usbmux {operation} failed: {result} ({number}).", result);
        }
    }

    public async Task<IReadOnlyList<UsbmuxDevice>> ListDevicesAsync(CancellationToken ct = default)
    {
        var reply = await RequestAsync(UsbmuxProtocol.CreateMessage("ListDevices"), ct).ConfigureAwait(false);
        EnsureOk(reply, "ListDevices");
        var list = new List<UsbmuxDevice>();
        foreach (var item in reply.GetArray("DeviceList") ?? [])
        {
            if (item is not PlistDictionary entry) continue;
            var props = entry.GetDictionary("Properties");
            if (props is null) continue;
            list.Add(UsbmuxDevice.FromProperties(props, entry.GetInt64("DeviceID")));
        }
        return list;
    }

    public async Task<string> ReadBuidAsync(CancellationToken ct = default)
    {
        var reply = await RequestAsync(UsbmuxProtocol.CreateMessage("ReadBUID"), ct).ConfigureAwait(false);
        EnsureOk(reply, "ReadBUID");
        return reply.GetString("BUID") ?? throw new UsbmuxException("ReadBUID returned no BUID.");
    }

    public async Task<byte[]?> ReadPairRecordAsync(string udid, CancellationToken ct = default)
    {
        var msg = UsbmuxProtocol.CreateMessage("ReadPairRecord").With("PairRecordID", udid);
        var reply = await RequestAsync(msg, ct).ConfigureAwait(false);
        if (reply.GetString("MessageType") == "Result" && (reply.GetInt64("Number") ?? 0) != 0) return null;
        return reply.GetData("PairRecordData");
    }

    public async Task SavePairRecordAsync(string udid, int deviceId, byte[] pairRecordPlist, CancellationToken ct = default)
    {
        var msg = UsbmuxProtocol.CreateMessage("SavePairRecord")
            .With("PairRecordID", udid)
            .With("PairRecordData", pairRecordPlist)
            .With("DeviceID", (long)deviceId);
        var reply = await RequestAsync(msg, ct).ConfigureAwait(false);
        EnsureOk(reply, "SavePairRecord");
    }

    public async Task<Stream> ConnectAsync(int deviceId, int port, CancellationToken ct = default)
    {
        var stream = await OpenSocketAsync(ct).ConfigureAwait(false);
        try
        {
            var msg = UsbmuxProtocol.CreateMessage("Connect")
                .With("DeviceID", (long)deviceId)
                .With("PortNumber", UsbmuxProtocol.EncodePort(port));
            var reply = await SendAndReceiveAsync(stream, msg, ct).ConfigureAwait(false);
            EnsureOk(reply, $"Connect(device {deviceId}, port {port})");
            // After a successful Connect the socket becomes a raw byte pipe to the device port.
            return stream;
        }
        catch
        {
            await stream.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    public async IAsyncEnumerable<UsbmuxEvent> ListenAsync([EnumeratorCancellation] CancellationToken ct = default)
    {
        await using var stream = await OpenSocketAsync(ct).ConfigureAwait(false);
        var reply = await SendAndReceiveAsync(stream, UsbmuxProtocol.CreateMessage("Listen"), ct).ConfigureAwait(false);
        EnsureOk(reply, "Listen");
        while (!ct.IsCancellationRequested)
        {
            PlistDictionary payload;
            try
            {
                (_, payload) = await UsbmuxProtocol.ReadPacketAsync(stream, ct).ConfigureAwait(false);
            }
            catch (EndOfStreamException ex)
            {
                throw new UsbmuxException("Apple Mobile Device Service closed the listen connection.", ex);
            }
            var type = payload.GetString("MessageType");
            switch (type)
            {
                case "Attached":
                    var props = payload.GetDictionary("Properties");
                    if (props is not null) yield return new UsbmuxAttachedEvent(UsbmuxDevice.FromProperties(props, payload.GetInt64("DeviceID")));
                    break;
                case "Detached":
                    yield return new UsbmuxDetachedEvent((int)(payload.GetInt64("DeviceID") ?? 0));
                    break;
                case "Paired":
                    yield return new UsbmuxPairedEvent((int)(payload.GetInt64("DeviceID") ?? 0));
                    break;
            }
        }
    }
}
