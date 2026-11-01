using System.Text;
using iPhoneMiror.Core.Models;
using iPhoneMiror.Mirroring;
using iPhoneMiror.Mirroring.AirPlay;
using iPhoneMiror.Mirroring.Mjpeg;
using Microsoft.Extensions.Logging.Abstractions;

namespace iPhoneMiror.UnitTests;

public class MirroringTests
{
    /// <summary>Minimal JPEG-like payload: SOI, SOF0 with the given size, EOI.</summary>
    internal static byte[] FakeJpeg(int width, int height, byte fill = 0x11)
    {
        return
        [
            0xFF, 0xD8,
            0xFF, 0xE0, 0x00, 0x04, fill, fill,
            0xFF, 0xC0, 0x00, 0x0B, 0x08, (byte)(height >> 8), (byte)height, (byte)(width >> 8), (byte)width, 0x01, 0x01, 0x11, 0x00,
            0xFF, 0xD9,
        ];
    }

    internal static byte[] WdaStream(params byte[][] frames)
    {
        var ms = new MemoryStream();
        ms.Write(Encoding.ASCII.GetBytes("HTTP/1.0 200 OK\r\nServer: WDA MJPEG Server\r\nConnection: close\r\nMax-Age: 0\r\nExpires: 0\r\nCache-Control: no-cache, private\r\nPragma: no-cache\r\nContent-Type: multipart/x-mixed-replace; boundary=--BoundaryString\r\n\r\n"));
        foreach (var f in frames)
        {
            ms.Write(Encoding.ASCII.GetBytes($"--BoundaryString\r\nContent-type: image/jpeg\r\nContent-Length: {f.Length}\r\n\r\n"));
            ms.Write(f);
        }
        return ms.ToArray();
    }

    private sealed class TrickleStream(byte[] data, int chunk) : Stream
    {
        private int _pos;
        public override int Read(byte[] buffer, int offset, int count)
        {
            var n = Math.Min(Math.Min(chunk, count), data.Length - _pos);
            Array.Copy(data, _pos, buffer, offset, n);
            _pos += n;
            return n;
        }
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => data.Length;
        public override long Position { get => _pos; set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    [Theory]
    [InlineData(1)]
    [InlineData(7)]
    [InlineData(4096)]
    public async Task Mjpeg_ReadsWdaFormattedFrames(int chunk)
    {
        var frames = new[] { FakeJpeg(390, 844, 1), FakeJpeg(844, 390, 2), FakeJpeg(1170, 2532, 3) };
        var reader = new MjpegStreamReader(new TrickleStream(WdaStream(frames), chunk));
        for (var i = 0; i < frames.Length; i++)
        {
            Assert.Equal(frames[i], await reader.ReadFrameAsync(CancellationToken.None));
        }
        Assert.Null(await reader.ReadFrameAsync(CancellationToken.None));
        Assert.StartsWith("multipart/x-mixed-replace", reader.ContentType);
    }

    [Fact]
    public async Task Mjpeg_PartsWithoutContentLength_UseEoi()
    {
        var f = FakeJpeg(10, 20);
        var data = Encoding.ASCII.GetBytes("HTTP/1.1 200 OK\r\nContent-Type: multipart/x-mixed-replace; boundary=x\r\n\r\n--x\r\nContent-Type: image/jpeg\r\n\r\n")
            .Concat(f).Concat(Encoding.ASCII.GetBytes("\r\n--x\r\nContent-Type: image/jpeg\r\n\r\n")).Concat(f).ToArray();
        var reader = new MjpegStreamReader(new MemoryStream(data));
        Assert.Equal(f, await reader.ReadFrameAsync(CancellationToken.None));
        Assert.Equal(f, await reader.ReadFrameAsync(CancellationToken.None));
    }

    [Fact]
    public async Task Mjpeg_RejectsErrorStatus()
    {
        var reader = new MjpegStreamReader(new MemoryStream(Encoding.ASCII.GetBytes("HTTP/1.1 404 Not Found\r\n\r\n")));
        await Assert.ThrowsAsync<InvalidDataException>(() => reader.ReadFrameAsync(CancellationToken.None));
    }

    [Fact]
    public void JpegInfo_ReadsSize()
    {
        Assert.Equal((1170, 2532), JpegInfo.TryReadSize(FakeJpeg(1170, 2532)));
        Assert.Null(JpegInfo.TryReadSize([1, 2, 3]));
    }

    [Fact]
    public void FrameRateMeter_CountsWithinWindow()
    {
        var meter = new FrameRateMeter(TimeSpan.FromSeconds(1));
        var t0 = DateTimeOffset.UtcNow;
        for (var i = 0; i < 30; i++) meter.Add(t0.AddMilliseconds(i * 33));
        Assert.Equal(30, meter.GetFps(t0.AddMilliseconds(990)), 0);
        Assert.Equal(0, meter.GetFps(t0.AddSeconds(5)));
    }

    [Fact]
    public void UxPlay_ParsesConnectionLines()
    {
        var r = new UxPlayReceiver(NullLogger.Instance);
        var events = new List<AirPlayReceiverEvent>();
        r.EventRaised += (_, e) => events.Add(e);
        r.OnLine("connection request from My iPhone (iPhone17,1) with deviceID = AA:BB:CC:DD:EE:FF");
        r.OnLine("*** ERROR lost connection with client (network problem?)");
        r.OnLine("random log");
        Assert.Equal(AirPlayReceiverEventKind.ClientConnected, events[0].Kind);
        Assert.Equal("My iPhone", events[0].ClientName);
        Assert.Equal("iPhone17,1", events[0].ClientModel);
        Assert.Equal(AirPlayReceiverEventKind.ClientDisconnected, events[1].Kind);
        Assert.Equal(AirPlayReceiverEventKind.Log, events[2].Kind);
        Assert.Equal(["-n", "Room", "-nh"], UxPlayReceiver.BuildArguments("Room"));
    }
}
