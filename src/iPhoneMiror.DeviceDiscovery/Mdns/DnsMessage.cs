using System.Buffers.Binary;
using System.Net;
using System.Text;

namespace iPhoneMiror.DeviceDiscovery.Mdns;

public enum DnsRecordType : ushort
{
    A = 1,
    Ptr = 12,
    Txt = 16,
    Aaaa = 28,
    Srv = 33,
    Any = 255,
}

public sealed record DnsQuestion(string Name, DnsRecordType Type, bool UnicastResponse = false);

public sealed record DnsRecord(string Name, DnsRecordType Type, uint Ttl, byte[] RawData)
{
    public string? PtrTarget { get; init; }
    public IPAddress? Address { get; init; }
    public (ushort Priority, ushort Weight, ushort Port, string Target)? Srv { get; init; }
    public IReadOnlyDictionary<string, string>? Txt { get; init; }
}

/// <summary>Minimal DNS message codec for multicast DNS (RFC 6762) / DNS-SD (RFC 6763).</summary>
public sealed class DnsMessage
{
    public ushort Id { get; init; }
    public bool IsResponse { get; init; }
    public List<DnsQuestion> Questions { get; } = [];
    public List<DnsRecord> Answers { get; } = [];
    public List<DnsRecord> Additional { get; } = [];

    public IEnumerable<DnsRecord> AllRecords => Answers.Concat(Additional);

    public byte[] Encode()
    {
        using var ms = new MemoryStream();
        Span<byte> header = stackalloc byte[12];
        BinaryPrimitives.WriteUInt16BigEndian(header, Id);
        BinaryPrimitives.WriteUInt16BigEndian(header[2..], (ushort)(IsResponse ? 0x8400 : 0));
        BinaryPrimitives.WriteUInt16BigEndian(header[4..], (ushort)Questions.Count);
        ms.Write(header);
        foreach (var q in Questions)
        {
            WriteName(ms, q.Name);
            Span<byte> tail = stackalloc byte[4];
            BinaryPrimitives.WriteUInt16BigEndian(tail, (ushort)q.Type);
            BinaryPrimitives.WriteUInt16BigEndian(tail[2..], (ushort)(q.UnicastResponse ? 0x8001 : 0x0001));
            ms.Write(tail);
        }
        return ms.ToArray();
    }

    public static DnsMessage CreateQuery(params DnsQuestion[] questions)
    {
        var m = new DnsMessage();
        m.Questions.AddRange(questions);
        return m;
    }

    private static void WriteName(Stream s, string name)
    {
        foreach (var label in name.TrimEnd('.').Split('.', StringSplitOptions.RemoveEmptyEntries))
        {
            var bytes = Encoding.UTF8.GetBytes(label);
            if (bytes.Length > 63) throw new ArgumentException($"DNS label too long: {label}");
            s.WriteByte((byte)bytes.Length);
            s.Write(bytes);
        }
        s.WriteByte(0);
    }

    public static DnsMessage Parse(ReadOnlySpan<byte> data)
    {
        if (data.Length < 12) throw new FormatException("DNS message too short.");
        var buf = data.ToArray();
        var flags = BinaryPrimitives.ReadUInt16BigEndian(buf.AsSpan(2));
        var msg = new DnsMessage
        {
            Id = BinaryPrimitives.ReadUInt16BigEndian(buf),
            IsResponse = (flags & 0x8000) != 0,
        };
        int qd = BinaryPrimitives.ReadUInt16BigEndian(buf.AsSpan(4));
        int an = BinaryPrimitives.ReadUInt16BigEndian(buf.AsSpan(6));
        int ns = BinaryPrimitives.ReadUInt16BigEndian(buf.AsSpan(8));
        int ar = BinaryPrimitives.ReadUInt16BigEndian(buf.AsSpan(10));
        var pos = 12;
        for (var i = 0; i < qd; i++)
        {
            var name = ReadName(buf, ref pos);
            Need(buf, pos, 4);
            var type = (DnsRecordType)BinaryPrimitives.ReadUInt16BigEndian(buf.AsSpan(pos));
            var cls = BinaryPrimitives.ReadUInt16BigEndian(buf.AsSpan(pos + 2));
            pos += 4;
            msg.Questions.Add(new DnsQuestion(name, type, (cls & 0x8000) != 0));
        }
        for (var i = 0; i < an; i++) msg.Answers.Add(ReadRecord(buf, ref pos));
        for (var i = 0; i < ns; i++) ReadRecord(buf, ref pos);
        for (var i = 0; i < ar; i++) msg.Additional.Add(ReadRecord(buf, ref pos));
        return msg;
    }

    private static void Need(byte[] buf, int pos, int count)
    {
        if (pos < 0 || pos + count > buf.Length) throw new FormatException("DNS message truncated.");
    }

    private static DnsRecord ReadRecord(byte[] buf, ref int pos)
    {
        var name = ReadName(buf, ref pos);
        Need(buf, pos, 10);
        var type = (DnsRecordType)BinaryPrimitives.ReadUInt16BigEndian(buf.AsSpan(pos));
        var ttl = BinaryPrimitives.ReadUInt32BigEndian(buf.AsSpan(pos + 4));
        int rdlen = BinaryPrimitives.ReadUInt16BigEndian(buf.AsSpan(pos + 8));
        pos += 10;
        Need(buf, pos, rdlen);
        var rdStart = pos;
        var raw = buf.AsSpan(pos, rdlen).ToArray();
        pos += rdlen;

        var record = new DnsRecord(name, type, ttl, raw);
        switch (type)
        {
            case DnsRecordType.Ptr:
                {
                    var p = rdStart;
                    return record with { PtrTarget = ReadName(buf, ref p) };
                }
            case DnsRecordType.A when rdlen == 4:
                return record with { Address = new IPAddress(raw) };
            case DnsRecordType.Aaaa when rdlen == 16:
                return record with { Address = new IPAddress(raw) };
            case DnsRecordType.Srv when rdlen >= 7:
                {
                    var p = rdStart + 6;
                    var target = ReadName(buf, ref p);
                    return record with
                    {
                        Srv = (BinaryPrimitives.ReadUInt16BigEndian(raw), BinaryPrimitives.ReadUInt16BigEndian(raw.AsSpan(2)),
                               BinaryPrimitives.ReadUInt16BigEndian(raw.AsSpan(4)), target),
                    };
                }
            case DnsRecordType.Txt:
                {
                    var txt = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                    var i = 0;
                    while (i < raw.Length)
                    {
                        int len = raw[i++];
                        if (i + len > raw.Length) break;
                        var entry = Encoding.UTF8.GetString(raw, i, len);
                        i += len;
                        var eq = entry.IndexOf('=');
                        if (eq > 0) txt[entry[..eq]] = entry[(eq + 1)..];
                        else if (entry.Length > 0) txt[entry] = "";
                    }
                    return record with { Txt = txt };
                }
            default:
                return record;
        }
    }

    internal static string ReadName(byte[] buf, ref int pos)
    {
        var labels = new List<string>();
        var jumped = false;
        var p = pos;
        var guard = 0;
        while (true)
        {
            Need(buf, p, 1);
            int len = buf[p];
            if (len == 0)
            {
                p++;
                break;
            }
            if ((len & 0xC0) == 0xC0)
            {
                Need(buf, p, 2);
                var ptr = ((len & 0x3F) << 8) | buf[p + 1];
                if (!jumped) pos = p + 2;
                jumped = true;
                p = ptr;
                if (++guard > 64) throw new FormatException("DNS name compression loop.");
                continue;
            }
            Need(buf, p + 1, len);
            labels.Add(Encoding.UTF8.GetString(buf, p + 1, len));
            p += len + 1;
        }
        if (!jumped) pos = p;
        return string.Join('.', labels);
    }
}
