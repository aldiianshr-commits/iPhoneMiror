using System.Globalization;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using iPhoneMiror.Core.Abstractions;
using iPhoneMiror.Core.Models;

namespace iPhoneMiror.Input.Wda;

public sealed class WdaException : IOException
{
    public WdaException(string message, string? error = null) : base(message) { Error = error; }
    public WdaException(string message, Exception inner) : base(message, inner) { }
    public string? Error { get; }
}

public sealed record WdaStatus(bool Ready, string? State, string? IosVersion, string? SdkVersion, string? Message);

/// <summary>
/// HTTP client for WebDriverAgent (appium/WebDriverAgent, BSD-3-Clause) running on the iPhone.
/// Requests are carried over a usbmux tunnel (USB) or Wi-Fi via <see cref="IDeviceTransport"/>.
/// Routes verified against WebDriverAgentLib/Commands/*.m.
/// </summary>
public sealed class WdaClient : IAsyncDisposable
{
    private readonly HttpClient _http;
    private string? _sessionId;

    public WdaClient(IDeviceTransport transport, DeviceInfo device, int port = 8100, TimeSpan? timeout = null)
        : this(CreateHandler(transport, device, port), port, timeout)
    {
    }

    /// <summary>For tests: use an arbitrary handler.</summary>
    internal WdaClient(HttpMessageHandler handler, int port, TimeSpan? timeout = null)
    {
        _http = new HttpClient(handler, disposeHandler: true)
        {
            BaseAddress = new Uri($"http://127.0.0.1:{port}/"),
            Timeout = timeout ?? TimeSpan.FromSeconds(20),
        };
    }

    private static SocketsHttpHandler CreateHandler(IDeviceTransport transport, DeviceInfo device, int port) => new()
    {
        ConnectCallback = async (_, ct) => await transport.OpenAsync(device, port, ct).ConfigureAwait(false),
        PooledConnectionLifetime = TimeSpan.FromMinutes(2),
        MaxConnectionsPerServer = 2,
    };

    public string? SessionId => _sessionId;

    public async Task<WdaStatus> GetStatusAsync(CancellationToken ct = default)
    {
        var value = await SendAsync(HttpMethod.Get, "status", null, ct).ConfigureAwait(false);
        var ios = value?["ios"];
        var build = value?["build"];
        return new WdaStatus(
            value?["ready"]?.GetValue<bool>() ?? true,
            value?["state"]?.GetValue<string>(),
            ios?["platformVersion"]?.GetValue<string>(),
            ios?["sdkVersion"]?.GetValue<string>() ?? build?["version"]?.ToString(),
            value?["message"]?.GetValue<string>());
    }

    public async Task<string> CreateSessionAsync(CancellationToken ct = default)
    {
        var body = new JsonObject { ["capabilities"] = new JsonObject { ["alwaysMatch"] = new JsonObject() } };
        using var response = await _http.PostAsJsonAsync("session", body, ct).ConfigureAwait(false);
        var root = await ReadRootAsync(response, "POST session", ct).ConfigureAwait(false);
        _sessionId = root?["sessionId"]?.GetValue<string>() ?? root?["value"]?["sessionId"]?.GetValue<string>()
            ?? throw new WdaException("WebDriverAgent did not return a session id.");
        return _sessionId;
    }

    private async Task<string> EnsureSessionAsync(CancellationToken ct) => _sessionId ?? await CreateSessionAsync(ct).ConfigureAwait(false);

    public async Task<(double Width, double Height)> GetWindowSizeAsync(CancellationToken ct = default)
    {
        var value = await SendAsync(HttpMethod.Get, "window/size", null, ct).ConfigureAwait(false);
        return (value?["width"]?.GetValue<double>() ?? 0, value?["height"]?.GetValue<double>() ?? 0);
    }

    public async Task<string?> GetOrientationAsync(CancellationToken ct = default)
    {
        var value = await SendAsync(HttpMethod.Get, "orientation", null, ct).ConfigureAwait(false);
        return value?.GetValue<string>();
    }

    public Task TapAsync(double x, double y, CancellationToken ct = default) =>
        SessionPostAsync("wda/tap", new JsonObject { ["x"] = x, ["y"] = y }, ct);

    public Task DoubleTapAsync(double x, double y, CancellationToken ct = default) =>
        SessionPostAsync("wda/doubleTap", new JsonObject { ["x"] = x, ["y"] = y }, ct);

    public Task TouchAndHoldAsync(double x, double y, TimeSpan duration, CancellationToken ct = default) =>
        SessionPostAsync("wda/touchAndHold", new JsonObject { ["x"] = x, ["y"] = y, ["duration"] = duration.TotalSeconds }, ct);

    /// <summary>Swipe/drag using W3C touch actions (pointerType "touch" is required by WebDriverAgent).</summary>
    public Task PerformSwipeAsync(double fromX, double fromY, double toX, double toY, TimeSpan duration, CancellationToken ct = default) =>
        SessionPostAsync("actions", BuildSwipeActions(fromX, fromY, toX, toY, duration), ct);

    internal static JsonObject BuildSwipeActions(double fromX, double fromY, double toX, double toY, TimeSpan duration) => new()
    {
        ["actions"] = new JsonArray
        {
            new JsonObject
            {
                ["type"] = "pointer",
                ["id"] = "finger1",
                ["parameters"] = new JsonObject { ["pointerType"] = "touch" },
                ["actions"] = new JsonArray
                {
                    new JsonObject { ["type"] = "pointerMove", ["duration"] = 0, ["x"] = Math.Round(fromX, 1), ["y"] = Math.Round(fromY, 1) },
                    new JsonObject { ["type"] = "pointerDown", ["button"] = 0 },
                    new JsonObject { ["type"] = "pause", ["duration"] = 30 },
                    new JsonObject { ["type"] = "pointerMove", ["duration"] = (int)Math.Clamp(duration.TotalMilliseconds, 50, 5000), ["x"] = Math.Round(toX, 1), ["y"] = Math.Round(toY, 1) },
                    new JsonObject { ["type"] = "pointerUp", ["button"] = 0 },
                },
            },
        },
    };

    public Task TypeAsync(string text, CancellationToken ct = default) =>
        SessionPostAsync("wda/keys", new JsonObject { ["value"] = new JsonArray(text.EnumerateRunes().Select(r => (JsonNode)r.ToString()).ToArray()) }, ct);

    public async Task HomeAsync(CancellationToken ct = default) =>
        await SendAsync(HttpMethod.Post, "wda/homescreen", new JsonObject(), ct).ConfigureAwait(false);

    /// <summary>Button names accepted by WebDriverAgent: home, volumeup, volumedown (case-insensitive).</summary>
    public Task PressButtonAsync(string name, CancellationToken ct = default) =>
        SessionPostAsync("wda/pressButton", new JsonObject { ["name"] = name }, ct);

    /// <summary>Applies MJPEG stream settings (setting keys from WebDriverAgentLib/Utilities/FBSettings.m).</summary>
    public Task SetMjpegSettingsAsync(int frameRate, int quality, int scalingPercent, CancellationToken ct = default) =>
        SessionPostAsync("appium/settings", new JsonObject
        {
            ["settings"] = new JsonObject
            {
                ["mjpegServerFramerate"] = Math.Clamp(frameRate, 1, 60),
                ["mjpegServerScreenshotQuality"] = Math.Clamp(quality, 1, 100),
                ["mjpegScalingFactor"] = Math.Clamp(scalingPercent, 1, 100),
            },
        }, ct);

    private async Task SessionPostAsync(string path, JsonObject body, CancellationToken ct)
    {
        var sid = await EnsureSessionAsync(ct).ConfigureAwait(false);
        try
        {
            await SendAsync(HttpMethod.Post, $"session/{sid}/{path}", body, ct).ConfigureAwait(false);
        }
        catch (WdaException ex) when (ex.Error is "invalid session id")
        {
            _sessionId = null;
            sid = await EnsureSessionAsync(ct).ConfigureAwait(false);
            await SendAsync(HttpMethod.Post, $"session/{sid}/{path}", body, ct).ConfigureAwait(false);
        }
    }

    private async Task<JsonNode?> SendAsync(HttpMethod method, string path, JsonObject? body, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(method, path);
        if (body is not null) request.Content = JsonContent.Create(body);
        HttpResponseMessage response;
        try
        {
            response = await _http.SendAsync(request, ct).ConfigureAwait(false);
        }
        catch (HttpRequestException ex)
        {
            throw new WdaException($"WebDriverAgent is not reachable ({method} /{path}). Make sure WebDriverAgent is running on the iPhone.", ex);
        }
        using (response)
        {
            var root = await ReadRootAsync(response, $"{method} /{path}", ct).ConfigureAwait(false);
            return root?["value"];
        }
    }

    private static async Task<JsonNode?> ReadRootAsync(HttpResponseMessage response, string what, CancellationToken ct)
    {
        var text = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        JsonNode? root = null;
        try { root = string.IsNullOrWhiteSpace(text) ? null : JsonNode.Parse(text); }
        catch (JsonException) { }

        var value = root?["value"];
        var error = value is JsonObject vo ? vo["error"]?.GetValue<string>() : null;
        if (!response.IsSuccessStatusCode || error is not null)
        {
            var message = value is JsonObject mo ? mo["message"]?.GetValue<string>() : null;
            throw new WdaException(string.Create(CultureInfo.InvariantCulture,
                $"WebDriverAgent {what} failed: HTTP {(int)response.StatusCode} {error} {message}".Trim()), error);
        }
        return root;
    }

    public ValueTask DisposeAsync()
    {
        _http.Dispose();
        return ValueTask.CompletedTask;
    }
}
