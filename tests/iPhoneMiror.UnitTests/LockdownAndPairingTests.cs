using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using iPhoneMiror.Connection.Lockdown;
using iPhoneMiror.Connection.Pairing;
using iPhoneMiror.Core.Plist;

namespace iPhoneMiror.UnitTests;

public class LockdownAndPairingTests
{
    [Fact]
    public async Task Framing_RoundTrip()
    {
        var ms = new MemoryStream();
        await LockdownFraming.WriteAsync(ms, new PlistDictionary { ["Request"] = "QueryType", ["Label"] = "t" }, CancellationToken.None);
        Assert.Equal((uint)(ms.Length - 4), System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(ms.ToArray()));
        ms.Position = 0;
        var d = await LockdownFraming.ReadAsync(ms, CancellationToken.None);
        Assert.Equal("QueryType", d.GetString("Request"));
    }

    [Fact]
    public void LockdownException_ClassifiesErrors()
    {
        Assert.True(new LockdownException("x", "PairingDialogResponsePending").IsPairingDialogPending);
        Assert.True(new LockdownException("x", "PasswordProtected").IsPasswordProtected);
        Assert.True(new LockdownException("x", "UserDeniedPairing").IsUserDenied);
        Assert.True(new LockdownException("x", "InvalidHostID").IsInvalidHostId);
    }

    private static (RSA Key, byte[] Pem) DeviceKey()
    {
        var rsa = RSA.Create(2048);
        return (rsa, Encoding.ASCII.GetBytes(rsa.ExportRSAPublicKeyPem()));
    }

    [Fact]
    public void Generator_ProducesValidChain()
    {
        var (deviceKey, pem) = DeviceKey();
        using var _ = deviceKey;
        var record = PairRecordGenerator.Generate(pem, "SYSTEM-BUID-1");

        using var root = X509Certificate2.CreateFromPem(Encoding.ASCII.GetString(record.RootCertificate));
        using var host = X509Certificate2.CreateFromPem(Encoding.ASCII.GetString(record.HostCertificate));
        using var device = X509Certificate2.CreateFromPem(Encoding.ASCII.GetString(record.DeviceCertificate));

        // Root is a CA, host/device are not.
        Assert.True(root.Extensions.OfType<X509BasicConstraintsExtension>().Single().CertificateAuthority);
        Assert.False(host.Extensions.OfType<X509BasicConstraintsExtension>().Single().CertificateAuthority);
        Assert.False(device.Extensions.OfType<X509BasicConstraintsExtension>().Single().CertificateAuthority);
        Assert.NotNull(device.Extensions.OfType<X509SubjectKeyIdentifierExtension>().SingleOrDefault());

        // Device certificate carries the device's own public key.
        Assert.Equal(deviceKey.ExportRSAPublicKey(), device.GetRSAPublicKey()!.ExportRSAPublicKey());

        // Host and device certificates are signed by the root key; root is self-signed.
        // (Verified directly: the empty subject/issuer names, as issued by libimobiledevice, defeat name-based chain building.)
        using var rootKey = root.GetRSAPublicKey()!;
        foreach (var cert in new[] { root, host, device })
        {
            Assert.True(SignedBy(cert, rootKey), "certificate signature does not verify with the root key");
        }
        Assert.False(SignedBy(host, deviceKey));

        Assert.True((root.NotAfter - root.NotBefore).TotalDays > 3600);
        Assert.Equal(36, record.HostId.Length);
        Assert.Equal("SYSTEM-BUID-1", record.SystemBuid);
    }

    private static bool SignedBy(X509Certificate2 cert, RSA key)
    {
        var reader = new System.Formats.Asn1.AsnReader(cert.RawData, System.Formats.Asn1.AsnEncodingRules.DER);
        var seq = reader.ReadSequence();
        var tbs = seq.ReadEncodedValue();
        seq.ReadSequence(); // signatureAlgorithm
        var signature = seq.ReadBitString(out _);
        return key.VerifyData(tbs.Span, signature, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
    }

    [Fact]
    public void PairRecord_RoundTripAndRequestExcludesPrivateKeys()
    {
        var (deviceKey, pem) = DeviceKey();
        using var _ = deviceKey;
        var record = PairRecordGenerator.Generate(pem, "BUID");
        record.EscrowBag = [1, 2, 3];
        record.WifiMacAddress = "aa:bb:cc:dd:ee:ff";

        var restored = PairRecord.FromBytes(record.ToBytes());
        Assert.Equal(record.HostId, restored.HostId);
        Assert.Equal(record.HostPrivateKey, restored.HostPrivateKey);
        Assert.Equal(new byte[] { 1, 2, 3 }, restored.EscrowBag);
        Assert.Equal("aa:bb:cc:dd:ee:ff", restored.WifiMacAddress);

        var request = record.ToPairRequestPlist();
        Assert.False(request.ContainsKey("HostPrivateKey"));
        Assert.False(request.ContainsKey("RootPrivateKey"));
        Assert.True(request.ContainsKey("DeviceCertificate"));
    }

    [Fact]
    public void HostCertificate_HasUsablePrivateKey()
    {
        var (deviceKey, pem) = DeviceKey();
        using var _ = deviceKey;
        var record = PairRecordGenerator.Generate(pem, "BUID");
        using var cert = record.CreateHostCertificateWithKey();
        Assert.True(cert.HasPrivateKey);
        using var rsa = cert.GetRSAPrivateKey()!;
        byte[] data = [1, 2, 3];
        var sig = rsa.SignData(data, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        Assert.True(cert.GetRSAPublicKey()!.VerifyData(data, sig, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1));
    }
}
