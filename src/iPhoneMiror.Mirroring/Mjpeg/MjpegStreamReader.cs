using System.Globalization;
using System.Text;

namespace iPhoneMiror.Mirroring.Mjpeg;

/// <summary>
/// Parses an HTTP "multipart/x-mixed-replace" MJPEG stream (as served by WebDriverAgent's MJPEG server on port 9100):
/// an HTTP response header, then parts of the form "--Boundary\r\nContent-type: image/jpeg\r\nContent-Length: N\r\n\r\n&lt;N bytes&gt;".
/// Parts without Content-Length are delimited by the JPEG end-of-image marker.
/// </summary>
public sealed class MjpegStreamReader
{
    private const int MaxHeaderBytes = 16 * 1024;
    private const int MaxFrameBytes = 32 * 1024 * 1024;
    private readonly Stream _stream;
    private readonly byte[] _buffer = new byte[64 * 1024];
    private int _start;
    private int _end;
    private bool _httpHeaderRead;

    public MjpegStreamReader(Stream stream, bool expectHttpResponseHeader = true)
    {
        _stream = stream;
        _httpHeaderRead = !expectHttpResponseHeader;
    }

    public string? ContentType { get; private set; }

    /// <summary>Returns the next JPEG frame, or null at end of stream.</summary>
    public async Task<byte[]?> ReadFrameAsync(CancellationToken ct)
    {
        if (!_httpHeaderRead)
        {
            var header = await ReadHeaderBlockAsync(ct).ConfigureAwait(false);
            if (header is null) return null;
            var lines = header.Split("\r\n");
            if (!lines[0].StartsWith("HTTP/", StringComparison.Ordinal))
                throw new InvalidDataException($"Unexpected MJPEG response: {lines[0]}");
            var parts = lines[0].Split(' ');
            if (parts.Length < 2 || parts[1] != "200") throw new InvalidDataException($"MJPEG server returned: {lines[0]}");
            ContentType = lines.Skip(1).Select(ParseHeader).FirstOrDefault(h => h.Name == "content-type").Value;
            _httpHeaderRead = true;
        }

        while (true)
        {
            // Skip anything until a boundary line ("--...") or a raw JPEG start.
            if (!await EnsureAsync(2, ct).ConfigureAwait(false)) return null;
            if (_buffer[_start] == 0xFF && _buffer[_start + 1] == 0xD8)
            {
                return await ReadUntilEoiAsync(ct).ConfigureAwait(false);
            }
            if (_buffer[_start] == (byte)'-' && _buffer[_start + 1] == (byte)'-')
            {
                var partHeader = await ReadHeaderBlockAsync(ct).ConfigureAwait(false);
                if (partHeader is null) return null;
                var headers = partHeader.Split("\r\n").Skip(1).Select(ParseHeader).ToList();
                var lengthText = headers.FirstOrDefault(h => h.Name == "content-length").Value;
                if (lengthText is not null && int.TryParse(lengthText, NumberStyles.None, CultureInfo.InvariantCulture, out var length))
                {
                    if (length <= 0 || length > MaxFrameBytes) throw new InvalidDataException($"Invalid MJPEG part length {length}.");
                    return await ReadExactAsync(length, ct).ConfigureAwait(false);
                }
                continue; // no length: next loop iteration finds the JPEG SOI.
            }
            _start++;
        }
    }

    private static (string Name, string? Value) ParseHeader(string line)
    {
        var i = line.IndexOf(':');
        return i <= 0 ? (line.Trim().ToLowerInvariant(), null) : (line[..i].Trim().ToLowerInvariant(), line[(i + 1)..].Trim());
    }

    /// <summary>Reads bytes up to and including "\r\n\r\n" and returns them as text without the terminator.</summary>
    private async Task<string?> ReadHeaderBlockAsync(CancellationToken ct)
    {
        var scanFrom = _start;
        while (true)
        {
            for (var i = Math.Max(scanFrom, _start); i + 3 < _end; i++)
            {
                if (_buffer[i] == '\r' && _buffer[i + 1] == '\n' && _buffer[i + 2] == '\r' && _buffer[i + 3] == '\n')
                {
                    var text = Encoding.ASCII.GetString(_buffer, _start, i - _start);
                    _start = i + 4;
                    return text;
                }
            }
            scanFrom = Math.Max(_start, _end - 3);
            if (_end - _start > MaxHeaderBytes) throw new InvalidDataException("MJPEG header too large.");
            if (!await FillAsync(ct).ConfigureAwait(false)) return null;
        }
    }

    private async Task<byte[]?> ReadExactAsync(int length, CancellationToken ct)
    {
        var frame = new byte[length];
        var copied = 0;
        while (copied < length)
        {
            if (_start == _end && !await FillAsync(ct).ConfigureAwait(false)) return null;
            var n = Math.Min(length - copied, _end - _start);
            Buffer.BlockCopy(_buffer, _start, frame, copied, n);
            _start += n;
            copied += n;
        }
        return frame;
    }

    private async Task<byte[]?> ReadUntilEoiAsync(CancellationToken ct)
    {
        using var ms = new MemoryStream();
        var prev = -1;
        while (true)
        {
            if (_start == _end && !await FillAsync(ct).ConfigureAwait(false)) return null;
            var b = _buffer[_start++];
            ms.WriteByte(b);
            if (prev == 0xFF && b == 0xD9) return ms.ToArray();
            if (ms.Length > MaxFrameBytes) throw new InvalidDataException("MJPEG frame too large.");
            prev = b;
        }
    }

    private async Task<bool> EnsureAsync(int count, CancellationToken ct)
    {
        while (_end - _start < count)
        {
            if (!await FillAsync(ct).ConfigureAwait(false)) return false;
        }
        return true;
    }

    private async Task<bool> FillAsync(CancellationToken ct)
    {
        if (_start > 0)
        {
            Buffer.BlockCopy(_buffer, _start, _buffer, 0, _end - _start);
            _end -= _start;
            _start = 0;
        }
        if (_end == _buffer.Length) throw new InvalidDataException("MJPEG buffer overflow.");
        var n = await _stream.ReadAsync(_buffer.AsMemory(_end), ct).ConfigureAwait(false);
        if (n == 0) return false;
        _end += n;
        return true;
    }
}
