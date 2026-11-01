using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using System.Web;
using iPhoneMiror.Core.Abstractions;
using iPhoneMiror.Core.Account;
using iPhoneMiror.Core.Settings;
using Microsoft.Extensions.Logging.Abstractions;

namespace iPhoneMiror.UnitTests;

public class AppleAccountTests
{
    private const string Audience = "com.example.iphonemiror.signin";
    private static readonly RSA Key = RSA.Create(2048);

    private sealed class KeysHandler : HttpMessageHandler
    {
        public int Calls;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Calls++;
            Assert.Equal(AppleIdTokenValidator.KeysEndpoint, request.RequestUri);
            var p = Key.ExportParameters(false);
            var jwks = new JsonObject
            {
                ["keys"] = new JsonArray
                {
                    new JsonObject { ["kty"] = "RSA", ["kid"] = "TEST", ["use"] = "sig", ["alg"] = "RS256",
                        ["n"] = AppleIdTokenValidator.Base64UrlEncode(p.Modulus!), ["e"] = AppleIdTokenValidator.Base64UrlEncode(p.Exponent!) },
                },
            };
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(jwks.ToJsonString()) });
        }
    }

    internal static string Token(JsonObject claims, string kid = "TEST", RSA? key = null)
    {
        var header = AppleIdTokenValidator.Base64UrlEncode(Encoding.UTF8.GetBytes(new JsonObject { ["alg"] = "RS256", ["kid"] = kid }.ToJsonString()));
        var payload = AppleIdTokenValidator.Base64UrlEncode(Encoding.UTF8.GetBytes(claims.ToJsonString()));
        var sig = (key ?? Key).SignData(Encoding.ASCII.GetBytes(header + "." + payload), HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        return header + "." + payload + "." + AppleIdTokenValidator.Base64UrlEncode(sig);
    }

    private static JsonObject Claims(string nonce, long? exp = null) => new()
    {
        ["iss"] = "https://appleid.apple.com",
        ["aud"] = Audience,
        ["sub"] = "000123.abc",
        ["email"] = "user@privaterelay.appleid.com",
        ["email_verified"] = "true",
        ["nonce"] = nonce,
        ["iat"] = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
        ["exp"] = exp ?? DateTimeOffset.UtcNow.AddMinutes(10).ToUnixTimeSeconds(),
    };

    [Fact]
    public async Task Validator_AcceptsGenuineToken()
    {
        var v = new AppleIdTokenValidator(new HttpClient(new KeysHandler()));
        var id = await v.ValidateAsync(Token(Claims("n1")), Audience, "n1");
        Assert.Equal("000123.abc", id.Subject);
        Assert.Equal("user@privaterelay.appleid.com", id.Email);
        Assert.True(id.EmailVerified);
    }

    [Fact]
    public async Task Validator_RejectsTampering()
    {
        var v = new AppleIdTokenValidator(new HttpClient(new KeysHandler()));
        using var other = RSA.Create(2048);
        await Assert.ThrowsAsync<AppleIdTokenException>(() => v.ValidateAsync(Token(Claims("n"), key: other), Audience, "n"));
        await Assert.ThrowsAsync<AppleIdTokenException>(() => v.ValidateAsync(Token(Claims("n")), "other.audience", "n"));
        await Assert.ThrowsAsync<AppleIdTokenException>(() => v.ValidateAsync(Token(Claims("n")), Audience, "different-nonce"));
        await Assert.ThrowsAsync<AppleIdTokenException>(() => v.ValidateAsync(Token(Claims("n", DateTimeOffset.UtcNow.AddHours(-1).ToUnixTimeSeconds())), Audience, "n"));
        var bad = Claims("n");
        bad["iss"] = "https://evil.example";
        await Assert.ThrowsAsync<AppleIdTokenException>(() => v.ValidateAsync(Token(bad), Audience, "n"));
        await Assert.ThrowsAsync<AppleIdTokenException>(() => v.ValidateAsync("abc.def", Audience, null));
    }

    private sealed class MemoryCredentials : ICredentialStore
    {
        public Dictionary<string, (string, string)> Items { get; } = [];
        public void Write(string target, string userName, string secret) => Items[target] = (userName, secret);
        public (string UserName, string Secret)? Read(string target) => Items.TryGetValue(target, out var v) ? v : null;
        public bool Delete(string target) => Items.Remove(target);
    }

    private sealed class CapturingBrowser : IBrowserLauncher
    {
        public Uri? Opened;
        public Task<bool> OpenAsync(Uri uri) { Opened = uri; return Task.FromResult(true); }
    }

    private sealed class MemorySettings : ISettingsStore
    {
        public AppSettings Current { get; } = new();
        public event EventHandler<AppSettings>? Changed { add { } remove { } }
        public Task LoadAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task SaveAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task UpdateAsync(Action<AppSettings> mutate, CancellationToken cancellationToken = default) { mutate(Current); return Task.CompletedTask; }
    }

    [Fact]
    public async Task Service_FullFlow_StoresIdentityNotPassword()
    {
        var settings = new MemorySettings();
        var creds = new MemoryCredentials();
        var browser = new CapturingBrowser();
        var svc = new AppleSignInService(settings, creds, new AppleIdTokenValidator(new HttpClient(new KeysHandler())), browser, NullLogger<AppleSignInService>.Instance);

        await svc.LoadAsync();
        Assert.Equal(AppleAccountState.NotConfigured, svc.Status.State);
        await Assert.ThrowsAsync<InvalidOperationException>(() => svc.BeginSignInAsync());

        settings.Current.AppleAccount.ServicesId = Audience;
        settings.Current.AppleAccount.RedirectUri = "https://example.github.io/apple-signin-relay/";
        await svc.BeginSignInAsync();
        Assert.Equal(AppleAccountState.SigningIn, svc.Status.State);
        var q = HttpUtility.ParseQueryString(browser.Opened!.Query);
        Assert.Equal("appleid.apple.com", browser.Opened.Host);
        Assert.Equal("/auth/authorize", browser.Opened.AbsolutePath);
        Assert.Equal(Audience, q["client_id"]);
        Assert.Equal("fragment", q["response_mode"]);
        Assert.Equal("code id_token", q["response_type"]);
        Assert.Null(q["scope"]);

        // Wrong state is rejected.
        var wrong = await svc.CompleteSignInAsync(new Uri($"iphonemiror://auth/callback?state=nope&id_token={Token(Claims(q["nonce"]!))}"));
        Assert.Equal(AppleAccountState.Error, wrong.State);

        await svc.BeginSignInAsync();
        q = HttpUtility.ParseQueryString(browser.Opened!.Query);
        var ok = await svc.CompleteSignInAsync(new Uri($"iphonemiror://auth/callback?state={q["state"]}&code=c&id_token={Token(Claims(q["nonce"]!))}"));
        Assert.Equal(AppleAccountState.SignedIn, ok.State);
        Assert.Equal("000123.abc", ok.Subject);
        var stored = creds.Items[AppleSignInService.CredentialTarget];
        Assert.DoesNotContain("password", stored.Item2, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("id_token", stored.Item2);

        // Reload from Credential Manager.
        var svc2 = new AppleSignInService(settings, creds, new AppleIdTokenValidator(new HttpClient(new KeysHandler())), browser, NullLogger<AppleSignInService>.Instance);
        await svc2.LoadAsync();
        Assert.Equal(AppleAccountState.SignedIn, svc2.Status.State);

        await svc2.SignOutAsync();
        Assert.Equal(AppleAccountState.SignedOut, svc2.Status.State);
        Assert.Empty(creds.Items);
    }

    [Fact]
    public async Task Service_UserCancelled()
    {
        var settings = new MemorySettings();
        settings.Current.AppleAccount.ServicesId = Audience;
        settings.Current.AppleAccount.RedirectUri = "https://example.org/relay";
        var svc = new AppleSignInService(settings, new MemoryCredentials(), new AppleIdTokenValidator(new HttpClient(new KeysHandler())), new CapturingBrowser(), NullLogger<AppleSignInService>.Instance);
        await svc.BeginSignInAsync();
        var r = await svc.CompleteSignInAsync(new Uri("iphonemiror://auth/callback?error=user_cancelled_authorize"));
        Assert.Equal(AppleAccountState.Error, r.State);
        Assert.Contains("cancelled", r.Message);
    }
}
