using iPhoneMiror.Connection.Pairing;
using iPhoneMiror.Connection.Services;
using iPhoneMiror.Connection.Usbmux;
using iPhoneMiror.Core.Abstractions;
using iPhoneMiror.Core.Models;
using iPhoneMiror.IntegrationTests.Fakes;
using Microsoft.Extensions.Logging.Abstractions;

namespace iPhoneMiror.IntegrationTests;

internal static class TestHelpers
{
    public const string Udid = "00008140-001234567890801E";

    public static async Task<T> WaitForAsync<T>(Func<T?> probe, TimeSpan timeout, string what) where T : class
    {
        var until = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < until)
        {
            var v = probe();
            if (v is not null) return v;
            await Task.Delay(50);
        }
        throw new TimeoutException($"Timed out waiting for {what}.");
    }

    public static async Task WaitUntilAsync(Func<bool> condition, TimeSpan timeout, string what)
    {
        var until = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < until)
        {
            if (condition()) return;
            await Task.Delay(50);
        }
        throw new TimeoutException($"Timed out waiting for {what}.");
    }

    public sealed class Harness : IAsyncDisposable
    {
        public Harness(FakeUsbmuxd mux, string pairDir, DeviceConnectionOptions? options = null)
        {
            Mux = mux;
            PairDirectory = pairDir;
            Client = new UsbmuxClient("127.0.0.1", mux.Port, TimeSpan.FromSeconds(10));
            Transport = new DeviceTransport(Client);
            Store = new PairRecordStore(Client, pairDir, null, NullLogger<PairRecordStore>.Instance);
            Service = new DeviceConnectionService(Client, Transport, Store, options ?? FastOptions(), NullLogger<DeviceConnectionService>.Instance);
        }

        public FakeUsbmuxd Mux { get; }
        public string PairDirectory { get; }
        public UsbmuxClient Client { get; }
        public DeviceTransport Transport { get; }
        public PairRecordStore Store { get; }
        public DeviceConnectionService Service { get; }

        public async ValueTask DisposeAsync()
        {
            await Service.DisposeAsync();
            await Mux.DisposeAsync();
            if (Directory.Exists(PairDirectory)) Directory.Delete(PairDirectory, true);
        }
    }

    public static DeviceConnectionOptions FastOptions() => new()
    {
        PairingPollInterval = TimeSpan.FromMilliseconds(100),
        PairingTimeout = TimeSpan.FromSeconds(20),
        ReconnectInitialDelay = TimeSpan.FromMilliseconds(100),
        ReconnectMaxDelay = TimeSpan.FromMilliseconds(500),
        RefreshInterval = TimeSpan.FromMinutes(10),
    };

    public static string TempDir() => Path.Combine(Path.GetTempPath(), "iphonemiror-it-" + Guid.NewGuid().ToString("N"));

    public static DeviceInfo? Connected(IDeviceConnectionService s) =>
        s.Devices.FirstOrDefault(d => d.State == DeviceConnectionState.Connected);
}
