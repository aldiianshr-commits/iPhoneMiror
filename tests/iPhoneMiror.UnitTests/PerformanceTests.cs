using System.Diagnostics;
using iPhoneMiror.Core.Plist;
using iPhoneMiror.Input.Mapping;
using iPhoneMiror.Mirroring.Mjpeg;
using Xunit.Abstractions;

namespace iPhoneMiror.UnitTests;

/// <summary>
/// Throughput and allocation checks for the hot paths of a mirroring session. Thresholds are deliberately lenient
/// (shared CI runners); the measured numbers are written to the test output.
/// </summary>
public class PerformanceTests(ITestOutputHelper output)
{
    [Fact]
    public async Task Mjpeg_ParsesWellAboveSixtyFramesPerSecond()
    {
        // 300 frames of ~150 KB ≈ a 1170x2532 JPEG at medium quality.
        var frames = Enumerable.Range(0, 300).Select(i =>
        {
            var f = new byte[150_000];
            new Random(i).NextBytes(f);
            f[0] = 0xFF; f[1] = 0xD8; f[^2] = 0xFF; f[^1] = 0xD9;
            return f;
        }).ToArray();
        var stream = new MemoryStream(MirroringTests.WdaStream(frames));
        var reader = new MjpegStreamReader(stream);

        var allocatedBefore = GC.GetAllocatedBytesForCurrentThread();
        var sw = Stopwatch.StartNew();
        var count = 0;
        while (await reader.ReadFrameAsync(CancellationToken.None) is not null) count++;
        sw.Stop();
        var allocated = GC.GetAllocatedBytesForCurrentThread() - allocatedBefore;

        var fps = count / sw.Elapsed.TotalSeconds;
        var mbps = stream.Length / 1048576.0 / sw.Elapsed.TotalSeconds;
        output.WriteLine($"MJPEG: {count} frames in {sw.ElapsedMilliseconds} ms = {fps:0} fps, {mbps:0} MB/s, allocated {allocated / 1048576.0:0.0} MB ({allocated / (double)stream.Length:0.00} bytes per input byte)");
        Assert.Equal(300, count);
        Assert.True(fps > 600, $"parser too slow: {fps:0} fps");
        Assert.True(allocated < stream.Length * 2, "parser should not copy frames more than once");
    }

    [Fact]
    public void PlistAndGestures_AreCheap()
    {
        var dict = new PlistDictionary { ["MessageType"] = "Attached", ["DeviceID"] = 5L, ["Properties"] = new PlistDictionary { ["SerialNumber"] = "x", ["ConnectionType"] = "USB" } };
        var sw = Stopwatch.StartNew();
        for (var i = 0; i < 5_000; i++) PropertyList.ParseDictionary(PropertyList.ToXml(dict));
        var plistMs = sw.Elapsed.TotalMilliseconds;

        var g = new GestureTranslator();
        var t0 = DateTimeOffset.UtcNow;
        sw.Restart();
        for (var i = 0; i < 100_000; i++)
        {
            g.OnPressed(new(i % 300, i % 600), t0.AddMilliseconds(i * 10));
            g.OnMoved(new(i % 300 + 1, i % 600 + 1));
            g.OnReleased(null, t0.AddMilliseconds(i * 10 + 5));
            g.Flush(t0.AddMilliseconds(i * 10 + 400));
        }
        var gestureMs = sw.Elapsed.TotalMilliseconds;
        output.WriteLine($"plist round-trip: {plistMs / 5000 * 1000:0.0} µs each; gesture translation: {gestureMs / 100000 * 1000:0.00} µs per click");
        Assert.True(plistMs / 5000 < 2, "plist round-trip should take well under 2 ms");
        Assert.True(gestureMs / 100000 < 0.5, "gesture translation should take well under 0.5 ms");
    }
}
