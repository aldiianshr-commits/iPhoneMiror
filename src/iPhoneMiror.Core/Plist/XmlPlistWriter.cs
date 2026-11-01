using System.Globalization;
using System.Text;
using System.Xml;

namespace iPhoneMiror.Core.Plist;

internal static class XmlPlistWriter
{
    public static byte[] Write(object root)
    {
        var settings = new XmlWriterSettings
        {
            Encoding = new UTF8Encoding(false),
            Indent = true,
            IndentChars = "\t",
            NewLineChars = "\n",
            OmitXmlDeclaration = false,
        };
        using var ms = new MemoryStream();
        using (var w = XmlWriter.Create(ms, settings))
        {
            w.WriteStartDocument();
            w.WriteDocType("plist", "-//Apple//DTD PLIST 1.0//EN", "http://www.apple.com/DTDs/PropertyList-1.0.dtd", null);
            w.WriteStartElement("plist");
            w.WriteAttributeString("version", "1.0");
            WriteValue(w, root);
            w.WriteEndElement();
            w.WriteEndDocument();
        }
        ms.WriteByte((byte)'\n');
        return ms.ToArray();
    }

    private static void WriteValue(XmlWriter w, object? value)
    {
        switch (value)
        {
            case null:
                throw new PlistFormatException("Property lists cannot contain null values.");
            case string s:
                w.WriteElementString("string", s);
                break;
            case bool b:
                w.WriteStartElement(b ? "true" : "false");
                w.WriteEndElement();
                break;
            case byte or sbyte or short or ushort or int or uint or long:
                w.WriteElementString("integer", Convert.ToInt64(value, CultureInfo.InvariantCulture).ToString(CultureInfo.InvariantCulture));
                break;
            case ulong ul:
                w.WriteElementString("integer", ul.ToString(CultureInfo.InvariantCulture));
                break;
            case float or double:
                w.WriteElementString("real", Convert.ToDouble(value, CultureInfo.InvariantCulture).ToString("R", CultureInfo.InvariantCulture));
                break;
            case DateTime dt:
                w.WriteElementString("date", dt.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture));
                break;
            case byte[] data:
                w.WriteElementString("data", Convert.ToBase64String(data));
                break;
            case PlistUid uid:
                // XML plists have no UID type; Apple encodes it as a dict with CF$UID.
                w.WriteStartElement("dict");
                w.WriteElementString("key", "CF$UID");
                w.WriteElementString("integer", uid.Value.ToString(CultureInfo.InvariantCulture));
                w.WriteEndElement();
                break;
            case IDictionary<string, object> dict:
                w.WriteStartElement("dict");
                foreach (var kv in dict)
                {
                    w.WriteElementString("key", kv.Key);
                    WriteValue(w, kv.Value);
                }
                w.WriteEndElement();
                break;
            case System.Collections.IEnumerable list:
                w.WriteStartElement("array");
                foreach (var item in list) WriteValue(w, item);
                w.WriteEndElement();
                break;
            default:
                throw new PlistFormatException($"Unsupported property list type: {value.GetType().FullName}");
        }
    }
}
