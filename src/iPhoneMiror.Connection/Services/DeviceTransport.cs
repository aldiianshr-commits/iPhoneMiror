using System.Net;
using System.Net.Sockets;
using iPhoneMiror.Connection.Usbmux;
using iPhoneMiror.Core.Abstractions;
using iPhoneMiror.Core.Models;

namespace iPhoneMiror.Connection.Services;

/// <summary>
/// Opens TCP streams to device ports. USB devices go through usbmux "Connect".
/// Wi-Fi devices are reached directly at their network address, falling back to usbmux.
/// </summary>
public sealed class DeviceTransport : IDeviceTransport
{
    private readonly IUsbmuxClient _usbmux;
    private readonly TimeSpan _connectTimeout;

    public DeviceTransport(IUsbmuxClient usbmux, TimeSpan? connectTimeout = null)
    {
        _usbmux = usbmux;
        _connectTimeout = connectTimeout ?? TimeSpan.FromSeconds(8);
    }

    public async Task<Stream> OpenAsync(DeviceInfo device, int devicePort, CancellationToken cancellationToken = default)
    {
        if (device.ConnectionType == ConnectionType.Network && IPAddress.TryParse(device.NetworkAddress, out var ip))
        {
            var socket = new Socket(ip.AddressFamily, SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
            try
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                timeout.CancelAfter(_connectTimeout);
                await socket.ConnectAsync(new IPEndPoint(ip, devicePort), timeout.Token).ConfigureAwait(false);
                return new NetworkStream(socket, ownsSocket: true);
            }
            catch (Exception ex) when (ex is SocketException or OperationCanceledException && !cancellationToken.IsCancellationRequested)
            {
                socket.Dispose();
                if (device.MuxDeviceId == 0)
                    throw new IOException($"Could not connect to {ip}:{devicePort} over Wi-Fi.", ex);
            }
        }

        if (device.MuxDeviceId == 0)
            throw new IOException($"Device {device.Udid} is not attached through Apple Mobile Device Service.");
        return await _usbmux.ConnectAsync(device.MuxDeviceId, devicePort, cancellationToken).ConfigureAwait(false);
    }
}
