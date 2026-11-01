using iPhoneMiror.Core.Models;

namespace iPhoneMiror.Core.Abstractions;

/// <summary>Opens raw TCP streams to a port on the iPhone (via usbmux over USB, or directly over Wi-Fi).</summary>
public interface IDeviceTransport
{
    Task<Stream> OpenAsync(DeviceInfo device, int devicePort, CancellationToken cancellationToken = default);
}
