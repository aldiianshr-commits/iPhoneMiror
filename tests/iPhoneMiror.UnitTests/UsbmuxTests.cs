using System.Net;
using iPhoneMiror.Connection.Usbmux;
using iPhoneMiror.Core.Models;
using iPhoneMiror.Core.Plist;

namespace iPhoneMiror.UnitTests;

public class UsbmuxTests
{
    [Fact]
    public void Encode_WritesLittleEndianHeader()
    {
        var packet = UsbmuxProtocol.Encode(UsbmuxProtocol.CreateMessage("ListDevices"), tag: 7);
        var (length, version, message, tag) = UsbmuxProtocol.DecodeHeader(packet);
        Assert.Equal((uint)packet.Length, length);
        Assert.Equal(1u, version);
        Assert.Equal(8u, message);
        Assert.Equal(7u, tag);
        var payload = PropertyList.ParseDictionary(packet.AsSpan(16));
        Assert.Equal("ListDevices", payload.GetString("MessageType"));
        Assert.Equal(3L, payload.GetInt64("kLibUSBMuxVersion"));
    }

    [Theory]
    [InlineData(62078, 32498)]
    [InlineData(8100, 42015)]
    [InlineData(9100, 35875)]
    public void Port_IsSentInNetworkByteOrder(int port, long encoded)
    {
        Assert.Equal(encoded, UsbmuxProtocol.EncodePort(port));
        Assert.Equal(port, UsbmuxProtocol.DecodePort(encoded));
    }

    [Fact]
    public async Task ReadPacket_RoundTripsThroughStream()
    {
        var ms = new MemoryStream(UsbmuxProtocol.Encode(new PlistDictionary { ["MessageType"] = "Result", ["Number"] = 0L }, 3));
        var (tag, payload) = await UsbmuxProtocol.ReadPacketAsync(ms, CancellationToken.None);
        Assert.Equal(3u, tag);
        Assert.Equal("Result", payload.GetString("MessageType"));
    }

    [Fact]
    public void Device_FromUsbProperties()
    {
        var props = new PlistDictionary
        {
            ["DeviceID"] = 5L,
            ["SerialNumber"] = "00008030001A2D3E3C82802E",
            ["ConnectionType"] = "USB",
            ["ProductID"] = 4776L,
            ["LocationID"] = 123L,
        };
        var d = UsbmuxDevice.FromProperties(props);
        Assert.Equal(5, d.DeviceId);
        Assert.Equal("00008030-001A2D3E3C82802E", d.Udid);
        Assert.Equal(ConnectionType.Usb, d.ConnectionType);
        Assert.Null(d.NetworkAddress);
    }

    [Fact]
    public void Device_FromNetworkProperties_ParsesSockaddr()
    {
        var sockaddr = new byte[16];
        sockaddr[0] = 16;
        sockaddr[1] = 0x02;
        new byte[] { 192, 168, 1, 23 }.CopyTo(sockaddr, 4);
        var props = new PlistDictionary
        {
            ["DeviceID"] = 9L,
            ["SerialNumber"] = "00008030-001A2D3E3C82802E",
            ["ConnectionType"] = "Network",
            ["NetworkAddress"] = sockaddr,
        };
        var d = UsbmuxDevice.FromProperties(props);
        Assert.Equal(ConnectionType.Network, d.ConnectionType);
        Assert.Equal(IPAddress.Parse("192.168.1.23"), d.NetworkAddress);
    }

    [Fact]
    public void ParseNetworkAddress_IPv6()
    {
        var sockaddr = new byte[28];
        sockaddr[0] = 28;
        sockaddr[1] = 0x1E;
        IPAddress.Parse("fe80::1").GetAddressBytes().CopyTo(sockaddr, 8);
        Assert.Equal(IPAddress.Parse("fe80::1"), UsbmuxDevice.ParseNetworkAddress(sockaddr));
    }

    [Theory]
    [InlineData("0123456789abcdef0123456789abcdef01234567", "0123456789abcdef0123456789abcdef01234567")]
    [InlineData("00008030-001A2D3E3C82802E", "00008030-001A2D3E3C82802E")]
    [InlineData("00008030001A2D3E3C82802E", "00008030-001A2D3E3C82802E")]
    public void NormalizeUdid(string input, string expected) => Assert.Equal(expected, UsbmuxDevice.NormalizeUdid(input));
}
