using System.Net.Security;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;
using iPhoneMiror.Connection.Pairing;
using iPhoneMiror.Core.Plist;

namespace iPhoneMiror.Connection.Lockdown;

/// <summary>
/// Client for lockdownd (TCP port 62078 on the device), the iOS service that exposes device information,
/// handles pairing and opens TLS sessions. Protocol reference: libimobiledevice src/lockdown.c (LGPL-2.1).
/// </summary>
public sealed class LockdownClient : IAsyncDisposable
{
    public const string ServiceType = "com.apple.mobile.lockdown";
    public const string ProtocolVersion = "2";

    private readonly string _label;
    private readonly TimeSpan _timeout;
    private Stream _stream;
    private readonly Stream _rawStream;

    public LockdownClient(Stream stream, string label = "iPhoneMiror", TimeSpan? timeout = null)
    {
        _stream = stream;
        _rawStream = stream;
        _label = label;
        _timeout = timeout ?? TimeSpan.FromSeconds(15);
    }

    public string? SessionId { get; private set; }
    public bool IsSessionEncrypted => !ReferenceEquals(_stream, _rawStream);

    public async Task<PlistDictionary> RequestAsync(PlistDictionary request, CancellationToken ct = default)
    {
        request["Label"] = _label;
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(_timeout);
        await LockdownFraming.WriteAsync(_stream, request, timeout.Token).ConfigureAwait(false);
        var response = await LockdownFraming.ReadAsync(_stream, timeout.Token).ConfigureAwait(false);
        var error = response.GetString("Error");
        if (error is not null) throw new LockdownException($"lockdownd {request.GetString("Request")} failed", error);
        return response;
    }

    public async Task<string> QueryTypeAsync(CancellationToken ct = default)
    {
        var r = await RequestAsync(new PlistDictionary { ["Request"] = "QueryType" }, ct).ConfigureAwait(false);
        var type = r.GetString("Type") ?? "";
        if (type != ServiceType) throw new LockdownException($"Unexpected lockdown service type '{type}'.");
        return type;
    }

    /// <summary>GetValue. With no key, returns the whole domain as a dictionary.</summary>
    public async Task<object?> GetValueAsync(string? domain, string? key, CancellationToken ct = default)
    {
        var req = new PlistDictionary { ["Request"] = "GetValue" };
        if (domain is not null) req["Domain"] = domain;
        if (key is not null) req["Key"] = key;
        var r = await RequestAsync(req, ct).ConfigureAwait(false);
        return r.TryGetValue("Value", out var v) ? v : null;
    }

    /// <summary>Sends "Pair" with the given record (private keys are never sent). Returns the EscrowBag if provided.</summary>
    public async Task<byte[]?> PairAsync(PairRecord record, CancellationToken ct = default)
    {
        var req = new PlistDictionary
        {
            ["Request"] = "Pair",
            ["PairRecord"] = record.ToPairRequestPlist(),
            ["ProtocolVersion"] = ProtocolVersion,
            ["PairingOptions"] = new PlistDictionary { ["ExtendedPairingErrors"] = true },
        };
        var r = await RequestAsync(req, ct).ConfigureAwait(false);
        return r.GetData("EscrowBag");
    }

    /// <summary>Starts an authenticated session and, if requested by the device, upgrades the connection to TLS.</summary>
    public async Task<string> StartSessionAsync(PairRecord record, CancellationToken ct = default)
    {
        var req = new PlistDictionary
        {
            ["Request"] = "StartSession",
            ["HostID"] = record.HostId,
            ["SystemBUID"] = record.SystemBuid,
        };
        var r = await RequestAsync(req, ct).ConfigureAwait(false);
        SessionId = r.GetString("SessionID") ?? throw new LockdownException("StartSession returned no SessionID.");
        if (r.GetBoolean("EnableSessionSSL") == true)
        {
            await UpgradeToTlsAsync(record, ct).ConfigureAwait(false);
        }
        return SessionId;
    }

    public async Task StopSessionAsync(CancellationToken ct = default)
    {
        if (SessionId is null) return;
        try
        {
            await RequestAsync(new PlistDictionary { ["Request"] = "StopSession", ["SessionID"] = SessionId }, ct).ConfigureAwait(false);
        }
        finally
        {
            SessionId = null;
        }
    }

    private async Task UpgradeToTlsAsync(PairRecord record, CancellationToken ct)
    {
        var clientCert = record.CreateHostCertificateWithKey();
        var expectedDevice = X509CertificateLoader.LoadCertificate(record.DeviceCertificatePemBytesToDer());
        var ssl = new SslStream(_rawStream, leaveInnerStreamOpen: true, (_, cert, _, _) =>
        {
            // Pin the device certificate from the pair record instead of using the system trust store.
            if (cert is null) return false;
            using var presented = X509CertificateLoader.LoadCertificate(cert.GetRawCertData());
            return presented.GetPublicKey().AsSpan().SequenceEqual(expectedDevice.GetPublicKey());
        });
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(_timeout);
        try
        {
            await ssl.AuthenticateAsClientAsync(new SslClientAuthenticationOptions
            {
                TargetHost = "iPhone",
                ClientCertificates = new X509CertificateCollection { clientCert },
                // Always present the host certificate, whatever CA list the device advertises.
                LocalCertificateSelectionCallback = (_, _, _, _, _) => clientCert,
                EnabledSslProtocols = SslProtocols.None, // let the OS pick the best supported version
                CertificateRevocationCheckMode = X509RevocationMode.NoCheck,
            }, timeout.Token).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is AuthenticationException or IOException)
        {
            await ssl.DisposeAsync().ConfigureAwait(false);
            throw new LockdownException("TLS handshake with lockdownd failed. The pair record may be stale; re-pair the device.", ex);
        }
        _stream = ssl;
    }

    public async ValueTask DisposeAsync()
    {
        if (!ReferenceEquals(_stream, _rawStream)) await _stream.DisposeAsync().ConfigureAwait(false);
        await _rawStream.DisposeAsync().ConfigureAwait(false);
    }
}
