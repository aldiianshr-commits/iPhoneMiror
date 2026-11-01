using iPhoneMiror.Core.Abstractions;
using iPhoneMiror.Core.Models;
using iPhoneMiror.Core.Util;
using iPhoneMiror.Mirroring.AirPlay;
using iPhoneMiror.Mirroring.Wda;
using Microsoft.Extensions.Logging;

namespace iPhoneMiror.Mirroring;

/// <summary>Coordinates one mirroring session at a time.</summary>
public sealed class MirroringService : IMirroringService
{
    private readonly IDeviceConnectionService _devices;
    private readonly IDeviceTransport _transport;
    private readonly IMirrorQualityConfigurator? _quality;
    private readonly Func<string?> _uxPlayPathProvider;
    private readonly Func<string> _airPlayNameProvider;
    private readonly ILogger<MirroringService> _logger;
    private readonly FrameRateMeter _fps = new();
    private readonly SemaphoreSlim _gate = new(1, 1);
    private CancellationTokenSource? _cts;
    private Task? _loop;
    private UxPlayReceiver? _uxplay;
    private long _frames;
    private long _bytes;
    private (int W, int H) _size;

    public MirroringService(
        IDeviceConnectionService devices,
        IDeviceTransport transport,
        ILogger<MirroringService> logger,
        IMirrorQualityConfigurator? quality = null,
        Func<string?>? uxPlayPathProvider = null,
        Func<string>? airPlayNameProvider = null)
    {
        _devices = devices;
        _transport = transport;
        _logger = logger;
        _quality = quality;
        _uxPlayPathProvider = uxPlayPathProvider ?? (() => null);
        _airPlayNameProvider = airPlayNameProvider ?? (() => "iPhoneMiror");
    }

    public MirroringState State { get; private set; } = MirroringState.Idle;
    public string? ActiveDeviceUdid { get; private set; }
    public MirroringBackendKind? ActiveBackend { get; private set; }
    public MirrorFrame? LatestFrame { get; private set; }

    public MirroringStats Stats => new(_fps.GetFps(DateTimeOffset.UtcNow), _size.W, _size.H, Interlocked.Read(ref _frames), Interlocked.Read(ref _bytes), LatestFrame?.ReceivedAt);

    public event EventHandler<MirrorFrame>? FrameReceived;
    public event EventHandler<MirroringStateChangedEventArgs>? StateChanged;

    public async Task StartAsync(string udid, MirroringOptions options, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await StopCoreAsync().ConfigureAwait(false);
            ActiveDeviceUdid = udid;
            ActiveBackend = options.Backend;
            _frames = 0;
            _bytes = 0;
            _fps.Reset();
            LatestFrame = null;
            _cts = new CancellationTokenSource();

            if (options.Backend == MirroringBackendKind.AirPlayUxPlay)
            {
                await StartAirPlayAsync().ConfigureAwait(false);
                return;
            }

            var device = _devices.Find(udid) ?? throw new InvalidOperationException("The selected iPhone is not connected.");
            if (device.MuxDeviceId == 0 && device.NetworkAddress is null)
                throw new InvalidOperationException("The selected iPhone is not attached over USB or Wi-Fi.");
            SetState(MirroringState.Starting, "Connecting to WebDriverAgent MJPEG stream…");
            var token = _cts.Token;
            _loop = Task.Run(() => WdaLoopAsync(udid, options, token), CancellationToken.None);
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task StartAirPlayAsync()
    {
        var exe = UxPlayReceiver.FindExecutable(_uxPlayPathProvider());
        if (exe is null)
        {
            SetState(MirroringState.Error, "UxPlay (uxplay.exe) was not found. Install UxPlay and set its path in Settings > Connection.");
            return;
        }
        _uxplay = new UxPlayReceiver(_logger);
        _uxplay.EventRaised += (_, e) =>
        {
            switch (e.Kind)
            {
                case AirPlayReceiverEventKind.Started: SetState(MirroringState.Starting, e.Message); break;
                case AirPlayReceiverEventKind.ClientConnected: SetState(MirroringState.Streaming, $"AirPlay mirroring from {e.ClientName} ({e.ClientModel}). Video is shown in the UxPlay window."); break;
                case AirPlayReceiverEventKind.ClientDisconnected: SetState(MirroringState.Starting, "AirPlay client disconnected. Waiting for a new connection."); break;
                case AirPlayReceiverEventKind.Exited: if (State != MirroringState.Stopped) SetState(MirroringState.Error, e.Message); break;
            }
        };
        await _uxplay.StartAsync(exe, _airPlayNameProvider()).ConfigureAwait(false);
    }

    private async Task WdaLoopAsync(string udid, MirroringOptions options, CancellationToken ct)
    {
        var policy = new ReconnectPolicy(TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(15));
        var attempt = 0;
        var source = new WdaMjpegSource(_transport);
        while (!ct.IsCancellationRequested)
        {
            try
            {
                var device = _devices.Find(udid) ?? throw new IOException("Device disconnected.");
                if (_quality is not null)
                {
                    try
                    {
                        await _quality.ApplyStreamSettingsAsync(device, options.WdaPort, options.FrameRate, options.Quality, options.ScalingPercent, ct).ConfigureAwait(false);
                    }
                    catch (Exception ex) when (ex is IOException or HttpRequestException or InvalidOperationException)
                    {
                        _logger.LogInformation(ex, "Could not apply stream settings; using WebDriverAgent defaults.");
                    }
                }

                await foreach (var jpeg in source.ReadFramesAsync(device, options.MjpegPort, ct).ConfigureAwait(false))
                {
                    var now = DateTimeOffset.UtcNow;
                    var frame = new MirrorFrame(jpeg, Interlocked.Increment(ref _frames), now);
                    Interlocked.Add(ref _bytes, jpeg.Length);
                    _fps.Add(now);
                    if (frame.TryGetPixelSize() is { } size) _size = size;
                    LatestFrame = frame;
                    if (State != MirroringState.Streaming) SetState(MirroringState.Streaming, $"Streaming from {device.DisplayName}.");
                    attempt = 0;
                    try { FrameReceived?.Invoke(this, frame); }
                    catch (Exception ex) { _logger.LogError(ex, "FrameReceived handler threw."); }
                }
                throw new IOException("The MJPEG stream ended.");
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                _logger.LogInformation(ex, "Mirroring stream interrupted.");
                if (!options.AutoReconnect)
                {
                    SetState(MirroringState.Error, Describe(ex));
                    return;
                }
                SetState(MirroringState.Reconnecting, Describe(ex) + " Reconnecting…");
                try { await Task.Delay(policy.GetDelay(attempt++), ct).ConfigureAwait(false); }
                catch (OperationCanceledException) { return; }
            }
        }
    }

    private static string Describe(Exception ex) => ex switch
    {
        IOException io when io.Message.Contains("ConnectionRefused", StringComparison.OrdinalIgnoreCase) || io.Message.Contains("refused", StringComparison.OrdinalIgnoreCase)
            => "WebDriverAgent is not running on the iPhone (MJPEG port refused the connection).",
        _ => ex.Message,
    };

    public async Task StopAsync()
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            await StopCoreAsync().ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task StopCoreAsync()
    {
        var hadSession = _cts is not null || _uxplay is not null;
        if (_cts is not null)
        {
            await _cts.CancelAsync().ConfigureAwait(false);
            if (_loop is not null)
            {
                try { await _loop.ConfigureAwait(false); } catch (OperationCanceledException) { }
            }
            _cts.Dispose();
            _cts = null;
            _loop = null;
        }
        if (_uxplay is not null)
        {
            await _uxplay.DisposeAsync().ConfigureAwait(false);
            _uxplay = null;
        }
        if (hadSession)
        {
            ActiveBackend = null;
            SetState(MirroringState.Stopped, "Mirroring stopped.");
        }
    }

    private void SetState(MirroringState state, string? message)
    {
        State = state;
        try { StateChanged?.Invoke(this, new MirroringStateChangedEventArgs(state, message)); }
        catch (Exception ex) { _logger.LogError(ex, "StateChanged handler threw."); }
    }

    public async ValueTask DisposeAsync() => await StopAsync().ConfigureAwait(false);
}
