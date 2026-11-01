namespace iPhoneMiror.Core.Models;

public static class JpegInfo
{
    /// <summary>Parses JPEG markers to find the frame dimensions (SOF0..SOF15 excluding DHT/JPG/DAC).</summary>
    public static (int Width, int Height)? TryReadSize(ReadOnlySpan<byte> jpeg)
    {
        if (jpeg.Length < 4 || jpeg[0] != 0xFF || jpeg[1] != 0xD8) return null;
        var i = 2;
        while (i + 3 < jpeg.Length)
        {
            if (jpeg[i] != 0xFF) { i++; continue; }
            var marker = jpeg[i + 1];
            if (marker == 0xFF) { i++; continue; }
            if (marker is 0xD8 or 0x01 || (marker >= 0xD0 && marker <= 0xD7)) { i += 2; continue; }
            if (marker == 0xD9 || marker == 0xDA) return null;
            var len = (jpeg[i + 2] << 8) | jpeg[i + 3];
            if (len < 2) return null;
            var isSof = marker >= 0xC0 && marker <= 0xCF && marker != 0xC4 && marker != 0xC8 && marker != 0xCC;
            if (isSof)
            {
                if (i + 8 >= jpeg.Length) return null;
                var height = (jpeg[i + 5] << 8) | jpeg[i + 6];
                var width = (jpeg[i + 7] << 8) | jpeg[i + 8];
                return width > 0 && height > 0 ? (width, height) : null;
            }
            i += 2 + len;
        }
        return null;
    }
}
