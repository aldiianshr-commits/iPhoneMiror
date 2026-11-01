using System.Text;
using iPhoneMiror.Core.Abstractions;
using iPhoneMiror.Core.Models;
using iPhoneMiror.Mirroring.Mjpeg;

namespace iPhoneMiror.Mirroring.Wda;

/// <summary>Connects to WebDriverAgent's MJPEG server on the device and yields JPEG frames.</summary>
public sealed class WdaMjpegSource
{
    private readonly IDeviceTransport _transport;

    public WdaMjpegSource(IDeviceTransport transport)
    {
        _transport = transport;
    }

    public async IAsyncEnumerable<byte[]> ReadFramesAsync(DeviceInfo device, int port, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
    {
        await using var stream = await _transport.OpenAsync(device, port, ct).ConfigureAwait(false);
        // WebDriverAgent starts streaming after it receives any request bytes from the client.
        var request = Encoding.ASCII.GetBytes("GET / HTTP/1.1\r\nHost: localhost\r\nConnection: keep-alive\r\n\r\n");
        await stream.WriteAsync(request, ct).ConfigureAwait(false);
        await stream.FlushAsync(ct).ConfigureAwait(false);

        var reader = new MjpegStreamReader(stream);
        while (!ct.IsCancellationRequested)
        {
            var frame = await reader.ReadFrameAsync(ct).ConfigureAwait(false);
            if (frame is null) yield break;
            yield return frame;
        }
    }
}
