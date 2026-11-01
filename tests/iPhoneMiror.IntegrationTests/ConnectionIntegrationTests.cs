using iPhoneMiror.Connection.Pairing;
using iPhoneMiror.Connection.Services;
using iPhoneMiror.Connection.Usbmux;
using iPhoneMiror.Core.Models;
using iPhoneMiror.IntegrationTests.Fakes;
using Microsoft.Extensions.Logging.Abstractions;
using static iPhoneMiror.IntegrationTests.TestHelpers;

namespace iPhoneMiror.IntegrationTests;

/// <summary>
/// End-to-end tests of usbmux + lockdown + pairing + TLS session against protocol emulators.
/// They prove the protocol implementation is internally consistent; real-device validation is tracked in docs/COMPATIBILITY.md.
/// </summary>
public class ConnectionIntegrationTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(30);

    [Fact]
    public async Task UsbmuxClient_ListsDevicesAndReadsBuid()
    {
        await using var mux = new FakeUsbmuxd(Udid, deviceId: 4);
        var client = new UsbmuxClient("127.0.0.1", mux.Port);
        var devices = await client.ListDevicesAsync();
        var d = Assert.Single(devices);
        Assert.Equal(4, d.DeviceId);
        Assert.Equal(Udid, d.Udid);
        Assert.Equal(ConnectionType.Usb, d.ConnectionType);
        Assert.Equal("FAKE-SYSTEM-BUID", await client.ReadBuidAsync());
        Assert.Null(await client.ReadPairRecordAsync(Udid));
    }

    [Fact]
    public async Task UsbmuxClient_ConnectToClosedPort_IsRefused()
    {
        await using var mux = new FakeUsbmuxd(Udid);
        var client = new UsbmuxClient("127.0.0.1", mux.Port);
        var ex = await Assert.ThrowsAsync<UsbmuxException>(() => client.ConnectAsync(1, 9999));
        Assert.Equal(UsbmuxResult.ConnectionRefused, ex.Result);
    }

    [Fact]
    public async Task UnreachableService_ReportsClearError()
    {
        var client = new UsbmuxClient("127.0.0.1", 1, TimeSpan.FromSeconds(2));
        var ex = await Assert.ThrowsAsync<UsbmuxException>(() => client.ListDevicesAsync());
        Assert.Contains("Apple Mobile Device Service", ex.Message);
    }

    [Fact]
    public async Task Attach_TrustDialog_Pairs_StartsTlsSession_ReadsInfo()
    {
        var mux = new FakeUsbmuxd(Udid);
        var lockdownd = new FakeLockdownd { PendingDialogs = 2 };
        mux.MapPort(UsbmuxProtocol.LockdownPort, lockdownd.HandleAsync);
        await using var h = new Harness(mux, TempDir());

        var states = new List<PairingState>();
        h.Service.DeviceChanged += (_, e) => { lock (states) states.Add(e.Device.Pairing); };
        await h.Service.StartAsync();

        var device = await WaitForAsync(() => Connected(h.Service), Timeout, "connected device");
        Assert.Equal("Integration iPhone", device.Name);
        Assert.Equal("iPhone17,1", device.ProductType);
        Assert.Equal("iPhone 16 Pro", device.ModelName);
        Assert.Equal("26.0", device.ProductVersion);
        Assert.Equal(87, device.BatteryLevel);
        Assert.True(device.BatteryCharging);
        Assert.Equal(ConnectionType.Usb, device.ConnectionType);
        Assert.Equal(PairingState.Paired, device.Pairing);

        lock (states) Assert.Contains(PairingState.DialogPending, states);
        Assert.Equal(3, lockdownd.PairRequests);
        Assert.True(lockdownd.TlsSessions >= 1);
        Assert.True(h.Service.ServiceStatus.IsAvailable);

        // The pair record was saved to the (fake) Apple Mobile Device Service and locally.
        var saved = PairRecord.FromBytes(mux.PairRecords[Udid]);
        Assert.Equal(lockdownd.PairedHostId, saved.HostId);
        Assert.Equal("FAKE-SYSTEM-BUID", saved.SystemBuid);
        Assert.Equal(new byte[] { 0xE5, 0xC0 }, saved.EscrowBag);
        Assert.True(saved.HasHostPrivateKey);
        Assert.Single(Directory.GetFiles(h.PairDirectory));
    }

    [Fact]
    public async Task Replug_ReconnectsWithStoredPairing()
    {
        var mux = new FakeUsbmuxd(Udid);
        var lockdownd = new FakeLockdownd { PendingDialogs = 0 };
        mux.MapPort(UsbmuxProtocol.LockdownPort, lockdownd.HandleAsync);
        await using var h = new Harness(mux, TempDir());
        await h.Service.StartAsync();
        await WaitForAsync(() => Connected(h.Service), Timeout, "first connection");
        Assert.Equal(1, lockdownd.PairRequests);

        await mux.DetachAsync();
        await WaitUntilAsync(() => h.Service.Find(Udid)?.State == DeviceConnectionState.Reconnecting, Timeout, "reconnecting state");
        Assert.Equal(0, h.Service.Find(Udid)!.MuxDeviceId);

        await mux.AttachAsync();
        var again = await WaitForAsync(() => Connected(h.Service), Timeout, "reconnection");
        Assert.Equal(mux.DeviceId, again.MuxDeviceId);
        Assert.Equal(1, lockdownd.PairRequests); // no new Trust prompt
    }

    [Fact]
    public async Task UserDeniesTrust_IsReported()
    {
        var mux = new FakeUsbmuxd(Udid);
        mux.MapPort(UsbmuxProtocol.LockdownPort, new FakeLockdownd { DenyPairing = true }.HandleAsync);
        await using var h = new Harness(mux, TempDir());
        await h.Service.StartAsync();
        var d = await WaitForAsync(() => h.Service.Devices.FirstOrDefault(x => x.Pairing == PairingState.UserDenied), Timeout, "denied state");
        Assert.Equal(DeviceConnectionState.AwaitingTrust, d.State);
        Assert.Equal("26.0", d.ProductVersion); // public values are still read before pairing
    }

    [Fact]
    public async Task UserDisconnect_StopsUntilReconnect()
    {
        var mux = new FakeUsbmuxd(Udid);
        mux.MapPort(UsbmuxProtocol.LockdownPort, new FakeLockdownd { PendingDialogs = 0 }.HandleAsync);
        await using var h = new Harness(mux, TempDir());
        await h.Service.StartAsync();
        await WaitForAsync(() => Connected(h.Service), Timeout, "connection");

        await h.Service.DisconnectAsync(Udid);
        Assert.Equal(DeviceConnectionState.Disconnected, h.Service.Find(Udid)!.State);

        await h.Service.ReconnectAsync(Udid);
        await WaitForAsync(() => Connected(h.Service), Timeout, "manual reconnect");
    }

    [Fact]
    public async Task ServiceUnavailable_IsReportedAndRetried()
    {
        // Reserve a free port with nothing listening on it.
        var probe = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
        probe.Start();
        var port = ((System.Net.IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();

        var client = new UsbmuxClient("127.0.0.1", port, TimeSpan.FromSeconds(2));
        var dir = TempDir();
        await using var service = new DeviceConnectionService(client, new DeviceTransport(client),
            new PairRecordStore(client, dir, null, NullLogger<PairRecordStore>.Instance), FastOptions(), NullLogger<DeviceConnectionService>.Instance);
        await service.StartAsync();
        await WaitUntilAsync(() => !service.ServiceStatus.IsAvailable && service.ServiceStatus.Message.Contains("Apple Mobile Device Service"), Timeout, "unavailable status");
        Assert.Empty(service.Devices);
    }
}
