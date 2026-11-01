using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using iPhoneMiror.Core.Abstractions;
using iPhoneMiror.Input.Mapping;
using iPhoneMiror.Input.Wda;

namespace iPhoneMiror.UnitTests;

public class InputTests
{
    [Fact]
    public void Mapper_CentredLetterbox()
    {
        // 1000x1000 view, portrait 500x1000 image -> shown 500x1000 at x offset 250.
        var p = CoordinateMapper.ViewToDevice(500, 500, 1000, 1000, 500, 1000, 390, 844);
        Assert.Equal(new DevicePoint(195, 422), p);
        Assert.Null(CoordinateMapper.ViewToDevice(100, 500, 1000, 1000, 500, 1000, 390, 844));
        Assert.Equal(new DevicePoint(0, 0), CoordinateMapper.ViewToDevice(250, 0, 1000, 1000, 500, 1000, 390, 844));
        Assert.Equal(new DevicePoint(390, 844), CoordinateMapper.ViewToDevice(750, 1000, 1000, 1000, 500, 1000, 390, 844));
    }

    [Fact]
    public void Mapper_SwapsDeviceSizeForLandscapeFrame()
    {
        var p = CoordinateMapper.ViewToDevice(844, 390, 844, 390, 1688, 780, 390, 844);
        Assert.Equal(new DevicePoint(844, 390), p);
    }

    private static readonly DateTimeOffset T0 = new(2026, 11, 1, 0, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Gestures_SingleTapIsDeferredUntilFlush()
    {
        var g = new GestureTranslator();
        g.OnPressed(new DevicePoint(10, 10), T0);
        Assert.Empty(g.OnReleased(new DevicePoint(11, 10), T0.AddMilliseconds(80)));
        Assert.Empty(g.Flush(T0.AddMilliseconds(200)));
        var flushed = g.Flush(T0.AddMilliseconds(500));
        Assert.Equal(new TapGesture(new DevicePoint(10, 10)), Assert.Single(flushed));
    }

    [Fact]
    public void Gestures_DoubleTap()
    {
        var g = new GestureTranslator();
        g.OnPressed(new DevicePoint(50, 50), T0);
        g.OnReleased(null, T0.AddMilliseconds(60));
        g.OnPressed(new DevicePoint(52, 51), T0.AddMilliseconds(150));
        var result = g.OnReleased(null, T0.AddMilliseconds(200));
        Assert.Equal(new DoubleTapGesture(new DevicePoint(50, 50)), Assert.Single(result));
        Assert.Empty(g.Flush(T0.AddSeconds(2)));
    }

    [Fact]
    public void Gestures_LongPressAndSwipe()
    {
        var g = new GestureTranslator(new GestureOptions { DetectDoubleTap = false });
        g.OnPressed(new DevicePoint(5, 5), T0);
        var lp = Assert.IsType<LongPressGesture>(Assert.Single(g.OnReleased(null, T0.AddMilliseconds(800))));
        Assert.Equal(TimeSpan.FromMilliseconds(800), lp.Duration);

        g.OnPressed(new DevicePoint(100, 600), T0);
        g.OnMoved(new DevicePoint(100, 300));
        var sw = Assert.IsType<SwipeGesture>(Assert.Single(g.OnReleased(null, T0.AddMilliseconds(250))));
        Assert.Equal(new DevicePoint(100, 600), sw.From);
        Assert.Equal(new DevicePoint(100, 300), sw.To);
    }

    [Fact]
    public void Gestures_WheelUpSwipesFingerDown()
    {
        var g = new GestureTranslator { DeviceSize = (390, 844) };
        Assert.Empty(g.OnWheel(120, new DevicePoint(200, 400), T0));
        Assert.Empty(g.OnWheel(120, new DevicePoint(200, 400), T0.AddMilliseconds(20)));
        var sw = Assert.IsType<SwipeGesture>(Assert.Single(g.Flush(T0.AddMilliseconds(200))));
        Assert.True(sw.To.Y > sw.From.Y);
        Assert.Equal(240, sw.To.Y - sw.From.Y, 1);

        g.OnWheel(-120, new DevicePoint(200, 400), T0.AddSeconds(1));
        var down = Assert.IsType<SwipeGesture>(Assert.Single(g.Flush(T0.AddSeconds(2))));
        Assert.True(down.To.Y < down.From.Y);
    }

    [Theory]
    [InlineData(VirtualKeys.Back, false, false, KeyAction.TypeText, "\b")]
    [InlineData(VirtualKeys.Enter, false, false, KeyAction.TypeText, "\n")]
    [InlineData(VirtualKeys.H, true, true, KeyAction.Home, null)]
    [InlineData(VirtualKeys.H, false, false, KeyAction.None, null)]
    public void Keyboard_SpecialKeys(int vk, bool ctrl, bool shift, KeyAction action, string? text)
    {
        var t = KeyboardTranslator.TranslateKey(vk, ctrl, shift, false);
        Assert.Equal(action, t.Action);
        Assert.Equal(text, t.Text);
        Assert.Equal("é", KeyboardTranslator.TranslateCharacter('é'));
        Assert.Null(KeyboardTranslator.TranslateCharacter('\r'));
    }

    private sealed class RecordingHandler : HttpMessageHandler
    {
        public List<(HttpMethod Method, string Path, string? Body)> Requests { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var body = request.Content is null ? null : await request.Content.ReadAsStringAsync(ct);
            var path = request.RequestUri!.AbsolutePath;
            Requests.Add((request.Method, path, body));
            string json = path switch
            {
                "/status" => """{"value":{"ready":true,"state":"success","ios":{"platformVersion":"26.0"}},"sessionId":null}""",
                "/session" => """{"value":{"sessionId":"S1","capabilities":{}},"sessionId":"S1"}""",
                "/window/size" => """{"value":{"width":393,"height":852},"sessionId":"S1"}""",
                _ when path.EndsWith("/fail") => """{"value":{"error":"unknown error","message":"boom"}}""",
                _ => """{"value":null,"sessionId":"S1"}""",
            };
            var status = path.EndsWith("/fail") ? HttpStatusCode.InternalServerError : HttpStatusCode.OK;
            return new HttpResponseMessage(status) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
        }
    }

    [Fact]
    public async Task WdaClient_UsesVerifiedRoutes()
    {
        var h = new RecordingHandler();
        await using var c = new WdaClient(h, 8100);
        var status = await c.GetStatusAsync();
        Assert.Equal("26.0", status.IosVersion);
        Assert.Equal((393d, 852d), await c.GetWindowSizeAsync());
        await c.TapAsync(10.5, 20);
        await c.DoubleTapAsync(1, 2);
        await c.TouchAndHoldAsync(3, 4, TimeSpan.FromSeconds(1));
        await c.TypeAsync("hi");
        await c.HomeAsync();
        await c.PressButtonAsync("volumeup");
        await c.SetMjpegSettingsAsync(30, 60, 75);
        await c.PerformSwipeAsync(1, 2, 3, 4, TimeSpan.FromMilliseconds(200));

        var paths = h.Requests.Select(r => $"{r.Method} {r.Path}").ToList();
        Assert.Contains("POST /session", paths);
        Assert.Contains("POST /session/S1/wda/tap", paths);
        Assert.Contains("POST /session/S1/wda/doubleTap", paths);
        Assert.Contains("POST /session/S1/wda/touchAndHold", paths);
        Assert.Contains("POST /session/S1/wda/keys", paths);
        Assert.Contains("POST /wda/homescreen", paths);
        Assert.Contains("POST /session/S1/wda/pressButton", paths);
        Assert.Contains("POST /session/S1/appium/settings", paths);
        Assert.Contains("POST /session/S1/actions", paths);

        var tap = JsonNode.Parse(h.Requests.First(r => r.Path.EndsWith("/wda/tap")).Body!)!;
        Assert.Equal(10.5, tap["x"]!.GetValue<double>());
        var keys = JsonNode.Parse(h.Requests.First(r => r.Path.EndsWith("/wda/keys")).Body!)!["value"]!.AsArray();
        Assert.Equal(["h", "i"], keys.Select(k => k!.GetValue<string>()));
        var settings = JsonNode.Parse(h.Requests.First(r => r.Path.EndsWith("/appium/settings")).Body!)!["settings"]!;
        Assert.Equal(30, settings["mjpegServerFramerate"]!.GetValue<int>());
        Assert.Equal(60, settings["mjpegServerScreenshotQuality"]!.GetValue<int>());
        Assert.Equal(75, settings["mjpegScalingFactor"]!.GetValue<int>());
    }

    [Fact]
    public void SwipeActions_UseTouchPointer()
    {
        var a = WdaClient.BuildSwipeActions(1, 2, 3, 4, TimeSpan.FromMilliseconds(10));
        var seq = a["actions"]![0]!;
        Assert.Equal("touch", seq["parameters"]!["pointerType"]!.GetValue<string>());
        var steps = seq["actions"]!.AsArray();
        Assert.Equal(["pointerMove", "pointerDown", "pause", "pointerMove", "pointerUp"], steps.Select(s => s!["type"]!.GetValue<string>()));
        Assert.Equal(50, steps[3]!["duration"]!.GetValue<int>()); // clamped minimum
    }
}
