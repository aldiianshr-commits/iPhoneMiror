using System.Globalization;
using System.Xml;
using System.Xml.Linq;

namespace iPhoneMiror.Core.Plist;

internal static class XmlPlistReader
{
    public static object Read(ReadOnlySpan<byte> data)
    {
        var settings = new XmlReaderSettings
        {
            DtdProcessing = DtdProcessing.Ignore,
            XmlResolver = null,
            IgnoreComments = true,
            IgnoreWhitespace = true,
        };
        XDocument doc;
        try
        {
            using var ms = new MemoryStream(data.ToArray());
            using var reader = XmlReader.Create(ms, settings);
            doc = XDocument.Load(reader);
        }
        catch (XmlException ex)
        {
            throw new PlistFormatException("Invalid XML property list.", ex);
        }

        var root = doc.Root ?? throw new PlistFormatException("Missing root element.");
        if (root.Name.LocalName != "plist") throw new PlistFormatException("Root element is not <plist>.");
        var first = root.Elements().FirstOrDefault() ?? throw new PlistFormatException("Empty <plist>.");
        return ReadValue(first);
    }

    private static object ReadValue(XElement e)
    {
        switch (e.Name.LocalName)
        {
            case "string": return e.Value;
            case "true": return true;
            case "false": return false;
            case "integer":
                {
                    var text = e.Value.Trim();
                    if (long.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var l)) return l;
                    if (ulong.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var ul)) return ul;
                    throw new PlistFormatException($"Invalid integer '{text}'.");
                }
            case "real":
                return double.Parse(e.Value.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture);
            case "date":
                return DateTime.Parse(e.Value.Trim(), CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal);
            case "data":
                {
                    var b64 = new string(e.Value.Where(c => !char.IsWhiteSpace(c)).ToArray());
                    return Convert.FromBase64String(b64);
                }
            case "array":
                return e.Elements().Select(ReadValue).ToList();
            case "dict":
                {
                    var dict = new PlistDictionary();
                    var children = e.Elements().ToList();
                    for (var i = 0; i < children.Count; i += 2)
                    {
                        if (children[i].Name.LocalName != "key")
                            throw new PlistFormatException("Expected <key> in <dict>.");
                        if (i + 1 >= children.Count)
                            throw new PlistFormatException($"Missing value for key '{children[i].Value}'.");
                        dict[children[i].Value] = ReadValue(children[i + 1]);
                    }
                    return dict;
                }
            default:
                throw new PlistFormatException($"Unsupported element <{e.Name.LocalName}>.");
        }
    }
}
