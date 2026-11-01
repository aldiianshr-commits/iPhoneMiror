using System.Collections.Concurrent;
using System.Globalization;
using System.Text;

namespace iPhoneMiror.IntegrationTests.Fakes;

/// <summary>Minimal HTTP/1.1 server emulating WebDriverAgent routes, plus its MJPEG server.</summary>
public sealed class FakeWebDriverAgent
{
    public ConcurrentQueue<(string Method, string Path, string Body)> Requests { get; } = new();
    public int MjpegConnections;

    public async Task HandleHttpAsync(Stream stream, CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                var requestLine = await ReadLineAsync(stream, ct);
                if (string.IsNullOrEmpty(requestLine)) return;
                var parts = requestLine.Split(' ');
                var length = 0;
                string? line;
                while (!string.IsNullOrEmpty(line = await ReadLineAsync(stream, ct)))
                {
                    var i = line.IndexOf(':');
                    if (i > 0 && line[..i].Trim().Equals("Content-Length", StringComparison.OrdinalIgnoreCase))
                        length = int.Parse(line[(i + 1)..].Trim(), CultureInfo.InvariantCulture);
                }
                var body = new byte[length];
                if (length > 0) await stream.ReadExactlyAsync(body, ct);
                var path = parts[1];
                Requests.Enqueue((parts[0], path, Encoding.UTF8.GetString(body)));

                var json = path switch
                {
                    "/status" => """{"value":{"ready":true,"state":"success","ios":{"platformVersion":"26.0"}},"sessionId":null}""",
                    "/session" => """{"value":{"sessionId":"FAKE-SESSION","capabilities":{}},"sessionId":"FAKE-SESSION"}""",
                    "/window/size" => """{"value":{"width":402,"height":874},"sessionId":"FAKE-SESSION"}""",
                    _ => """{"value":null,"sessionId":"FAKE-SESSION"}""",
                };
                var payload = Encoding.UTF8.GetBytes(json);
                var header = $"HTTP/1.1 200 OK\r\nContent-Type: application/json;charset=UTF-8\r\nContent-Length: {payload.Length}\r\nConnection: keep-alive\r\n\r\n";
                await stream.WriteAsync(Encoding.ASCII.GetBytes(header), ct);
                await stream.WriteAsync(payload, ct);
                await stream.FlushAsync(ct);
            }
        }
        catch (IOException) { }
        catch (OperationCanceledException) { }
    }

    private static async Task<string?> ReadLineAsync(Stream s, CancellationToken ct)
    {
        var sb = new StringBuilder();
        var one = new byte[1];
        while (true)
        {
            var n = await s.ReadAsync(one, ct);
            if (n == 0) return sb.Length == 0 ? null : sb.ToString();
            if (one[0] == '\n') return sb.ToString().TrimEnd('\r');
            sb.Append((char)one[0]);
        }
    }

    /// <summary>Waits for the request, then streams <paramref name="frames"/> frames and closes the connection.</summary>
    public async Task HandleMjpegAsync(Stream stream, int frames, Func<int, byte[]> frameFactory, CancellationToken ct)
    {
        Interlocked.Increment(ref MjpegConnections);
        try
        {
            var buf = new byte[1024];
            _ = await stream.ReadAsync(buf, ct); // client request
            await stream.WriteAsync("HTTP/1.0 200 OK\r\nServer: WDA MJPEG Server\r\nConnection: close\r\nContent-Type: multipart/x-mixed-replace; boundary=--BoundaryString\r\n\r\n"u8.ToArray(), ct);
            for (var i = 0; i < frames; i++)
            {
                var f = frameFactory(i);
                await stream.WriteAsync(Encoding.ASCII.GetBytes($"--BoundaryString\r\nContent-type: image/jpeg\r\nContent-Length: {f.Length}\r\n\r\n"), ct);
                await stream.WriteAsync(f, ct);
                await stream.FlushAsync(ct);
                await Task.Delay(20, ct);
            }
        }
        catch (IOException) { }
        catch (OperationCanceledException) { }
    }
}
