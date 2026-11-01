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

        // Host and device certificates verify against the root signature.
        foreach (var leaf in new[] { host, device })
        {
            using var chain = new X509Chain();
            chain.ChainPolicy.TrustMode = X509ChainTrustMode.CustomRootTrust;
            chain.ChainPolicy.CustomTrustStore.Add(root);
            chain.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck;
            chain.ChainPolicy.VerificationFlags = X509VerificationFlags.IgnoreWrongUsage;
            Assert.True(chain.Build(leaf), string.Join("; ", chain.ChainStatus.Select(s => s.StatusInformation)));
        }

        Assert.True((root.NotAfter - root.NotBefore).TotalDays > 3600);
        Assert.Equal(36, record.HostId.Length);
        Assert.Equal("SYSTEM-BUID-1", record.SystemBuid);
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
