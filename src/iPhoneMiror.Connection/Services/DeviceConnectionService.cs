using System.Collections.Concurrent;
using iPhoneMiror.Connection.Lockdown;
using iPhoneMiror.Connection.Pairing;
using iPhoneMiror.Connection.Usbmux;
using iPhoneMiror.Core.Abstractions;
using iPhoneMiror.Core.Models;
using iPhoneMiror.Core.Plist;
using iPhoneMiror.Core.Util;
using Microsoft.Extensions.Logging;

namespace iPhoneMiror.Connection.Services;

/// <summary>
/// Tracks iPhones attached through Apple Mobile Device Service (USB and Wi-Fi), reads their information through
/// lockdownd, performs pairing ("Trust This Computer") and reconnects automatically.
/// </summary>
public sealed class DeviceConnectionService : IDeviceConnectionService
{
    private readonly IUsbmuxClient _usbmux;
    private readonly IDeviceTransport _transport;
    private readonly IPairRecordStore _pairStore;
    private readonly IDeviceRepository? _repository;
    private readonly DeviceConnectionOptions _options;
    private readonly ILogger<DeviceConnectionService> _logger;
    private readonly ReconnectPolicy _policy;
    private readonly ConcurrentDictionary<string, DeviceEntry> _entries = new(StringComparer.OrdinalIgnoreCase);
    private CancellationTokenSource? _cts;
    private Task? _listenTask;
    private Task? _refreshTask;

    public DeviceConnectionService(
        IUsbmuxClient usbmux,
        IDeviceTransport transport,
        IPairRecordStore pairStore,
        DeviceConnectionOptions options,
        ILogger<DeviceConnectionService> logger,
        IDeviceRepository? repository = null)
    {
        _usbmux = usbmux;
        _transport = transport;
        _pairStore = pairStore;
        _options = options;
        _logger = logger;
        _repository = repository;
        _policy = new ReconnectPolicy(options.ReconnectInitialDelay, options.ReconnectMaxDelay);
        ServiceStatus = new MuxServiceStatus(false, "Not started.", DateTimeOffset.UtcNow);
    }

    public IReadOnlyList<DeviceInfo> Devices => _entries.Values.Select(e => e.Info).OrderBy(d => d.DisplayName, StringComparer.CurrentCulture).ToList();

    public MuxServiceStatus ServiceStatus { get; private set; }

    public event EventHandler<DeviceChangedEventArgs>? DeviceChanged;
    public event EventHandler<MuxServiceStatus>? ServiceStatusChanged;

    public DeviceInfo? Find(string udid) => _entries.TryGetValue(udid, out var e) ? e.Info : null;

    public Task StartAsync(CancellationToken cancellationToken = default)
    {
        if (_cts is not null) return Task.CompletedTask;
        _cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var token = _cts.Token; // captured now: StopAsync may clear _cts before the tasks start
        _listenTask = Task.Run(() => ListenLoopAsync(token), CancellationToken.None);
        _refreshTask = Task.Run(() => RefreshLoopAsync(token), CancellationToken.None);
        return Task.CompletedTask;
    }

    public async Task StopAsync()
    {
        var cts = _cts;
        if (cts is null) return;
        _cts = null;
        await cts.CancelAsync().ConfigureAwait(false);
        foreach (var e in _entries.Values) e.CancelWork();
        try { await Task.WhenAll(_listenTask ?? Task.CompletedTask, _refreshTask ?? Task.CompletedTask).ConfigureAwait(false); }
        catch (OperationCanceledException) { }
        cts.Dispose();
    }

    public async ValueTask DisposeAsync() => await StopAsync().ConfigureAwait(false);

    // ---------------------------------------------------------------- listen loop

    private async Task ListenLoopAsync(CancellationToken ct)
    {
        var attempt = 0;
        while (!ct.IsCancellationRequested)
        {
            try
            {
                // ListDevices verifies the service is reachable before subscribing.
                var current = await _usbmux.ListDevicesAsync(ct).ConfigureAwait(false);
                SetServiceStatus(true, $"Apple Mobile Device Service connected ({current.Count} attachment(s)).");
                attempt = 0;
                SyncAttachments(current);
                await foreach (var ev in _usbmux.ListenAsync(ct).ConfigureAwait(false))
                {
                    switch (ev)
                    {
                        case UsbmuxAttachedEvent a: HandleAttached(a.Device); break;
                        case UsbmuxDetachedEvent d: HandleDetached(d.DeviceId); break;
                        case UsbmuxPairedEvent p: HandlePaired(p.DeviceId); break;
                    }
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "usbmux listen loop failed.");
                SetServiceStatus(false, ex.Message);
                DropAllAttachments();
            }

            if (ct.IsCancellationRequested) break;
            try { await Task.Delay(_policy.GetDelay(attempt++), ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { break; }
        }
    }

    private void SetServiceStatus(bool available, string message)
    {
        if (ServiceStatus.IsAvailable == available && ServiceStatus.Message == message) return;
        ServiceStatus = new MuxServiceStatus(available, message, DateTimeOffset.UtcNow);
        ServiceStatusChanged?.Invoke(this, ServiceStatus);
    }

    private void SyncAttachments(IReadOnlyList<UsbmuxDevice> current)
    {
        var ids = current.Select(d => d.DeviceId).ToHashSet();
        foreach (var entry in _entries.Values)
        {
            foreach (var id in entry.AttachmentIds().Where(id => !ids.Contains(id)).ToList()) HandleDetached(id);
        }
        foreach (var d in current) HandleAttached(d);
    }

    private void DropAllAttachments()
    {
        foreach (var entry in _entries.Values)
        {
            foreach (var id in entry.AttachmentIds().ToList()) HandleDetached(id);
        }
    }

    internal void HandleAttached(UsbmuxDevice device)
    {
        if (device.ConnectionType == ConnectionType.Network && !_options.EnableWifiDevices) return;
        var isNew = false;
        var entry = _entries.GetOrAdd(device.Udid, udid =>
        {
            isNew = true;
            return new DeviceEntry(new DeviceInfo { Udid = udid, ConnectionType = device.ConnectionType, State = DeviceConnectionState.Connecting });
        });
        var alreadyKnown = entry.HasAttachment(device.DeviceId);
        entry.AddAttachment(device);
        Update(entry, info => ApplyAttachment(info, entry) with
        {
            State = entry.UserDisconnected ? DeviceConnectionState.Disconnected : (info.State is DeviceConnectionState.Connected && alreadyKnown ? info.State : DeviceConnectionState.Connecting),
            LastSeen = DateTimeOffset.UtcNow,
        }, isNew ? DeviceChangeKind.Added : DeviceChangeKind.Updated);
        _ = LogEventAsync(device.Udid, "attached", device.ConnectionType.ToString());
        if (!entry.UserDisconnected && !alreadyKnown) StartInitialize(entry);
    }

    internal void HandleDetached(int muxDeviceId)
    {
        var entry = _entries.Values.FirstOrDefault(e => e.HasAttachment(muxDeviceId));
        if (entry is null) return;
        entry.RemoveAttachment(muxDeviceId);
        if (entry.AttachmentCount == 0)
        {
            entry.CancelWork();
            Update(entry, info => info with
            {
                MuxDeviceId = 0,
                State = _options.AutoReconnect && !entry.UserDisconnected ? DeviceConnectionState.Reconnecting : DeviceConnectionState.Disconnected,
                LastSeen = DateTimeOffset.UtcNow,
            });
            _ = LogEventAsync(entry.Info.Udid, "detached", null);
        }
        else
        {
            Update(entry, info => ApplyAttachment(info, entry));
        }
    }

    private void HandlePaired(int muxDeviceId)
    {
        var entry = _entries.Values.FirstOrDefault(e => e.HasAttachment(muxDeviceId));
        if (entry is not null && !entry.UserDisconnected) StartInitialize(entry);
    }

    private static DeviceInfo ApplyAttachment(DeviceInfo info, DeviceEntry entry)
    {
        var preferred = entry.PreferredAttachment();
        if (preferred is null) return info;
        return info with
        {
            MuxDeviceId = preferred.DeviceId,
            ConnectionType = preferred.ConnectionType,
            NetworkAddress = preferred.NetworkAddress?.ToString() ?? info.NetworkAddress,
        };
    }

    // ---------------------------------------------------------------- initialization / session

    private void StartInitialize(DeviceEntry entry)
    {
        var token = entry.BeginWork(_cts?.Token ?? CancellationToken.None);
        _ = Task.Run(() => InitializeWithRetryAsync(entry, token), CancellationToken.None);
    }

    private async Task InitializeWithRetryAsync(DeviceEntry entry, CancellationToken ct)
    {
        for (var attempt = 0; attempt < _options.MaxInitializeAttempts && !ct.IsCancellationRequested; attempt++)
        {
            try
            {
                await InitializeAsync(entry, ct).ConfigureAwait(false);
                return;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Initializing {Udid} failed (attempt {Attempt}).", entry.Info.Udid, attempt + 1);
                Update(entry, i => i with { State = DeviceConnectionState.Error, LastError = ex.Message });
                if (!_options.AutoReconnect || entry.AttachmentCount == 0) return;
                try { await Task.Delay(_policy.GetDelay(attempt), ct).ConfigureAwait(false); }
                catch (OperationCanceledException) { return; }
                Update(entry, i => i with { State = DeviceConnectionState.Reconnecting });
            }
        }
    }

    private async Task<LockdownClient> OpenLockdownAsync(DeviceEntry entry, CancellationToken ct)
    {
        var stream = await _transport.OpenAsync(entry.Info, UsbmuxProtocol.LockdownPort, ct).ConfigureAwait(false);
        var client = new LockdownClient(stream, _options.Label);
        try
        {
            await client.QueryTypeAsync(ct).ConfigureAwait(false);
            return client;
        }
        catch
        {
            await client.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    private async Task InitializeAsync(DeviceEntry entry, CancellationToken ct)
    {
        await entry.Gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            Update(entry, i => i with { State = DeviceConnectionState.Connecting, LastError = null });

            await using (var lockdown = await OpenLockdownAsync(entry, ct).ConfigureAwait(false))
            {
                // Values readable without a session (limited set on modern iOS).
                try
                {
                    if (await lockdown.GetValueAsync(null, null, ct).ConfigureAwait(false) is PlistDictionary pub)
                        Update(entry, i => ApplyValues(i, pub));
                }
                catch (LockdownException ex)
                {
                    _logger.LogDebug(ex, "Unauthenticated GetValue failed for {Udid}.", entry.Info.Udid);
                }
            }

            var record = await _pairStore.LoadAsync(entry.Info.Udid, ct).ConfigureAwait(false);
            if (record is not null && await TryReadSessionInfoAsync(entry, record, ct).ConfigureAwait(false))
            {
                return;
            }

            Update(entry, i => i with { State = DeviceConnectionState.AwaitingTrust, Pairing = PairingState.NotPaired });
            if (_options.AutoPair)
            {
                var state = await PairCoreAsync(entry, ct).ConfigureAwait(false);
                if (state == PairingState.Paired)
                {
                    record = await _pairStore.LoadAsync(entry.Info.Udid, ct).ConfigureAwait(false);
                    if (record is not null) await TryReadSessionInfoAsync(entry, record, ct).ConfigureAwait(false);
                }
            }
        }
        finally
        {
            entry.Gate.Release();
        }
    }

    /// <returns>false if the record is not accepted by the device (stale pairing).</returns>
    private async Task<bool> TryReadSessionInfoAsync(DeviceEntry entry, PairRecord record, CancellationToken ct)
    {
        await using var lockdown = await OpenLockdownAsync(entry, ct).ConfigureAwait(false);
        try
        {
            await lockdown.StartSessionAsync(record, ct).ConfigureAwait(false);
        }
        catch (LockdownException ex) when (ex.IsInvalidHostId || ex.IsPasswordProtected || ex.ErrorCode is "SessionInactive" || ex.InnerException is not null)
        {
            _logger.LogInformation("Stored pairing for {Udid} was not accepted: {Message}", entry.Info.Udid, ex.Message);
            if (ex.IsPasswordProtected)
            {
                Update(entry, i => i with { State = DeviceConnectionState.AwaitingTrust, Pairing = PairingState.DeviceLocked, LastError = "Unlock the iPhone to continue." });
            }
            return false;
        }

        var values = await lockdown.GetValueAsync(null, null, ct).ConfigureAwait(false) as PlistDictionary;
        PlistDictionary? battery = null;
        try
        {
            battery = await lockdown.GetValueAsync("com.apple.mobile.battery", null, ct).ConfigureAwait(false) as PlistDictionary;
        }
        catch (LockdownException ex)
        {
            _logger.LogDebug(ex, "Battery domain not available for {Udid}.", entry.Info.Udid);
        }
        try { await lockdown.StopSessionAsync(ct).ConfigureAwait(false); } catch (Exception ex) when (ex is LockdownException or IOException) { }

        var updated = Update(entry, i =>
        {
            var n = values is null ? i : ApplyValues(i, values);
            if (battery is not null)
            {
                n = n with
                {
                    BatteryLevel = (int?)battery.GetInt64("BatteryCurrentCapacity") ?? n.BatteryLevel,
                    BatteryCharging = battery.GetBoolean("BatteryIsCharging") ?? n.BatteryCharging,
                };
            }
            return n with { State = DeviceConnectionState.Connected, Pairing = PairingState.Paired, LastError = null, LastSeen = DateTimeOffset.UtcNow };
        });
        if (_repository is not null)
        {
            try { await _repository.UpsertAsync(updated, ct).ConfigureAwait(false); }
            catch (Exception ex) { _logger.LogWarning(ex, "Saving device {Udid} failed.", updated.Udid); }
        }
        _ = LogEventAsync(updated.Udid, "connected", $"{updated.ConnectionType}, iOS {updated.ProductVersion}");
        return true;
    }

    private static DeviceInfo ApplyValues(DeviceInfo info, PlistDictionary v) => info with
    {
        Name = v.GetString("DeviceName") ?? info.Name,
        ProductType = v.GetString("ProductType") ?? info.ProductType,
        ProductVersion = v.GetString("ProductVersion") ?? info.ProductVersion,
        BuildVersion = v.GetString("BuildVersion") ?? info.BuildVersion,
        DeviceClass = v.GetString("DeviceClass") ?? info.DeviceClass,
        WifiAddress = v.GetString("WiFiAddress") ?? info.WifiAddress,
    };

    // ---------------------------------------------------------------- pairing

    public async Task<PairingState> PairAsync(string udid, CancellationToken cancellationToken = default)
    {
        if (!_entries.TryGetValue(udid, out var entry)) throw new InvalidOperationException($"Device {udid} is not attached.");
        if (entry.AttachmentCount == 0) throw new InvalidOperationException("Device is not attached. Connect it with a USB cable first.");
        await entry.Gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        PairingState state;
        try
        {
            state = await PairCoreAsync(entry, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            entry.Gate.Release();
        }
        if (state == PairingState.Paired) StartInitialize(entry);
        return state;
    }

    private async Task<PairingState> PairCoreAsync(DeviceEntry entry, CancellationToken ct)
    {
        var udid = entry.Info.Udid;
        byte[] devicePublicKey;
        string? wifiMac;
        await using (var lockdown = await OpenLockdownAsync(entry, ct).ConfigureAwait(false))
        {
            devicePublicKey = await lockdown.GetValueAsync(null, "DevicePublicKey", ct).ConfigureAwait(false) as byte[]
                ?? throw new LockdownException("Device did not return DevicePublicKey.");
            wifiMac = await lockdown.GetValueAsync(null, "WiFiAddress", ct).ConfigureAwait(false) as string;
        }

        string buid;
        try { buid = await _usbmux.ReadBuidAsync(ct).ConfigureAwait(false); }
        catch (UsbmuxException) { buid = Guid.NewGuid().ToString("D").ToUpperInvariant(); }

        var record = PairRecordGenerator.Generate(devicePublicKey, buid);
        var deadline = DateTimeOffset.UtcNow + _options.PairingTimeout;
        _ = LogEventAsync(udid, "pairing", "Pair request sent");

        while (!ct.IsCancellationRequested)
        {
            try
            {
                await using var lockdown = await OpenLockdownAsync(entry, ct).ConfigureAwait(false);
                record.EscrowBag = await lockdown.PairAsync(record, ct).ConfigureAwait(false);
                record.WifiMacAddress = wifiMac;
                await _pairStore.SaveAsync(udid, entry.Info.MuxDeviceId, record, ct).ConfigureAwait(false);
                Update(entry, i => i with { Pairing = PairingState.Paired, LastError = null });
                _ = LogEventAsync(udid, "paired", null);
                return PairingState.Paired;
            }
            catch (LockdownException ex) when (ex.IsPairingDialogPending || ex.IsPasswordProtected)
            {
                var state = ex.IsPasswordProtected ? PairingState.DeviceLocked : PairingState.DialogPending;
                var message = ex.IsPasswordProtected
                    ? "Unlock your iPhone to show the Trust dialog."
                    : "Tap \"Trust\" on your iPhone and enter the passcode.";
                Update(entry, i => i with { State = DeviceConnectionState.AwaitingTrust, Pairing = state, LastError = message });
            }
            catch (LockdownException ex) when (ex.IsUserDenied)
            {
                Update(entry, i => i with { State = DeviceConnectionState.AwaitingTrust, Pairing = PairingState.UserDenied, LastError = "Trust was declined on the iPhone." });
                _ = LogEventAsync(udid, "pairing-denied", null);
                return PairingState.UserDenied;
            }

            if (DateTimeOffset.UtcNow > deadline)
            {
                Update(entry, i => i with { Pairing = PairingState.Failed, LastError = "Timed out waiting for \"Trust This Computer\"." });
                return PairingState.Failed;
            }
            await Task.Delay(_options.PairingPollInterval, ct).ConfigureAwait(false);
        }
        return PairingState.Failed;
    }

    // ---------------------------------------------------------------- refresh / disconnect

    public async Task<DeviceInfo?> RefreshAsync(string udid, CancellationToken cancellationToken = default)
    {
        if (!_entries.TryGetValue(udid, out var entry) || entry.AttachmentCount == 0 || entry.UserDisconnected) return Find(udid);
        await InitializeAsync(entry, cancellationToken).ConfigureAwait(false);
        return entry.Info;
    }

    public Task DisconnectAsync(string udid)
    {
        if (_entries.TryGetValue(udid, out var entry))
        {
            entry.UserDisconnected = true;
            entry.CancelWork();
            Update(entry, i => i with { State = DeviceConnectionState.Disconnected, LastError = null });
            _ = LogEventAsync(udid, "disconnected-by-user", null);
        }
        return Task.CompletedTask;
    }

    public Task ReconnectAsync(string udid, CancellationToken cancellationToken = default)
    {
        if (_entries.TryGetValue(udid, out var entry))
        {
            entry.UserDisconnected = false;
            if (entry.AttachmentCount > 0) StartInitialize(entry);
            else Update(entry, i => i with { State = DeviceConnectionState.Reconnecting });
        }
        return Task.CompletedTask;
    }

    private async Task RefreshLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try { await Task.Delay(_options.RefreshInterval, ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { break; }
            foreach (var entry in _entries.Values.Where(e => e.Info.State == DeviceConnectionState.Connected && !e.UserDisconnected))
            {
                try
                {
                    var record = await _pairStore.LoadAsync(entry.Info.Udid, ct).ConfigureAwait(false);
                    if (record is null) continue;
                    await entry.Gate.WaitAsync(ct).ConfigureAwait(false);
                    try { await TryReadSessionInfoAsync(entry, record, ct).ConfigureAwait(false); }
                    finally { entry.Gate.Release(); }
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested) { return; }
                catch (Exception ex)
                {
                    _logger.LogDebug(ex, "Periodic refresh failed for {Udid}.", entry.Info.Udid);
                }
            }
        }
    }

    // ---------------------------------------------------------------- helpers

    private DeviceInfo Update(DeviceEntry entry, Func<DeviceInfo, DeviceInfo> change, DeviceChangeKind kind = DeviceChangeKind.Updated)
    {
        DeviceInfo updated;
        lock (entry)
        {
            updated = change(entry.Info);
            if (updated == entry.Info && kind == DeviceChangeKind.Updated) return updated;
            entry.Info = updated;
        }
        try { DeviceChanged?.Invoke(this, new DeviceChangedEventArgs(updated, kind)); }
        catch (Exception ex) { _logger.LogError(ex, "DeviceChanged handler threw."); }
        return updated;
    }

    private async Task LogEventAsync(string udid, string kind, string? detail)
    {
        _logger.LogInformation("Device {Udid}: {Kind} {Detail}", udid, kind, detail);
        if (_repository is null) return;
        try { await _repository.AddEventAsync(udid, kind, detail).ConfigureAwait(false); }
        catch (Exception ex) { _logger.LogDebug(ex, "Writing connection event failed."); }
    }

    private sealed class DeviceEntry(DeviceInfo info)
    {
        private readonly Dictionary<int, UsbmuxDevice> _attachments = new();
        private CancellationTokenSource? _work;

        public DeviceInfo Info { get; set; } = info;
        public SemaphoreSlim Gate { get; } = new(1, 1);
        public volatile bool UserDisconnected;

        public int AttachmentCount { get { lock (_attachments) return _attachments.Count; } }
        public bool HasAttachment(int id) { lock (_attachments) return _attachments.ContainsKey(id); }
        public void AddAttachment(UsbmuxDevice d) { lock (_attachments) _attachments[d.DeviceId] = d; }
        public void RemoveAttachment(int id) { lock (_attachments) _attachments.Remove(id); }
        public IReadOnlyList<int> AttachmentIds() { lock (_attachments) return _attachments.Keys.ToList(); }

        public UsbmuxDevice? PreferredAttachment()
        {
            lock (_attachments)
            {
                return _attachments.Values.OrderBy(a => a.ConnectionType == ConnectionType.Usb ? 0 : 1).FirstOrDefault();
            }
        }

        public CancellationToken BeginWork(CancellationToken parent)
        {
            CancelWork();
            var cts = CancellationTokenSource.CreateLinkedTokenSource(parent);
            _work = cts;
            return cts.Token;
        }

        public void CancelWork()
        {
            var w = Interlocked.Exchange(ref _work, null);
            if (w is null) return;
            // Not disposed: in-flight operations may still observe the token.
            try { w.Cancel(); } catch (ObjectDisposedException) { }
        }
    }
}
