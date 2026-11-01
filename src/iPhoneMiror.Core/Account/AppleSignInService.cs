using System.Security.Cryptography;
using System.Text.Json;
using System.Web;
using iPhoneMiror.Core.Abstractions;
using iPhoneMiror.Core.Settings;
using Microsoft.Extensions.Logging;

namespace iPhoneMiror.Core.Account;

public interface IBrowserLauncher
{
    Task<bool> OpenAsync(Uri uri);
}

/// <summary>
/// Sign in with Apple through the user's browser (OAuth 2.0 / OpenID Connect, response_mode=fragment, no scopes).
/// Apple redirects to the configured HTTPS relay page, which forwards the result to iphonemiror://auth/callback.
/// Only the verified identity (subject, e-mail) is stored, in Windows Credential Manager. Passwords never pass
/// through this application, and signing in does not unlock Apple's private iPhone Mirroring protocol.
/// </summary>
public sealed class AppleSignInService : IAppleAccountService
{
    public const string CredentialTarget = "iPhoneMiror/AppleAccount";
    public const string CallbackScheme = "iphonemiror";
    public static readonly Uri AuthorizeEndpoint = new("https://appleid.apple.com/auth/authorize");

    private readonly ISettingsStore _settings;
    private readonly ICredentialStore _credentials;
    private readonly AppleIdTokenValidator _validator;
    private readonly IBrowserLauncher _browser;
    private readonly ILogger<AppleSignInService> _logger;
    private readonly TimeProvider _time;
    private (string State, string Nonce, DateTimeOffset Expires)? _pending;

    public AppleSignInService(ISettingsStore settings, ICredentialStore credentials, AppleIdTokenValidator validator, IBrowserLauncher browser,
        ILogger<AppleSignInService> logger, TimeProvider? time = null)
    {
        _settings = settings;
        _credentials = credentials;
        _validator = validator;
        _browser = browser;
        _logger = logger;
        _time = time ?? TimeProvider.System;
        Status = new AppleAccountStatus(AppleAccountState.NotConfigured, "Sign in with Apple is not configured.");
    }

    public AppleAccountStatus Status { get; private set; }
    public event EventHandler<AppleAccountStatus>? StatusChanged;

    private AppleAccountSettings Config => _settings.Current.AppleAccount;

    public bool IsConfigured =>
        !string.IsNullOrWhiteSpace(Config.ServicesId) &&
        Uri.TryCreate(Config.RedirectUri, UriKind.Absolute, out var u) && u.Scheme == Uri.UriSchemeHttps;

    public static Uri BuildAuthorizeUri(string servicesId, string redirectUri, string state, string nonce)
    {
        var q = HttpUtility.ParseQueryString(string.Empty);
        q["client_id"] = servicesId;
        q["redirect_uri"] = redirectUri;
        q["response_type"] = "code id_token";
        q["response_mode"] = "fragment";
        q["state"] = state;
        q["nonce"] = nonce;
        return new UriBuilder(AuthorizeEndpoint) { Query = q.ToString() }.Uri;
    }

    public Task LoadAsync(CancellationToken cancellationToken = default)
    {
        var stored = _credentials.Read(CredentialTarget);
        if (stored is { } s)
        {
            try
            {
                var data = JsonSerializer.Deserialize<StoredIdentity>(s.Secret);
                if (data is not null)
                {
                    SetStatus(new AppleAccountStatus(AppleAccountState.SignedIn, "Apple ID verified.", data.Subject, data.Email, data.AuthenticatedAt, data.TokenExpiresAt));
                    return Task.CompletedTask;
                }
            }
            catch (JsonException ex)
            {
                _logger.LogWarning(ex, "Stored Apple identity is unreadable; removing it.");
                _credentials.Delete(CredentialTarget);
            }
        }
        SetStatus(IsConfigured
            ? new AppleAccountStatus(AppleAccountState.SignedOut, "Not signed in.")
            : new AppleAccountStatus(AppleAccountState.NotConfigured, "Enter your Services ID and HTTPS redirect URI in Apple Account settings to enable Sign in with Apple."));
        return Task.CompletedTask;
    }

    public async Task BeginSignInAsync(CancellationToken cancellationToken = default)
    {
        if (!IsConfigured) throw new InvalidOperationException("Sign in with Apple is not configured. Enter a Services ID and an HTTPS redirect URI first.");
        var state = AppleIdTokenValidator.Base64UrlEncode(RandomNumberGenerator.GetBytes(24));
        var nonce = AppleIdTokenValidator.Base64UrlEncode(RandomNumberGenerator.GetBytes(24));
        _pending = (state, nonce, _time.GetUtcNow().AddMinutes(10));
        var uri = BuildAuthorizeUri(Config.ServicesId, Config.RedirectUri, state, nonce);
        SetStatus(new AppleAccountStatus(AppleAccountState.SigningIn, "Continue in your browser…"));
        if (!await _browser.OpenAsync(uri).ConfigureAwait(false))
        {
            _pending = null;
            SetStatus(new AppleAccountStatus(AppleAccountState.Error, "The browser could not be opened."));
        }
    }

    public async Task<AppleAccountStatus> CompleteSignInAsync(Uri callbackUri, CancellationToken cancellationToken = default)
    {
        var raw = string.IsNullOrEmpty(callbackUri.Query) ? callbackUri.Fragment.TrimStart('#') : callbackUri.Query.TrimStart('?');
        var q = HttpUtility.ParseQueryString(raw);
        try
        {
            if (q["error"] is { } error)
                throw new AppleIdTokenException(error == "user_cancelled_authorize" ? "Sign in was cancelled." : $"Apple returned an error: {error}.");
            var pending = _pending ?? throw new AppleIdTokenException("No sign-in is in progress. Start again from iPhoneMiror.");
            if (_time.GetUtcNow() > pending.Expires) throw new AppleIdTokenException("The sign-in request expired. Please try again.");
            if (q["state"] != pending.State) throw new AppleIdTokenException("The sign-in response does not match this request (state mismatch).");
            var idToken = q["id_token"] ?? throw new AppleIdTokenException("Apple did not return an identity token.");

            var identity = await _validator.ValidateAsync(idToken, Config.ServicesId, pending.Nonce, cancellationToken).ConfigureAwait(false);
            _pending = null;
            var stored = new StoredIdentity(identity.Subject, identity.Email, _time.GetUtcNow(), identity.ExpiresAt);
            _credentials.Write(CredentialTarget, identity.Subject, JsonSerializer.Serialize(stored));
            SetStatus(new AppleAccountStatus(AppleAccountState.SignedIn, "Apple ID verified.", identity.Subject, identity.Email, stored.AuthenticatedAt, identity.ExpiresAt));
        }
        catch (Exception ex) when (ex is AppleIdTokenException or HttpRequestException or CryptographicException)
        {
            _logger.LogWarning(ex, "Sign in with Apple failed.");
            SetStatus(new AppleAccountStatus(AppleAccountState.Error, ex.Message));
        }
        return Status;
    }

    public Task SignOutAsync()
    {
        _credentials.Delete(CredentialTarget);
        _pending = null;
        return LoadAsync();
    }

    private void SetStatus(AppleAccountStatus status)
    {
        Status = status;
        StatusChanged?.Invoke(this, status);
    }

    private sealed record StoredIdentity(string Subject, string? Email, DateTimeOffset AuthenticatedAt, DateTimeOffset TokenExpiresAt);
}
