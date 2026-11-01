using iPhoneMiror.Core.Models;

namespace iPhoneMiror.Core.Abstractions;

/// <summary>A point in device coordinates (iOS points, not pixels).</summary>
public readonly record struct DevicePoint(double X, double Y);

public enum HardwareButton { Home, VolumeUp, VolumeDown }

public sealed record InputAvailability(bool IsAvailable, string Message, DateTimeOffset CheckedAt)
{
    public static InputAvailability Unknown { get; } = new(false, "Not checked yet.", DateTimeOffset.MinValue);
}

/// <summary>Forwards Windows input to the iPhone. Implementations must actually deliver events to the device.</summary>
public interface IInputForwarder : IAsyncDisposable
{
    InputAvailability Availability { get; }
    event EventHandler<InputAvailability>? AvailabilityChanged;

    /// <summary>Size of the device screen in points, once known.</summary>
    (double Width, double Height)? ScreenSizePoints { get; }

    Task<InputAvailability> ConnectAsync(DeviceInfo device, int wdaPort, CancellationToken cancellationToken = default);
    Task DisconnectAsync();

    Task TapAsync(DevicePoint point, CancellationToken cancellationToken = default);
    Task DoubleTapAsync(DevicePoint point, CancellationToken cancellationToken = default);
    Task LongPressAsync(DevicePoint point, TimeSpan duration, CancellationToken cancellationToken = default);
    Task DragAsync(DevicePoint from, DevicePoint to, TimeSpan duration, CancellationToken cancellationToken = default);
    Task TypeTextAsync(string text, CancellationToken cancellationToken = default);
    Task PressButtonAsync(HardwareButton button, CancellationToken cancellationToken = default);
}

/// <summary>Applies stream quality settings on the device side (implemented by the WebDriverAgent client).</summary>
public interface IMirrorQualityConfigurator
{
    Task ApplyStreamSettingsAsync(DeviceInfo device, int wdaPort, int frameRate, int quality, int scalingPercent, CancellationToken cancellationToken = default);
}
