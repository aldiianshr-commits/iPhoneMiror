using System.Text;

namespace iPhoneMiror.Core.Plist;

/// <summary>Entry point for reading and writing Apple property lists (XML and binary "bplist00").</summary>
public static class PropertyList
{
    private static readonly byte[] BinaryMagic = "bplist00"u8.ToArray();

    public static bool IsBinary(ReadOnlySpan<byte> data) =>
        data.Length >= BinaryMagic.Length && data[..BinaryMagic.Length].SequenceEqual(BinaryMagic);

    /// <summary>Parses either an XML or a binary property list.</summary>
    public static object Parse(ReadOnlySpan<byte> data)
    {
        if (data.IsEmpty) throw new PlistFormatException("Property list is empty.");
        return IsBinary(data) ? BinaryPlistReader.Read(data) : XmlPlistReader.Read(data);
    }

    public static PlistDictionary ParseDictionary(ReadOnlySpan<byte> data) =>
        Parse(data) as PlistDictionary ?? throw new PlistFormatException("Root object is not a dictionary.");

    public static byte[] ToXml(object root) => XmlPlistWriter.Write(root);

    public static string ToXmlString(object root) => Encoding.UTF8.GetString(XmlPlistWriter.Write(root));
}
