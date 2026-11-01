namespace iPhoneMiror.Core.Models;

/// <summary>One encoded video frame (JPEG) received from the device.</summary>
public sealed class MirrorFrame
{
    public MirrorFrame(byte[] jpeg, long sequence, DateTimeOffset receivedAt)
    {
        Jpeg = jpeg;
        Sequence = sequence;
        ReceivedAt = receivedAt;
    }

    public byte[] Jpeg { get; }
    public long Sequence { get; }
    public DateTimeOffset ReceivedAt { get; }

    /// <summary>Reads the pixel size from the JPEG SOF marker without decoding the image.</summary>
    public (int Width, int Height)? TryGetPixelSize() => JpegInfo.TryReadSize(Jpeg);
}
