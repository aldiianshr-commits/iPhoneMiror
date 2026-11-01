using System.Buffers.Binary;
using System.Net;
using System.Text;
using iPhoneMiror.DeviceDiscovery.Mdns;
using Microsoft.Extensions.Logging.Abstractions;

namespace iPhoneMiror.UnitTests;

public class DnsAndDiscoveryTests
{
    /// <summary>Builds an mDNS response: PTR (answer) + SRV, TXT, A (additional), using name compression.</summary>
    internal static byte[] BuildResponse(string service, string instanceLabel, string host, IPAddress address, ushort port, uint ttl = 120)
    {
        var ms = new MemoryStream();
        void U16(int v) { Span<byte> b = stackalloc byte[2]; BinaryPrimitives.WriteUInt16BigEndian(b, (ushort)v); ms.Write(b); }
        void U32(uint v) { Span<byte> b = stackalloc byte[4]; BinaryPrimitives.WriteUInt32BigEndian(b, v); ms.Write(b); }
        void Name(string n) { foreach (var l in n.Split('.')) { var b = Encoding.UTF8.GetBytes(l); ms.WriteByte((byte)b.Length); ms.Write(b); } ms.WriteByte(0); }

        U16(0); U16(0x8400); U16(0); U16(1); U16(0); U16(3);
        var serviceOffset = (int)ms.Length;
        Name(service);
        U16(12); U16(1); U32(ttl);
        // PTR rdata: instance label + pointer to service name
        var label = Encoding.UTF8.GetBytes(instanceLabel);
        U16(1 + label.Length + 2);
        var instanceOffset = (int)ms.Length;
        ms.WriteByte((byte)label.Length); ms.Write(label); U16(0xC000 | serviceOffset);

        // SRV with compressed owner name
        U16(0xC000 | instanceOffset); U16(33); U16(0x8001); U32(ttl);
        var hostBytes = new MemoryStream();
        foreach (var l in host.Split('.')) { var b = Encoding.UTF8.GetBytes(l); hostBytes.WriteByte((byte)b.Length); hostBytes.Write(b); }
        hostBytes.WriteByte(0);
        U16(6 + (int)hostBytes.Length); U16(0); U16(0); U16(port); ms.Write(hostBytes.ToArray());

        // TXT
        U16(0xC000 | instanceOffset); U16(16); U16(0x8001); U32(ttl);
        var txt = Encoding.UTF8.GetBytes("model=iPhone17,1");
        U16(1 + txt.Length); ms.WriteByte((byte)txt.Length); ms.Write(txt);

        // A
        Name(host); U16(1); U16(0x8001); U32(ttl); U16(4); ms.Write(address.GetAddressBytes());
        return ms.ToArray();
    }

    [Fact]
    public void Query_EncodesPtrQuestion()
    {
        var bytes = DnsMessage.CreateQuery(new DnsQuestion("_apple-mobdev2._tcp.local", DnsRecordType.Ptr)).Encode();
        var parsed = DnsMessage.Parse(bytes);
        Assert.False(parsed.IsResponse);
        Assert.Equal("_apple-mobdev2._tcp.local", Assert.Single(parsed.Questions).Name);
        Assert.Equal(DnsRecordType.Ptr, parsed.Questions[0].Type);
    }

    [Fact]
    public void Parse_ResponseWithCompression()
    {
        var bytes = BuildResponse("_apple-mobdev2._tcp.local", "aa:bb:cc:dd:ee:ff@fe80::1", "My-iPhone.local", IPAddress.Parse("192.168.1.50"), 32498);
        var msg = DnsMessage.Parse(bytes);
        Assert.True(msg.IsResponse);
        var ptr = Assert.Single(msg.Answers);
        Assert.Equal("aa:bb:cc:dd:ee:ff@fe80::1._apple-mobdev2._tcp.local", ptr.PtrTarget);
        var srv = msg.Additional.Single(r => r.Type == DnsRecordType.Srv);
        Assert.Equal((ushort)32498, srv.Srv!.Value.Port);
        Assert.Equal("My-iPhone.local", srv.Srv.Value.Target);
        Assert.Equal("iPhone17,1", msg.Additional.Single(r => r.Type == DnsRecordType.Txt).Txt!["model"]);
        Assert.Equal(IPAddress.Parse("192.168.1.50"), msg.Additional.Single(r => r.Type == DnsRecordType.A).Address);
    }

    [Fact]
    public void Parse_RejectsCompressionLoop()
    {
        var bytes = new byte[] { 0, 0, 0x84, 0, 0, 1, 0, 0, 0, 0, 0, 0, 0xC0, 12, 0, 12, 0, 1 };
        Assert.Throws<FormatException>(() => DnsMessage.Parse(bytes));
    }

    [Fact]
    public async Task Browser_ResolvesInstanceFromResponse()
    {
        await using var browser = new MdnsBrowser(AppleServiceNames.All, NullLogger.Instance);
        MdnsServiceInstance? found = null;
        browser.InstanceFound += (_, i) => found = i;
        var msg = DnsMessage.Parse(BuildResponse("_apple-mobdev2._tcp.local", "aa:bb:cc:dd:ee:ff@fe80::1", "My-iPhone.local", IPAddress.Parse("10.0.0.7"), 1234));
        browser.Process(msg);
        Assert.NotNull(found);
        Assert.Equal("_apple-mobdev2._tcp.local", found!.ServiceType);
        Assert.Equal(1234, found.Port);
        Assert.Equal(IPAddress.Parse("10.0.0.7"), Assert.Single(found.Addresses));
        Assert.Equal("aa:bb:cc:dd:ee:ff", AppleServiceNames.TryGetWifiMac(found.InstanceName));
    }

    [Theory]
    [InlineData("AA:BB:CC:DD:EE:FF@fe80::1._apple-mobdev2._tcp.local", "aa:bb:cc:dd:ee:ff")]
    [InlineData("SomeService._remotepairing._tcp.local", null)]
    public void WifiMacFromInstanceName(string name, string? expected) => Assert.Equal(expected, AppleServiceNames.TryGetWifiMac(name));
}
