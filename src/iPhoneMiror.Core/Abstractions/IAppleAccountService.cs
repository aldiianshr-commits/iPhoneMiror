namespace iPhoneMiror.Core.Abstractions;

public enum AppleAccountState { NotConfigured, SignedOut, SigningIn, SignedIn, Error }

public sealed record AppleAccountStatus(
    AppleAccountState State,
    string Message,
    string? Subject = null,
    string? Email = null,
    DateTimeOffset? AuthenticatedAt = null,
    DateTimeOffset? ExpiresAt = null);

/// <summary>
/// Sign in with Apple (OpenID Connect). Provides identity only: it does NOT grant access to Apple's
/// private iPhone Mirroring/Continuity protocols, and passwords are never handled by this application.
/// </summary>
public interface IAppleAccountService
{
    AppleAccountStatus Status { get; }
    event EventHandler<AppleAccountStatus>? StatusChanged;

    bool IsConfigured { get; }

    /// <summary>Opens the system browser on Apple's authorization page.</summary>
    Task BeginSignInAsync(CancellationToken cancellationToken = default);

    /// <summary>Handles the redirect (iphonemiror://auth/callback?...) forwarded by the relay page.</summary>
    Task<AppleAccountStatus> CompleteSignInAsync(Uri callbackUri, CancellationToken cancellationToken = default);

    Task SignOutAsync();
    Task LoadAsync(CancellationToken cancellationToken = default);
}
