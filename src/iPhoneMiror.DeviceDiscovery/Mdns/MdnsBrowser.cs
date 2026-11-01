using System.Collections.Concurrent;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using Microsoft.Extensions.Logging;

namespace iPhoneMiror.DeviceDiscovery.Mdns;

/// <summary>A DNS-SD service instance resolved from mDNS responses.</summary>
public sealed record MdnsServiceInstance(
    string InstanceName,
    string ServiceType,
    string? HostName,
    int? Port,
    IReadOnlyList<IPAddress> Addresses,
    IReadOnlyDictionary<string, string> Txt,
    DateTimeOffset LastSeen);

/// <summary>
/// Passive/active multicast DNS browser. Sends PTR queries for the requested service types on every
/// IPv4 interface and resolves SRV/TXT/A records from the answers.
/// </summary>
public sealed class MdnsBrowser : IAsyncDisposable
{
    public static readonly IPAddress MulticastAddress = IPAddress.Parse("224.0.0.251");
    public const int MdnsPort = 5353;

    private readonly IReadOnlyList<string> _serviceTypes;
    private readonly ILogger _logger;
    private readonly ConcurrentDictionary<string, InstanceState> _instances = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, List<IPAddress>> _hostAddresses = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<UdpClient> _clients = [];
    private CancellationTokenSource? _cts;
    private readonly List<Task> _tasks = [];

    public MdnsBrowser(IEnumerable<string> serviceTypes, ILogger logger)
    {
        _serviceTypes = serviceTypes.Select(s => s.EndsWith(".local", StringComparison.OrdinalIgnoreCase) ? s : s + ".local").ToList();
        _logger = logger;
    }

    public event EventHandler<MdnsServiceInstance>? InstanceFound;

    public bool IsRunning => _cts is not null;

    public IReadOnlyList<MdnsServiceInstance> Instances => _instances.Values.Select(ToInstance).ToList();

    public Task StartAsync(TimeSpan queryInterval, CancellationToken cancellationToken = default)
    {
        if (_cts is not null) return Task.CompletedTask;
        _cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

        foreach (var local in GetLocalIPv4Addresses())
        {
            var client = CreateClient(local);
            if (client is null) continue;
            _clients.Add(client);
            _tasks.Add(Task.Run(() => ReceiveLoopAsync(client, _cts.Token)));
        }

        if (_clients.Count == 0)
        {
            _cts = null;
            throw new InvalidOperationException("No usable IPv4 network interface for mDNS discovery. Connect the PC to Wi-Fi or Ethernet.");
        }

        _tasks.Add(Task.Run(() => QueryLoopAsync(queryInterval, _cts.Token)));
        return Task.CompletedTask;
    }

    private UdpClient? CreateClient(IPAddress local)
    {
        try
        {
            var socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
            socket.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
            try
            {
                socket.Bind(new IPEndPoint(IPAddress.Any, MdnsPort));
            }
            catch (SocketException)
            {
                // Port 5353 held exclusively: fall back to an ephemeral port; responders answer such "legacy" queries by unicast.
                socket.Bind(new IPEndPoint(IPAddress.Any, 0));
            }
            socket.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.AddMembership, new MulticastOption(MulticastAddress, local));
            socket.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.MulticastInterface, local.GetAddressBytes());
            socket.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.MulticastTimeToLive, 255);
            return new UdpClient { Client = socket };
        }
        catch (SocketException ex)
        {
            _logger.LogDebug(ex, "mDNS socket for {Address} could not be opened.", local);
            return null;
        }
    }

    private static IEnumerable<IPAddress> GetLocalIPv4Addresses()
    {
        foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (nic.OperationalStatus != OperationalStatus.Up || nic.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;
            if (!nic.SupportsMulticast) continue;
            foreach (var ua in nic.GetIPProperties().UnicastAddresses)
            {
                if (ua.Address.AddressFamily == AddressFamily.InterNetwork) yield return ua.Address;
            }
        }
    }

    private async Task QueryLoopAsync(TimeSpan interval, CancellationToken ct)
    {
        var query = DnsMessage.CreateQuery(_serviceTypes.Select(t => new DnsQuestion(t, DnsRecordType.Ptr)).ToArray()).Encode();
        var target = new IPEndPoint(MulticastAddress, MdnsPort);
        while (!ct.IsCancellationRequested)
        {
            foreach (var c in _clients)
            {
                try { await c.SendAsync(query, target, ct).ConfigureAwait(false); }
                catch (SocketException ex) { _logger.LogDebug(ex, "mDNS query send failed."); }
                catch (ObjectDisposedException) { return; }
            }
            try { await Task.Delay(interval, ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { return; }
        }
    }

    private async Task ReceiveLoopAsync(UdpClient client, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            UdpReceiveResult result;
            try { result = await client.ReceiveAsync(ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { return; }
            catch (ObjectDisposedException) { return; }
            catch (SocketException ex) { _logger.LogDebug(ex, "mDNS receive failed."); continue; }

            try
            {
                var msg = DnsMessage.Parse(result.Buffer);
                if (msg.IsResponse) Process(msg, result.RemoteEndPoint.Address);
            }
            catch (FormatException ex)
            {
                _logger.LogTrace(ex, "Ignoring malformed mDNS packet from {Remote}.", result.RemoteEndPoint);
            }
        }
    }

    /// <summary>Applies the records of one mDNS response. Exposed for tests.</summary>
    internal void Process(DnsMessage msg, IPAddress? source = null)
    {
        var touched = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var records = msg.AllRecords.ToList();

        foreach (var r in records.Where(r => r.Type is DnsRecordType.A or DnsRecordType.Aaaa && r.Address is not null))
        {
            var list = _hostAddresses.GetOrAdd(r.Name, _ => []);
            lock (list) { if (!list.Contains(r.Address!)) list.Add(r.Address!); }
        }

        foreach (var r in records.Where(r => r.Type == DnsRecordType.Ptr && r.PtrTarget is not null))
        {
            var type = _serviceTypes.FirstOrDefault(t => string.Equals(t, r.Name, StringComparison.OrdinalIgnoreCase));
            if (type is null) continue;
            var state = _instances.GetOrAdd(r.PtrTarget!, n => new InstanceState(n, type));
            if (r.Ttl == 0) { _instances.TryRemove(r.PtrTarget!, out _); continue; }
            state.LastSeen = DateTimeOffset.UtcNow;
            touched.Add(state.Name);
        }

        foreach (var r in records)
        {
            if (!_instances.TryGetValue(r.Name, out var state)) continue;
            if (r.Srv is { } srv) { state.Host = srv.Target; state.Port = srv.Port; touched.Add(state.Name); }
            if (r.Txt is { } txt) { state.Txt = txt; touched.Add(state.Name); }
        }

        foreach (var name in touched)
        {
            if (!_instances.TryGetValue(name, out var s)) continue;
            if (s.Host is null && source is not null) s.FallbackAddress = source;
            InstanceFound?.Invoke(this, ToInstance(s));
        }
    }

    private MdnsServiceInstance ToInstance(InstanceState s)
    {
        var addresses = new List<IPAddress>();
        if (s.Host is not null && _hostAddresses.TryGetValue(s.Host, out var list))
        {
            lock (list) addresses.AddRange(list);
        }
        if (addresses.Count == 0 && s.FallbackAddress is not null) addresses.Add(s.FallbackAddress);
        return new MdnsServiceInstance(s.Name, s.Type, s.Host, s.Port, addresses, s.Txt, s.LastSeen);
    }

    public async Task StopAsync()
    {
        var cts = _cts;
        if (cts is null) return;
        _cts = null;
        await cts.CancelAsync().ConfigureAwait(false);
        foreach (var c in _clients) c.Dispose();
        _clients.Clear();
        try { await Task.WhenAll(_tasks).ConfigureAwait(false); } catch (OperationCanceledException) { }
        _tasks.Clear();
        cts.Dispose();
    }

    public async ValueTask DisposeAsync() => await StopAsync().ConfigureAwait(false);

    private sealed class InstanceState(string name, string type)
    {
        public string Name { get; } = name;
        public string Type { get; } = type;
        public string? Host { get; set; }
        public int? Port { get; set; }
        public IPAddress? FallbackAddress { get; set; }
        public IReadOnlyDictionary<string, string> Txt { get; set; } = new Dictionary<string, string>();
        public DateTimeOffset LastSeen { get; set; } = DateTimeOffset.UtcNow;
    }
}
