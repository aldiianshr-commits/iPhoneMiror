using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;

namespace iPhoneMiror.Connection.Pairing;

/// <summary>
/// Generates the root/host/device certificates for a new pair record, following libimobiledevice's
/// pair_record_generate_keys_and_certs (common/userpref.c): RSA-2048 root and host keys, serial 0,
/// 10-year validity, SHA-256 signatures, root is a CA, the device certificate wraps the device's own public key.
/// </summary>
public static class PairRecordGenerator
{
    private static readonly byte[] SerialZero = [0];

    public static PairRecord Generate(byte[] devicePublicKeyPem, string systemBuid, string? hostId = null, DateTimeOffset? now = null)
    {
        var start = (now ?? DateTimeOffset.UtcNow).AddMinutes(-5);
        var end = start.AddYears(10);
        // libimobiledevice issues these certificates with an empty subject/issuer (DER: empty SEQUENCE).
        var emptyName = new X500DistinguishedName(new byte[] { 0x30, 0x00 });

        using var rootKey = RSA.Create(2048);
        using var hostKey = RSA.Create(2048);
        using var deviceKey = RSA.Create();
        deviceKey.ImportFromPem(Encoding.ASCII.GetString(devicePublicKeyPem));

        // Root (self-signed CA)
        var rootReq = new CertificateRequest(emptyName, rootKey, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        rootReq.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, false, 0, true));
        using var rootCert = rootReq.Create(emptyName, X509SignatureGenerator.CreateForRSA(rootKey, RSASignaturePadding.Pkcs1), start, end, SerialZero);

        var rootGenerator = X509SignatureGenerator.CreateForRSA(rootKey, RSASignaturePadding.Pkcs1);

        // Host certificate signed by root
        var hostReq = new CertificateRequest(emptyName, hostKey, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        hostReq.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, true));
        hostReq.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.KeyEncipherment, true));
        using var hostCert = hostReq.Create(emptyName, rootGenerator, start, end, SerialZero);

        // Device certificate: device public key, signed by root
        var devicePublicKey = new PublicKey(deviceKey);
        var devReq = new CertificateRequest(emptyName, devicePublicKey, HashAlgorithmName.SHA256);
        devReq.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, true));
        devReq.CertificateExtensions.Add(new X509SubjectKeyIdentifierExtension(devicePublicKey, false));
        devReq.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.KeyEncipherment, true));
        using var devCert = devReq.Create(emptyName, rootGenerator, start, end, SerialZero);

        return new PairRecord
        {
            RootCertificate = Encoding.ASCII.GetBytes(rootCert.ExportCertificatePem() + "\n"),
            HostCertificate = Encoding.ASCII.GetBytes(hostCert.ExportCertificatePem() + "\n"),
            DeviceCertificate = Encoding.ASCII.GetBytes(devCert.ExportCertificatePem() + "\n"),
            RootPrivateKey = Encoding.ASCII.GetBytes(rootKey.ExportRSAPrivateKeyPem() + "\n"),
            HostPrivateKey = Encoding.ASCII.GetBytes(hostKey.ExportRSAPrivateKeyPem() + "\n"),
            HostId = hostId ?? Guid.NewGuid().ToString("D").ToUpperInvariant(),
            SystemBuid = systemBuid,
        };
    }
}
