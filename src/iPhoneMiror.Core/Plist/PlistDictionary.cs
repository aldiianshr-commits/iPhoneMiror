using System.Globalization;

namespace iPhoneMiror.Core.Plist;

/// <summary>
/// A property-list dictionary. Values are plain .NET objects:
/// <see cref="string"/>, <see cref="long"/>, <see cref="ulong"/>, <see cref="double"/>, <see cref="bool"/>,
/// <see cref="byte"/>[] (data), <see cref="DateTime"/>, <see cref="List{T}"/> of object (array),
/// <see cref="PlistDictionary"/> (dict) and <see cref="PlistUid"/>.
/// </summary>
public sealed class PlistDictionary : Dictionary<string, object>
{
    public PlistDictionary() : base(StringComparer.Ordinal) { }

    public PlistDictionary(IDictionary<string, object> source) : base(source, StringComparer.Ordinal) { }

    public string? GetString(string key) => TryGetValue(key, out var v) ? v as string : null;

    public long? GetInt64(string key)
    {
        if (!TryGetValue(key, out var v)) return null;
        return v switch
        {
            long l => l,
            ulong ul => unchecked((long)ul),
            int i => i,
            double d => (long)d,
            string s when long.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out var p) => p,
            _ => null,
        };
    }

    public bool? GetBoolean(string key) => TryGetValue(key, out var v) && v is bool b ? b : null;

    public byte[]? GetData(string key) => TryGetValue(key, out var v) ? v as byte[] : null;

    public PlistDictionary? GetDictionary(string key) => TryGetValue(key, out var v) ? v as PlistDictionary : null;

    public List<object>? GetArray(string key) => TryGetValue(key, out var v) ? v as List<object> : null;

    public PlistDictionary With(string key, object value)
    {
        this[key] = value;
        return this;
    }
}

/// <summary>Keyed-archiver UID value found in binary property lists.</summary>
public readonly record struct PlistUid(ulong Value);
