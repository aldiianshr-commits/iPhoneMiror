using iPhoneMiror.Core.Models;

namespace iPhoneMiror.Core.Abstractions;

public sealed record MirroringOptions
{
    public MirroringBackendKind Backend { get; init; } = MirroringBackendKind.WebDriverAgent;
    /// <summary>Frames per second requested from the device (1-60).</summary>
    public int FrameRate { get; init; } = 30;
    /// <summary>JPEG quality requested from the device (1-100).</summary>
    public int Quality { get; init; } = 60;
    /// <summary>Scaling percentage applied on the device before encoding (1-100).</summary>
    public int ScalingPercent { get; init; } = 75;
    public int WdaPort { get; init; } = 8100;
    public int MjpegPort { get; init; } = 9100;
    public bool AutoReconnect { get; init; } = true;
}

public sealed record MirroringStats(double FramesPerSecond, int Width, int Height, long FramesReceived, long BytesReceived, DateTimeOffset? LastFrameAt);

public sealed record MirroringStateChangedEventArgs(MirroringState State, string? Message);

public interface IMirroringService : IAsyncDisposable
{
    MirroringState State { get; }
    string? ActiveDeviceUdid { get; }
    MirroringBackendKind? ActiveBackend { get; }
    MirroringStats Stats { get; }
    MirrorFrame? LatestFrame { get; }

    event EventHandler<MirrorFrame>? FrameReceived;
    event EventHandler<MirroringStateChangedEventArgs>? StateChanged;

    Task StartAsync(string udid, MirroringOptions options, CancellationToken cancellationToken = default);
    Task StopAsync();
}
