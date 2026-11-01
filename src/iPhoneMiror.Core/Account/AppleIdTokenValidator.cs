using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace iPhoneMiror.Core.Account;

public sealed record AppleIdentity(string Subject, string? Email, bool? EmailVerified, bool? IsPrivateEmail, DateTimeOffset IssuedAt, DateTimeOffset ExpiresAt);

public sealed class AppleIdTokenException : Exception
{
    public AppleIdTokenException(string message) : base(message) { }
}

/// <summary>
/// Validates a Sign in with Apple identity token (RS256 JWT) against Apple's published keys
/// (https://appleid.apple.com/auth/keys): signature, issuer, audience, expiry and nonce.
/// </summary>
public sealed class AppleIdTokenValidator
{
    public const string Issuer = "https://appleid.apple.com";
    public static readonly Uri KeysEndpoint = new("https://appleid.apple.com/auth/keys");

    private readonly HttpClient _http;
    private readonly TimeProvider _time;
    private JsonArray? _cachedKeys;
    private DateTimeOffset _cachedAt;

    public AppleIdTokenValidator(HttpClient http, TimeProvider? time = null)
    {
        _http = http;
        _time = time ?? TimeProvider.System;
    }

    public async Task<AppleIdentity> ValidateAsync(string idToken, string expectedAudience, string? expectedNonce, CancellationToken ct = default)
    {
        var parts = idToken.Split('.');
        if (parts.Length != 3) throw new AppleIdTokenException("Identity token is not a JWT.");

        JsonNode header, payload;
        try
        {
            header = JsonNode.Parse(Base64UrlDecode(parts[0])) ?? throw new AppleIdTokenException("Empty JWT header.");
            payload = JsonNode.Parse(Base64UrlDecode(parts[1])) ?? throw new AppleIdTokenException("Empty JWT payload.");
        }
        catch (Exception ex) when (ex is JsonException or FormatException)
        {
            throw new AppleIdTokenException("Identity token is malformed.");
        }

        if (header["alg"]?.GetValue<string>() != "RS256") throw new AppleIdTokenException("Unexpected token algorithm.");
        var kid = header["kid"]?.GetValue<string>() ?? throw new AppleIdTokenException("Token has no key id.");

        var key = await FindKeyAsync(kid, ct).ConfigureAwait(false) ?? throw new AppleIdTokenException("Signing key not found in Apple's key set.");
        using var rsa = RSA.Create(new RSAParameters
        {
            Modulus = Base64UrlDecode(key["n"]!.GetValue<string>()),
            Exponent = Base64UrlDecode(key["e"]!.GetValue<string>()),
        });
        var signed = Encoding.ASCII.GetBytes(parts[0] + "." + parts[1]);
        if (!rsa.VerifyData(signed, Base64UrlDecode(parts[2]), HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1))
            throw new AppleIdTokenException("Identity token signature is invalid.");

        if (payload["iss"]?.GetValue<string>() != Issuer) throw new AppleIdTokenException("Identity token issuer is not Apple.");
        var aud = payload["aud"];
        var audOk = aud switch
        {
            JsonArray arr => arr.Any(a => a?.GetValue<string>() == expectedAudience),
            JsonValue v => v.GetValue<string>() == expectedAudience,
            _ => false,
        };
        if (!audOk) throw new AppleIdTokenException("Identity token was issued for a different Services ID.");

        var now = _time.GetUtcNow();
        var exp = DateTimeOffset.FromUnixTimeSeconds(ReadLong(payload["exp"]) ?? 0);
        var iat = DateTimeOffset.FromUnixTimeSeconds(ReadLong(payload["iat"]) ?? 0);
        if (exp <= now.AddMinutes(-2)) throw new AppleIdTokenException("Identity token has expired.");

        if (expectedNonce is not null)
        {
            var nonce = payload["nonce"]?.GetValue<string>();
            var hashed = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(expectedNonce)));
            if (nonce != expectedNonce && nonce != hashed) throw new AppleIdTokenException("Identity token nonce does not match this sign-in.");
        }

        var sub = payload["sub"]?.GetValue<string>() ?? throw new AppleIdTokenException("Identity token has no subject.");
        return new AppleIdentity(sub, payload["email"]?.GetValue<string>(), ReadBool(payload["email_verified"]), ReadBool(payload["is_private_email"]), iat, exp);
    }

    private async Task<JsonObject?> FindKeyAsync(string kid, CancellationToken ct)
    {
        for (var attempt = 0; attempt < 2; attempt++)
        {
            if (_cachedKeys is null || attempt == 1 || _time.GetUtcNow() - _cachedAt > TimeSpan.FromHours(12))
            {
                var json = await _http.GetStringAsync(KeysEndpoint, ct).ConfigureAwait(false);
                _cachedKeys = JsonNode.Parse(json)?["keys"]?.AsArray() ?? [];
                _cachedAt = _time.GetUtcNow();
            }
            var match = _cachedKeys.OfType<JsonObject>().FirstOrDefault(k => k["kid"]?.GetValue<string>() == kid);
            if (match is not null) return match;
        }
        return null;
    }

    private static long? ReadLong(JsonNode? n) => n switch
    {
        null => null,
        JsonValue v when v.TryGetValue<long>(out var l) => l,
        JsonValue v when v.TryGetValue<double>(out var d) => (long)d,
        JsonValue v when v.TryGetValue<string>(out var s) && long.TryParse(s, out var p) => p,
        _ => null,
    };

    private static bool? ReadBool(JsonNode? n) => n switch
    {
        null => null,
        JsonValue v when v.TryGetValue<bool>(out var b) => b,
        JsonValue v when v.TryGetValue<string>(out var s) => s == "true",
        _ => null,
    };

    public static byte[] Base64UrlDecode(string s)
    {
        var t = s.Replace('-', '+').Replace('_', '/');
        t = t.PadRight(t.Length + (4 - t.Length % 4) % 4, '=');
        return Convert.FromBase64String(t);
    }

    public static string Base64UrlEncode(byte[] b) => Convert.ToBase64String(b).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}
