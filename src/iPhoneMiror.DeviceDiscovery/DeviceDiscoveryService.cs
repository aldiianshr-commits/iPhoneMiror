using System.Collections.Concurrent;
using iPhoneMiror.Core.Abstractions;
using iPhoneMiror.DeviceDiscovery.Bluetooth;
using iPhoneMiror.DeviceDiscovery.Mdns;
using Microsoft.Extensions.Logging;

namespace iPhoneMiror.DeviceDiscovery;

/// <summary>Combines Wi-Fi (mDNS/DNS-SD) and Bluetooth discovery.</summary>
public sealed class DeviceDiscoveryService : IDeviceDiscoveryService
{
    private readonly IBluetoothScanner _bluetooth;
    private readonly ILogger<DeviceDiscoveryService> _logger;
    private readonly Func<string, string?>? _resolveNameByWifiMac;
    private readonly ConcurrentDictionary<string, DiscoveredDevice> _discovered = new();
    private MdnsBrowser? _mdns;

    public DeviceDiscoveryService(IBluetoothScanner bluetooth, ILogger<DeviceDiscoveryService> logger, Func<string, string?>? resolveNameByWifiMac = null)
    {
        _bluetooth = bluetooth;
        _logger = logger;
        _resolveNameByWifiMac = resolveNameByWifiMac;
        _bluetooth.DeviceSeen += (_, d) => Report(d);
    }

    public IReadOnlyList<DiscoveredDevice> Discovered => _discovered.Values.OrderByDescending(d => d.LastSeen).ToList();

    public event EventHandler<DiscoveredDevice>? DeviceDiscovered;

    public bool IsNetworkDiscoveryRunning => _mdns?.IsRunning == true;
    public bool IsBluetoothScanRunning => _bluetooth.IsScanning;

    public async Task StartNetworkDiscoveryAsync(CancellationToken cancellationToken = default)
    {
        if (_mdns is not null) return;
        var browser = new MdnsBrowser(AppleServiceNames.All, _logger);
        browser.InstanceFound += (_, inst) => Report(ToDiscovered(inst));
        await browser.StartAsync(TimeSpan.FromSeconds(10), cancellationToken).ConfigureAwait(false);
        _mdns = browser;
    }

    public async Task StopNetworkDiscoveryAsync()
    {
        var m = _mdns;
        _mdns = null;
        if (m is not null) await m.DisposeAsync().ConfigureAwait(false);
    }

    internal DiscoveredDevice ToDiscovered(MdnsServiceInstance inst)
    {
        var mac = AppleServiceNames.TryGetWifiMac(inst.InstanceName);
        var known = mac is null ? null : _resolveNameByWifiMac?.Invoke(mac);
        var name = known ?? (mac is not null ? $"iPhone (Wi-Fi {mac})" : inst.InstanceName.Split('.')[0]);
        var address = inst.Addresses.FirstOrDefault(a => a.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork) ?? inst.Addresses.FirstOrDefault();
        return new DiscoveredDevice("mdns:" + inst.InstanceName, name, DiscoverySource.Mdns, address?.ToString(), null, inst.ServiceType, false, inst.LastSeen);
    }

    private void Report(DiscoveredDevice d)
    {
        _discovered[d.Id] = d;
        try { DeviceDiscovered?.Invoke(this, d); }
        catch (Exception ex) { _logger.LogError(ex, "DeviceDiscovered handler threw."); }
    }

    public Task<BluetoothStatus> GetBluetoothStatusAsync(CancellationToken cancellationToken = default) => _bluetooth.GetStatusAsync(cancellationToken);

    public Task StartBluetoothScanAsync(CancellationToken cancellationToken = default) => _bluetooth.StartAsync(cancellationToken);

    public Task StopBluetoothScanAsync() => _bluetooth.StopAsync();

    public Task<string> PairBluetoothAsync(string deviceId, CancellationToken cancellationToken = default) => _bluetooth.PairAsync(deviceId, cancellationToken);

    public async ValueTask DisposeAsync()
    {
        await StopNetworkDiscoveryAsync().ConfigureAwait(false);
        await _bluetooth.DisposeAsync().ConfigureAwait(false);
    }
}
