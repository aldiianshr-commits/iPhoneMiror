using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using iPhoneMiror.Core.Plist;

namespace iPhoneMiror.Connection.Pairing;

/// <summary>
/// A host/device pair record, compatible with the format stored by Apple Mobile Device Service and libimobiledevice
/// (keys: DeviceCertificate, HostCertificate, RootCertificate, HostPrivateKey, RootPrivateKey, HostID, SystemBUID,
/// EscrowBag, WiFiMACAddress). Certificates and keys are PEM text stored as plist data.
/// </summary>
public sealed class PairRecord
{
    public required byte[] DeviceCertificate { get; init; }
    public required byte[] HostCertificate { get; init; }
    public required byte[] RootCertificate { get; init; }
    public byte[]? HostPrivateKey { get; init; }
    public byte[]? RootPrivateKey { get; init; }
    public required string HostId { get; init; }
    public required string SystemBuid { get; init; }
    public byte[]? EscrowBag { get; set; }
    public string? WifiMacAddress { get; set; }

    public static PairRecord FromPlist(PlistDictionary d) => new()
    {
        DeviceCertificate = d.GetData("DeviceCertificate") ?? throw new PlistFormatException("Pair record lacks DeviceCertificate."),
        HostCertificate = d.GetData("HostCertificate") ?? throw new PlistFormatException("Pair record lacks HostCertificate."),
        RootCertificate = d.GetData("RootCertificate") ?? throw new PlistFormatException("Pair record lacks RootCertificate."),
        HostPrivateKey = d.GetData("HostPrivateKey"),
        RootPrivateKey = d.GetData("RootPrivateKey"),
        HostId = d.GetString("HostID") ?? throw new PlistFormatException("Pair record lacks HostID."),
        SystemBuid = d.GetString("SystemBUID") ?? throw new PlistFormatException("Pair record lacks SystemBUID."),
        EscrowBag = d.GetData("EscrowBag"),
        WifiMacAddress = d.GetString("WiFiMACAddress"),
    };

    public static PairRecord FromBytes(byte[] plist) => FromPlist(PropertyList.ParseDictionary(plist));

    /// <summary>Full record including private keys, for local storage only.</summary>
    public PlistDictionary ToPlist()
    {
        var d = ToPairRequestPlist();
        if (HostPrivateKey is not null) d["HostPrivateKey"] = HostPrivateKey;
        if (RootPrivateKey is not null) d["RootPrivateKey"] = RootPrivateKey;
        if (EscrowBag is not null) d["EscrowBag"] = EscrowBag;
        if (WifiMacAddress is not null) d["WiFiMACAddress"] = WifiMacAddress;
        return d;
    }

    /// <summary>The subset sent to the device in a "Pair" request (no private keys).</summary>
    public PlistDictionary ToPairRequestPlist() => new()
    {
        ["DeviceCertificate"] = DeviceCertificate,
        ["HostCertificate"] = HostCertificate,
        ["RootCertificate"] = RootCertificate,
        ["HostID"] = HostId,
        ["SystemBUID"] = SystemBuid,
    };

    public byte[] ToBytes() => PropertyList.ToXml(ToPlist());

    public bool HasHostPrivateKey => HostPrivateKey is { Length: > 0 };

    internal byte[] DeviceCertificatePemBytesToDer() => PemToDer(DeviceCertificate);

    /// <summary>Host certificate with its private key, re-imported so Windows SChannel can use it for client authentication.</summary>
    public X509Certificate2 CreateHostCertificateWithKey()
    {
        if (!HasHostPrivateKey) throw new InvalidOperationException("Pair record has no host private key; it cannot be used to start a session.");
        using var rsa = RSA.Create();
        rsa.ImportFromPem(Encoding.ASCII.GetString(HostPrivateKey!));
        using var cert = X509CertificateLoader.LoadCertificate(PemToDer(HostCertificate));
        using var withKey = cert.CopyWithPrivateKey(rsa);
        var pfx = withKey.Export(X509ContentType.Pkcs12);
        return X509CertificateLoader.LoadPkcs12(pfx, null, X509KeyStorageFlags.Exportable);
    }

    internal static byte[] PemToDer(byte[] pemOrDer)
    {
        var text = Encoding.ASCII.GetString(pemOrDer);
        if (!text.Contains("-----BEGIN", StringComparison.Ordinal)) return pemOrDer;
        var fields = PemEncoding.Find(text);
        return Convert.FromBase64String(text[fields.Base64Data]);
    }
}
