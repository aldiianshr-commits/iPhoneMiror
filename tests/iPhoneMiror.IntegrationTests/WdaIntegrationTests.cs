using iPhoneMiror.Connection.Usbmux;
using iPhoneMiror.Core.Abstractions;
using iPhoneMiror.Core.Models;
using iPhoneMiror.Input;
using iPhoneMiror.IntegrationTests.Fakes;
using iPhoneMiror.Mirroring;
using Microsoft.Extensions.Logging.Abstractions;
using static iPhoneMiror.IntegrationTests.TestHelpers;

namespace iPhoneMiror.IntegrationTests;

/// <summary>Mirroring and input over a usbmux tunnel to emulated WebDriverAgent HTTP and MJPEG servers.</summary>
public class WdaIntegrationTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(30);

    private static byte[] Jpeg(int i) =>
    [
        0xFF, 0xD8, 0xFF, 0xC0, 0x00, 0x0B, 0x08, 0x03, 0x6A, 0x01, 0x92, 0x01, 0x01, 0x11, 0x00, (byte)i, 0xFF, 0xD9,
    ]; // 402 x 874

    [Fact]
    public async Task InputForwarder_SendsTouchesThroughUsbTunnel()
    {
        var mux = new FakeUsbmuxd(Udid);
        var lockdownd = new FakeLockdownd { PendingDialogs = 0 };
        var wda = new FakeWebDriverAgent();
        mux.MapPort(UsbmuxProtocol.LockdownPort, lockdownd.HandleAsync);
        mux.MapPort(8100, wda.HandleHttpAsync);
        await using var h = new Harness(mux, TempDir());
        await h.Service.StartAsync();
        var device = await WaitForAsync(() => Connected(h.Service), Timeout, "device");

        await using var input = new WdaInputForwarder(h.Transport, NullLogger<WdaInputForwarder>.Instance);
        var availability = await input.ConnectAsync(device, 8100);
        Assert.True(availability.IsAvailable, availability.Message);
        Assert.Equal((402d, 874d), input.ScreenSizePoints);

        await input.TapAsync(new DevicePoint(100, 200));
        await input.DoubleTapAsync(new DevicePoint(1, 2));
        await input.LongPressAsync(new DevicePoint(3, 4), TimeSpan.FromSeconds(1));
        await input.DragAsync(new DevicePoint(200, 700), new DevicePoint(200, 300), TimeSpan.FromMilliseconds(250));
        await input.TypeTextAsync("Hi\b");
        await input.PressButtonAsync(HardwareButton.Home);
        await input.PressButtonAsync(HardwareButton.VolumeUp);

        var reqs = wda.Requests.Select(r => $"{r.Method} {r.Path}").ToList();
        Assert.Contains("POST /session/FAKE-SESSION/wda/tap", reqs);
        Assert.Contains("POST /session/FAKE-SESSION/wda/doubleTap", reqs);
        Assert.Contains("POST /session/FAKE-SESSION/wda/touchAndHold", reqs);
        Assert.Contains("POST /session/FAKE-SESSION/actions", reqs);
        Assert.Contains("POST /session/FAKE-SESSION/wda/keys", reqs);
        Assert.Contains("POST /wda/homescreen", reqs);
        Assert.Contains("POST /session/FAKE-SESSION/wda/pressButton", reqs);
        var tap = wda.Requests.First(r => r.Path.EndsWith("/wda/tap"));
        Assert.Contains("\"x\":100", tap.Body);
        Assert.Contains("\"y\":200", tap.Body);
        Assert.Contains(8100, mux.ConnectRequests);
    }

    [Fact]
    public async Task InputForwarder_ReportsUnavailableWithoutWda()
    {
        var mux = new FakeUsbmuxd(Udid);
        mux.MapPort(UsbmuxProtocol.LockdownPort, new FakeLockdownd { PendingDialogs = 0 }.HandleAsync);
        await using var h = new Harness(mux, TempDir());
        await h.Service.StartAsync();
        var device = await WaitForAsync(() => Connected(h.Service), Timeout, "device");

        await using var input = new WdaInputForwarder(h.Transport, NullLogger<WdaInputForwarder>.Instance);
        var availability = await input.ConnectAsync(device, 8100);
        Assert.False(availability.IsAvailable);
        Assert.Contains("WebDriverAgent", availability.Message);
        await Assert.ThrowsAsync<InvalidOperationException>(() => input.TapAsync(new DevicePoint(1, 1)));
    }

    [Fact]
    public async Task Mirroring_StreamsFramesAndReconnects()
    {
        var mux = new FakeUsbmuxd(Udid);
        var wda = new FakeWebDriverAgent();
        mux.MapPort(UsbmuxProtocol.LockdownPort, new FakeLockdownd { PendingDialogs = 0 }.HandleAsync);
        mux.MapPort(8100, wda.HandleHttpAsync);
        mux.MapPort(9100, (s, ct) => wda.HandleMjpegAsync(s, 5, Jpeg, ct));
        await using var h = new Harness(mux, TempDir());
        await h.Service.StartAsync();
        await WaitForAsync(() => Connected(h.Service), Timeout, "device");

        await using var input = new WdaInputForwarder(h.Transport, NullLogger<WdaInputForwarder>.Instance);
        await using var mirroring = new MirroringService(h.Service, h.Transport, NullLogger<MirroringService>.Instance, input);
        var frames = 0;
        var states = new List<MirroringState>();
        mirroring.FrameReceived += (_, _) => Interlocked.Increment(ref frames);
        mirroring.StateChanged += (_, e) => { lock (states) states.Add(e.State); };

        await mirroring.StartAsync(Udid, new MirroringOptions { FrameRate = 24, Quality = 50, ScalingPercent = 50 });
        await WaitUntilAsync(() => Volatile.Read(ref frames) >= 8, Timeout, "frames across a reconnect");

        Assert.True(wda.MjpegConnections >= 2, "stream should reconnect after the server closed it");
        lock (states)
        {
            Assert.Contains(MirroringState.Streaming, states);
            Assert.Contains(MirroringState.Reconnecting, states);
        }
        Assert.Equal(402, mirroring.Stats.Width);
        Assert.Equal(874, mirroring.Stats.Height);
        Assert.Contains(wda.Requests, r => r.Path.EndsWith("/appium/settings") && r.Body.Contains("\"mjpegServerFramerate\":24"));

        await mirroring.StopAsync();
        Assert.Equal(MirroringState.Stopped, mirroring.State);
    }

    [Fact]
    public async Task Mirroring_AirPlayWithoutUxPlay_ReportsError()
    {
        var mux = new FakeUsbmuxd(Udid);
        await using var h = new Harness(mux, TempDir());
        await using var mirroring = new MirroringService(h.Service, h.Transport, NullLogger<MirroringService>.Instance,
            uxPlayPathProvider: () => Path.Combine(Path.GetTempPath(), "does-not-exist", "uxplay.exe"));
        await mirroring.StartAsync("airplay", new MirroringOptions { Backend = MirroringBackendKind.AirPlayUxPlay });
        if (mirroring.State == MirroringState.Error) return;
        // A uxplay binary may exist on PATH on a developer machine; then it must be running.
        Assert.Equal(MirroringState.Starting, mirroring.State);
        await mirroring.StopAsync();
    }
}
