using System.Net.Security;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using iPhoneMiror.Connection.Lockdown;
using iPhoneMiror.Core.Plist;

namespace iPhoneMiror.IntegrationTests.Fakes;

/// <summary>
/// Emulates lockdownd: QueryType, GetValue, Pair (with "Trust" dialog pending / denied), StartSession with a real TLS
/// upgrade using the device certificate issued by the host during pairing, and StopSession.
/// </summary>
public sealed class FakeLockdownd
{
    private readonly RSA _deviceKey = RSA.Create(2048);
    private byte[]? _deviceCertificatePem;
    private byte[]? _hostCertificatePem;

    public string ProductType { get; init; } = "iPhone17,1";
    public string ProductVersion { get; init; } = "26.0";
    public string DeviceName { get; init; } = "Integration iPhone";
    public string WifiAddress { get; init; } = "aa:bb:cc:dd:ee:ff";
    public int PendingDialogs { get; set; } = 2;
    public bool DenyPairing { get; set; }
    public string? PairedHostId { get; private set; }
    public int PairRequests { get; private set; }
    public int TlsSessions { get; private set; }

    public async Task HandleAsync(Stream stream, CancellationToken ct)
    {
        Stream current = stream;
        var sessionActive = false;
        try
        {
            while (!ct.IsCancellationRequested)
            {
                var req = await LockdownFraming.ReadAsync(current, ct);
                var request = req.GetString("Request");
                var reply = new PlistDictionary { ["Request"] = request ?? "" };
                switch (request)
                {
                    case "QueryType":
                        reply["Type"] = "com.apple.mobile.lockdown";
                        break;
                    case "GetValue":
                        var domain = req.GetString("Domain");
                        var key = req.GetString("Key");
                        if (domain == "com.apple.mobile.battery")
                        {
                            if (!sessionActive) { reply["Error"] = "InvalidService"; break; }
                            reply["Value"] = new PlistDictionary { ["BatteryCurrentCapacity"] = 87L, ["BatteryIsCharging"] = true };
                        }
                        else if (key == "DevicePublicKey") reply["Value"] = Encoding.ASCII.GetBytes(_deviceKey.ExportRSAPublicKeyPem());
                        else if (key == "WiFiAddress") reply["Value"] = WifiAddress;
                        else if (key is null)
                        {
                            var values = new PlistDictionary
                            {
                                ["ProductType"] = ProductType,
                                ["ProductVersion"] = ProductVersion,
                                ["BuildVersion"] = "23A000",
                                ["DeviceClass"] = "iPhone",
                            };
                            if (sessionActive) { values["DeviceName"] = DeviceName; values["WiFiAddress"] = WifiAddress; }
                            reply["Value"] = values;
                        }
                        else reply["Error"] = "MissingValue";
                        break;
                    case "Pair":
                        PairRequests++;
                        var record = req.GetDictionary("PairRecord");
                        if (record is null || record.ContainsKey("HostPrivateKey") || record.ContainsKey("RootPrivateKey"))
                        {
                            reply["Error"] = "InvalidPairRecord";
                        }
                        else if (DenyPairing) reply["Error"] = "UserDeniedPairing";
                        else if (PendingDialogs > 0) { PendingDialogs--; reply["Error"] = "PairingDialogResponsePending"; }
                        else
                        {
                            PairedHostId = record.GetString("HostID");
                            _deviceCertificatePem = record.GetData("DeviceCertificate");
                            _hostCertificatePem = record.GetData("HostCertificate");
                            reply["EscrowBag"] = new byte[] { 0xE5, 0xC0 };
                        }
                        break;
                    case "StartSession":
                        if (PairedHostId is null || req.GetString("HostID") != PairedHostId)
                        {
                            reply["Error"] = "InvalidHostID";
                            break;
                        }
                        reply["SessionID"] = Guid.NewGuid().ToString("D");
                        reply["EnableSessionSSL"] = true;
                        await LockdownFraming.WriteAsync(current, reply, ct);
                        current = await UpgradeToTlsAsync(stream, ct);
                        sessionActive = true;
                        TlsSessions++;
                        continue;
                    case "StopSession":
                        sessionActive = false;
                        break;
                    default:
                        reply["Error"] = "InvalidRequest";
                        break;
                }
                await LockdownFraming.WriteAsync(current, reply, ct);
            }
        }
        catch (EndOfStreamException) { }
        catch (IOException) { }
        catch (OperationCanceledException) { }
        finally
        {
            if (!ReferenceEquals(current, stream)) await current.DisposeAsync();
        }
    }

    private async Task<Stream> UpgradeToTlsAsync(Stream inner, CancellationToken ct)
    {
        using var cert = X509Certificate2.CreateFromPem(Encoding.ASCII.GetString(_deviceCertificatePem!));
        using var withKey = cert.CopyWithPrivateKey(_deviceKey);
        var serverCert = X509CertificateLoader.LoadPkcs12(withKey.Export(X509ContentType.Pkcs12), null);
        using var expectedHost = X509Certificate2.CreateFromPem(Encoding.ASCII.GetString(_hostCertificatePem!));
        var expectedHostKey = expectedHost.GetPublicKey();
        var ssl = new SslStream(inner, leaveInnerStreamOpen: true, (_, client, _, _) =>
            client is not null && X509CertificateLoader.LoadCertificate(client.GetRawCertData()).GetPublicKey().AsSpan().SequenceEqual(expectedHostKey));
        await ssl.AuthenticateAsServerAsync(new SslServerAuthenticationOptions
        {
            ServerCertificate = serverCert,
            ClientCertificateRequired = true,
            CertificateRevocationCheckMode = X509RevocationMode.NoCheck,
        }, ct);
        return ssl;
    }
}
