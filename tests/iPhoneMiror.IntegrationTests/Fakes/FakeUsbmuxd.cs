using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using iPhoneMiror.Connection.Usbmux;
using iPhoneMiror.Core.Plist;

namespace iPhoneMiror.IntegrationTests.Fakes;

/// <summary>
/// In-process emulation of usbmuxd / Apple Mobile Device Service speaking the plist usbmux protocol on loopback.
/// "Connect" hands the client socket to a registered handler for the requested device port.
/// This validates iPhoneMiror's protocol code end-to-end; it is NOT a substitute for testing with a real iPhone.
/// </summary>
public sealed class FakeUsbmuxd : IAsyncDisposable
{
    private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
    private readonly CancellationTokenSource _cts = new();
    private readonly ConcurrentDictionary<int, Func<NetworkStream, CancellationToken, Task>> _portHandlers = new();
    private readonly List<(Stream Stream, SemaphoreSlim Lock)> _listeners = [];
    private readonly Task _acceptLoop;

    public FakeUsbmuxd(string udid, int deviceId = 1)
    {
        Udid = udid;
        DeviceId = deviceId;
        _listener.Start();
        _acceptLoop = Task.Run(AcceptLoopAsync);
    }

    public string Udid { get; }
    public int DeviceId { get; private set; }
    public bool Attached { get; private set; } = true;
    public int Port => ((IPEndPoint)_listener.LocalEndpoint).Port;
    public string Buid { get; } = "FAKE-SYSTEM-BUID";
    public ConcurrentDictionary<string, byte[]> PairRecords { get; } = new();
    public ConcurrentQueue<int> ConnectRequests { get; } = new();

    public void MapPort(int devicePort, Func<NetworkStream, CancellationToken, Task> handler) => _portHandlers[devicePort] = handler;

    private PlistDictionary DeviceProperties() => new()
    {
        ["DeviceID"] = (long)DeviceId,
        ["SerialNumber"] = Udid,
        ["ConnectionType"] = "USB",
        ["ProductID"] = 4776L,
        ["LocationID"] = 0x14100000L,
        ["ConnectionSpeed"] = 480000000L,
    };

    private async Task AcceptLoopAsync()
    {
        while (!_cts.IsCancellationRequested)
        {
            TcpClient client;
            try { client = await _listener.AcceptTcpClientAsync(_cts.Token); }
            catch (Exception) { return; }
            _ = Task.Run(() => HandleClientAsync(client));
        }
    }

    private async Task HandleClientAsync(TcpClient client)
    {
        var stream = client.GetStream();
        var handedOff = false;
        try
        {
            while (!_cts.IsCancellationRequested)
            {
                var (tag, msg) = await UsbmuxProtocol.ReadPacketAsync(stream, _cts.Token);
                switch (msg.GetString("MessageType"))
                {
                    case "ListDevices":
                        var list = new List<object>();
                        if (Attached) list.Add(new PlistDictionary { ["DeviceID"] = (long)DeviceId, ["MessageType"] = "Attached", ["Properties"] = DeviceProperties() });
                        await Send(stream, new PlistDictionary { ["DeviceList"] = list }, tag);
                        break;
                    case "Listen":
                        await Send(stream, Result(0), tag);
                        var gate = new SemaphoreSlim(1, 1);
                        lock (_listeners) _listeners.Add((stream, gate));
                        handedOff = true;
                        if (Attached) await SendLocked(stream, gate, AttachedMessage());
                        return; // keep socket open; events are pushed by Attach/Detach.
                    case "ReadBUID":
                        await Send(stream, new PlistDictionary { ["BUID"] = Buid }, tag);
                        break;
                    case "ReadPairRecord":
                        var id = msg.GetString("PairRecordID")!;
                        await Send(stream, PairRecords.TryGetValue(id, out var data) ? new PlistDictionary { ["PairRecordData"] = data } : Result(2), tag);
                        break;
                    case "SavePairRecord":
                        PairRecords[msg.GetString("PairRecordID")!] = msg.GetData("PairRecordData")!;
                        await Send(stream, Result(0), tag);
                        break;
                    case "Connect":
                        var port = UsbmuxProtocol.DecodePort(msg.GetInt64("PortNumber") ?? 0);
                        ConnectRequests.Enqueue(port);
                        if (!Attached || msg.GetInt64("DeviceID") != DeviceId || !_portHandlers.TryGetValue(port, out var handler))
                        {
                            await Send(stream, Result((int)UsbmuxResult.ConnectionRefused), tag);
                            return;
                        }
                        await Send(stream, Result(0), tag);
                        handedOff = true;
                        try { await handler(stream, _cts.Token); }
                        finally { client.Dispose(); }
                        return;
                    default:
                        await Send(stream, Result(1), tag);
                        break;
                }
            }
        }
        catch (Exception) when (_cts.IsCancellationRequested) { }
        catch (EndOfStreamException) { }
        catch (IOException) { }
        finally
        {
            if (!handedOff) client.Dispose();
        }
    }

    private PlistDictionary AttachedMessage() => new() { ["MessageType"] = "Attached", ["DeviceID"] = (long)DeviceId, ["Properties"] = DeviceProperties() };

    private static PlistDictionary Result(int n) => new() { ["MessageType"] = "Result", ["Number"] = (long)n };

    private static async Task Send(Stream s, PlistDictionary d, uint tag)
    {
        await s.WriteAsync(UsbmuxProtocol.Encode(d, tag));
        await s.FlushAsync();
    }

    private static async Task SendLocked(Stream s, SemaphoreSlim gate, PlistDictionary d)
    {
        await gate.WaitAsync();
        try { await Send(s, d, 0); }
        catch (IOException) { }
        catch (ObjectDisposedException) { }
        finally { gate.Release(); }
    }

    private async Task Broadcast(PlistDictionary d)
    {
        List<(Stream, SemaphoreSlim)> copy;
        lock (_listeners) copy = [.. _listeners];
        foreach (var (s, g) in copy) await SendLocked(s, g, d);
    }

    public Task DetachAsync()
    {
        Attached = false;
        return Broadcast(new PlistDictionary { ["MessageType"] = "Detached", ["DeviceID"] = (long)DeviceId });
    }

    public Task AttachAsync()
    {
        DeviceId++;
        Attached = true;
        return Broadcast(AttachedMessage());
    }

    public async ValueTask DisposeAsync()
    {
        await _cts.CancelAsync();
        _listener.Stop();
        lock (_listeners) foreach (var (s, _) in _listeners) s.Dispose();
        try { await _acceptLoop; } catch { }
    }
}
