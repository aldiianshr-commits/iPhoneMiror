using iPhoneMiror.Core.Abstractions;
using iPhoneMiror.Core.Models;
using iPhoneMiror.Input.Wda;
using Microsoft.Extensions.Logging;

namespace iPhoneMiror.Input;

/// <summary>
/// Forwards input to the iPhone through WebDriverAgent. Commands are serialized; any failure marks input unavailable
/// with the reason so the UI never pretends input was delivered.
/// </summary>
public sealed class WdaInputForwarder : IInputForwarder, IMirrorQualityConfigurator
{
    private readonly IDeviceTransport _transport;
    private readonly ILogger<WdaInputForwarder> _logger;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private WdaClient? _client;
    private string? _udid;
    private int _port;

    public WdaInputForwarder(IDeviceTransport transport, ILogger<WdaInputForwarder> logger)
    {
        _transport = transport;
        _logger = logger;
    }

    /// <summary>Factory override for tests.</summary>
    internal Func<DeviceInfo, int, WdaClient>? ClientFactory { get; set; }

    public InputAvailability Availability { get; private set; } = InputAvailability.Unknown;
    public event EventHandler<InputAvailability>? AvailabilityChanged;
    public (double Width, double Height)? ScreenSizePoints { get; private set; }

    private WdaClient Create(DeviceInfo device, int port) => ClientFactory?.Invoke(device, port) ?? new WdaClient(_transport, device, port);

    public async Task<InputAvailability> ConnectAsync(DeviceInfo device, int wdaPort, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_client is not null) await _client.DisposeAsync().ConfigureAwait(false);
            _client = Create(device, wdaPort);
            _udid = device.Udid;
            _port = wdaPort;
            var status = await _client.GetStatusAsync(cancellationToken).ConfigureAwait(false);
            await _client.CreateSessionAsync(cancellationToken).ConfigureAwait(false);
            var size = await _client.GetWindowSizeAsync(cancellationToken).ConfigureAwait(false);
            if (size.Width > 0 && size.Height > 0) ScreenSizePoints = size;
            SetAvailability(true, $"Input via WebDriverAgent (iOS {status.IosVersion ?? "?"}), screen {size.Width:0}x{size.Height:0} pt.");
        }
        catch (Exception ex) when (ex is WdaException or IOException or HttpRequestException or TaskCanceledException)
        {
            _logger.LogInformation(ex, "WebDriverAgent not available for {Udid}.", device.Udid);
            SetAvailability(false, "Input control needs WebDriverAgent running on the iPhone: " + ex.Message);
        }
        finally
        {
            _gate.Release();
        }
        return Availability;
    }

    public async Task DisconnectAsync()
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_client is not null) await _client.DisposeAsync().ConfigureAwait(false);
            _client = null;
            _udid = null;
            SetAvailability(false, "Disconnected.");
        }
        finally
        {
            _gate.Release();
        }
    }

    private void SetAvailability(bool available, string message)
    {
        Availability = new InputAvailability(available, message, DateTimeOffset.UtcNow);
        AvailabilityChanged?.Invoke(this, Availability);
    }

    private async Task RunAsync(string what, Func<WdaClient, Task> action, CancellationToken ct)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var client = _client ?? throw new InvalidOperationException("Input forwarding is not connected.");
            await action(client).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is WdaException or IOException or HttpRequestException)
        {
            _logger.LogWarning(ex, "Input command {What} failed.", what);
            SetAvailability(false, $"{what} failed: {ex.Message}");
            throw;
        }
        finally
        {
            _gate.Release();
        }
    }

    public Task TapAsync(DevicePoint point, CancellationToken cancellationToken = default) =>
        RunAsync("Tap", c => c.TapAsync(point.X, point.Y, cancellationToken), cancellationToken);

    public Task DoubleTapAsync(DevicePoint point, CancellationToken cancellationToken = default) =>
        RunAsync("Double tap", c => c.DoubleTapAsync(point.X, point.Y, cancellationToken), cancellationToken);

    public Task LongPressAsync(DevicePoint point, TimeSpan duration, CancellationToken cancellationToken = default) =>
        RunAsync("Long press", c => c.TouchAndHoldAsync(point.X, point.Y, duration, cancellationToken), cancellationToken);

    public Task DragAsync(DevicePoint from, DevicePoint to, TimeSpan duration, CancellationToken cancellationToken = default) =>
        RunAsync("Swipe", c => c.PerformSwipeAsync(from.X, from.Y, to.X, to.Y, duration, cancellationToken), cancellationToken);

    public Task TypeTextAsync(string text, CancellationToken cancellationToken = default) =>
        string.IsNullOrEmpty(text) ? Task.CompletedTask : RunAsync("Typing", c => c.TypeAsync(text, cancellationToken), cancellationToken);

    public Task PressButtonAsync(HardwareButton button, CancellationToken cancellationToken = default) =>
        RunAsync(button.ToString(), c => button switch
        {
            HardwareButton.Home => c.HomeAsync(cancellationToken),
            HardwareButton.VolumeUp => c.PressButtonAsync("volumeup", cancellationToken),
            HardwareButton.VolumeDown => c.PressButtonAsync("volumedown", cancellationToken),
            _ => Task.CompletedTask,
        }, cancellationToken);

    public async Task ApplyStreamSettingsAsync(DeviceInfo device, int wdaPort, int frameRate, int quality, int scalingPercent, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_client is not null && _udid == device.Udid && _port == wdaPort)
            {
                await _client.SetMjpegSettingsAsync(frameRate, quality, scalingPercent, cancellationToken).ConfigureAwait(false);
                return;
            }
            await using var temp = Create(device, wdaPort);
            await temp.SetMjpegSettingsAsync(frameRate, quality, scalingPercent, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_client is not null) await _client.DisposeAsync().ConfigureAwait(false);
        _client = null;
    }
}
