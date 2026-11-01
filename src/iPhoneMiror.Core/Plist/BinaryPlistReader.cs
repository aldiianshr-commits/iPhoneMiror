using System.Buffers.Binary;
using System.Text;

namespace iPhoneMiror.Core.Plist;

/// <summary>Reader for Apple's binary property list format ("bplist00").</summary>
internal sealed class BinaryPlistReader
{
    private static readonly DateTime AppleEpoch = new(2001, 1, 1, 0, 0, 0, DateTimeKind.Utc);
    private readonly byte[] _data;
    private readonly int _objectRefSize;
    private readonly long[] _offsets;
    private int _depth;

    private BinaryPlistReader(byte[] data, int objectRefSize, long[] offsets)
    {
        _data = data;
        _objectRefSize = objectRefSize;
        _offsets = offsets;
    }

    public static object Read(ReadOnlySpan<byte> span)
    {
        var data = span.ToArray();
        if (data.Length < 8 + 32) throw new PlistFormatException("Binary property list is too short.");
        var trailer = data.AsSpan(data.Length - 32);
        int offsetIntSize = trailer[6];
        int objectRefSize = trailer[7];
        var numObjects = BinaryPrimitives.ReadUInt64BigEndian(trailer[8..]);
        var topObject = BinaryPrimitives.ReadUInt64BigEndian(trailer[16..]);
        var offsetTableOffset = BinaryPrimitives.ReadUInt64BigEndian(trailer[24..]);

        if (offsetIntSize is < 1 or > 8 || objectRefSize is < 1 or > 8)
            throw new PlistFormatException("Invalid binary plist trailer sizes.");
        if (numObjects == 0 || numObjects > (ulong)data.Length || topObject >= numObjects)
            throw new PlistFormatException("Invalid binary plist object count.");
        if (offsetTableOffset + numObjects * (ulong)offsetIntSize > (ulong)(data.Length - 32))
            throw new PlistFormatException("Offset table out of range.");

        var offsets = new long[numObjects];
        for (ulong i = 0; i < numObjects; i++)
        {
            offsets[i] = (long)ReadSizedUInt(data, (long)offsetTableOffset + (long)i * offsetIntSize, offsetIntSize);
            if (offsets[i] >= data.Length - 32) throw new PlistFormatException("Object offset out of range.");
        }

        return new BinaryPlistReader(data, objectRefSize, offsets).ReadObject((long)topObject);
    }

    private static ulong ReadSizedUInt(byte[] data, long pos, int size)
    {
        if (pos < 0 || pos + size > data.Length) throw new PlistFormatException("Read past end of data.");
        ulong v = 0;
        for (var i = 0; i < size; i++) v = (v << 8) | data[pos + i];
        return v;
    }

    private object ReadObject(long index)
    {
        if (index < 0 || index >= _offsets.Length) throw new PlistFormatException("Object reference out of range.");
        if (++_depth > 512) throw new PlistFormatException("Property list nesting too deep.");
        try
        {
            var pos = _offsets[index];
            var marker = _data[pos];
            int type = marker >> 4;
            int info = marker & 0x0F;
            pos++;

            switch (type)
            {
                case 0x0:
                    return info switch
                    {
                        0x8 => false,
                        0x9 => true,
                        _ => throw new PlistFormatException($"Unsupported singleton marker 0x{marker:X2}."),
                    };
                case 0x1:
                    {
                        var size = 1 << info;
                        if (size == 16) return (long)ReadSizedUInt(_data, pos + 8, 8);
                        var raw = ReadSizedUInt(_data, pos, size);
                        return size == 8 ? unchecked((long)raw) : (long)raw;
                    }
                case 0x2:
                    {
                        var size = 1 << info;
                        if (pos + size > _data.Length) throw new PlistFormatException("Real out of range.");
                        return size switch
                        {
                            4 => (double)BinaryPrimitives.ReadSingleBigEndian(_data.AsSpan((int)pos)),
                            8 => BinaryPrimitives.ReadDoubleBigEndian(_data.AsSpan((int)pos)),
                            _ => throw new PlistFormatException("Unsupported real size."),
                        };
                    }
                case 0x3:
                    {
                        var seconds = BinaryPrimitives.ReadDoubleBigEndian(_data.AsSpan((int)pos));
                        return AppleEpoch.AddSeconds(seconds);
                    }
                case 0x4:
                    {
                        var (count, start) = ReadCount(info, pos);
                        return Slice(start, count);
                    }
                case 0x5:
                    {
                        var (count, start) = ReadCount(info, pos);
                        return Encoding.ASCII.GetString(Slice(start, count));
                    }
                case 0x6:
                    {
                        var (count, start) = ReadCount(info, pos);
                        return Encoding.BigEndianUnicode.GetString(Slice(start, count * 2));
                    }
                case 0x7:
                    {
                        var (count, start) = ReadCount(info, pos);
                        return Encoding.UTF8.GetString(Slice(start, count));
                    }
                case 0x8:
                    return new PlistUid(ReadSizedUInt(_data, pos, info + 1));
                case 0xA:
                case 0xC:
                    {
                        var (count, start) = ReadCount(info, pos);
                        var list = new List<object>((int)Math.Min(count, 4096));
                        for (long i = 0; i < count; i++)
                            list.Add(ReadObject((long)ReadSizedUInt(_data, start + i * _objectRefSize, _objectRefSize)));
                        return list;
                    }
                case 0xD:
                    {
                        var (count, start) = ReadCount(info, pos);
                        var dict = new PlistDictionary();
                        for (long i = 0; i < count; i++)
                        {
                            var keyRef = (long)ReadSizedUInt(_data, start + i * _objectRefSize, _objectRefSize);
                            var valRef = (long)ReadSizedUInt(_data, start + (count + i) * _objectRefSize, _objectRefSize);
                            var key = ReadObject(keyRef) as string ?? throw new PlistFormatException("Dictionary key is not a string.");
                            dict[key] = ReadObject(valRef);
                        }
                        return dict;
                    }
                default:
                    throw new PlistFormatException($"Unsupported object marker 0x{marker:X2}.");
            }
        }
        finally
        {
            _depth--;
        }
    }

    private (long Count, long Start) ReadCount(int info, long pos)
    {
        if (info != 0xF) return (info, pos);
        var marker = _data[pos];
        if (marker >> 4 != 0x1) throw new PlistFormatException("Expected integer count.");
        var size = 1 << (marker & 0x0F);
        var count = (long)ReadSizedUInt(_data, pos + 1, size);
        if (count < 0 || count > _data.Length) throw new PlistFormatException("Invalid object count.");
        return (count, pos + 1 + size);
    }

    private byte[] Slice(long start, long count)
    {
        if (start < 0 || count < 0 || start + count > _data.Length) throw new PlistFormatException("Data out of range.");
        return _data.AsSpan((int)start, (int)count).ToArray();
    }
}
