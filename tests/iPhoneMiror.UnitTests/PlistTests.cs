using iPhoneMiror.Core.Plist;

namespace iPhoneMiror.UnitTests;

public class PlistTests
{
    // Generated with Python's plistlib (FMT_BINARY) as an independent reference implementation.
    private const string BinaryFixture = "YnBsaXN0MDDbAQIDBAUGBwgJCgsMDQ4PEBYXGBkaG1NCaWdURGF0YVREYXRlVEZsYWdUTGlzdFtNZXNzYWdlVHlwZVNOZWdWTnVtYmVyU09mZlRSZWFsV1VuaWNvZGUTAAABAAAAAABIAAECaGVsbG8zQceDtpKAAAAJoxESE1FhEAHRFBVRa1F2VlJlc3VsdBP/////////+xAACCNACgAAAAAAAG8QEgBpAFAAaABvAG4AZQAgIBMAIADcAG4A7wBjAG8AZABlACAnEwgfIygtMjdDR05SV19ocXp7f4GDhoiKkZqcnaYAAAAAAAABAQAAAAAAAAAcAAAAAAAAAAAAAAAAAAAAzQ==";

    [Fact]
    public void Xml_RoundTrip_PreservesAllTypes()
    {
        var original = new PlistDictionary
        {
            ["String"] = "hello <&> world",
            ["Int"] = 42L,
            ["Negative"] = -7L,
            ["True"] = true,
            ["False"] = false,
            ["Data"] = new byte[] { 0, 1, 2, 255 },
            ["Real"] = 1.5,
            ["Date"] = new DateTime(2026, 11, 1, 12, 0, 0, DateTimeKind.Utc),
            ["Array"] = new List<object> { "x", 1L, new PlistDictionary { ["n"] = "v" } },
        };

        var xml = PropertyList.ToXml(original);
        var parsed = PropertyList.ParseDictionary(xml);

        Assert.Equal("hello <&> world", parsed.GetString("String"));
        Assert.Equal(42L, parsed.GetInt64("Int"));
        Assert.Equal(-7L, parsed.GetInt64("Negative"));
        Assert.True(parsed.GetBoolean("True"));
        Assert.False(parsed.GetBoolean("False"));
        Assert.Equal(new byte[] { 0, 1, 2, 255 }, parsed.GetData("Data"));
        Assert.Equal(1.5, (double)parsed["Real"]);
        Assert.Equal(new DateTime(2026, 11, 1, 12, 0, 0, DateTimeKind.Utc), (DateTime)parsed["Date"]);
        var array = parsed.GetArray("Array")!;
        Assert.Equal("x", array[0]);
        Assert.Equal("v", ((PlistDictionary)array[2]).GetString("n"));
    }

    [Fact]
    public void Xml_ContainsAppleDoctype()
    {
        var text = PropertyList.ToXmlString(new PlistDictionary { ["Request"] = "QueryType" });
        Assert.Contains("<!DOCTYPE plist PUBLIC \"-//Apple//DTD PLIST 1.0//EN\"", text);
        Assert.Contains("<key>Request</key>", text);
    }

    [Fact]
    public void Binary_ParsesPythonGeneratedFixture()
    {
        var bytes = Convert.FromBase64String(BinaryFixture);
        Assert.True(PropertyList.IsBinary(bytes));
        var d = PropertyList.ParseDictionary(bytes);
        Assert.Equal("Result", d.GetString("MessageType"));
        Assert.Equal(0L, d.GetInt64("Number"));
        Assert.Equal(1L << 40, d.GetInt64("Big"));
        Assert.Equal(-5L, d.GetInt64("Neg"));
        Assert.True(d.GetBoolean("Flag"));
        Assert.False(d.GetBoolean("Off"));
        Assert.Equal("\0\u0001\u0002hello"u8.ToArray(), d.GetData("Data"));
        Assert.Equal(3.25, (double)d["Real"]);
        Assert.Equal("iPhone – Ünïcode ✓", d.GetString("Unicode"));
        Assert.Equal(new DateTime(2026, 1, 2, 3, 4, 5, DateTimeKind.Utc), (DateTime)d["Date"]);
        var list = d.GetArray("List")!;
        Assert.Equal(3, list.Count);
        Assert.Equal("v", ((PlistDictionary)list[2]).GetString("k"));
    }

    [Fact]
    public void Binary_RejectsTruncatedData()
    {
        var bytes = Convert.FromBase64String(BinaryFixture);
        Assert.Throws<PlistFormatException>(() => PropertyList.Parse(bytes.AsSpan(0, 30)));
    }

    [Fact]
    public void Xml_RejectsGarbage()
    {
        Assert.Throws<PlistFormatException>(() => PropertyList.Parse("not xml"u8));
    }
}
