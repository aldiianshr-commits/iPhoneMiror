namespace iPhoneMiror.Connection.Lockdown;

/// <summary>Error returned by lockdownd ("Error" key in a response) or a protocol failure.</summary>
public sealed class LockdownException : IOException
{
    public LockdownException(string message, string? errorCode = null) : base(errorCode is null ? message : $"{message} ({errorCode})")
    {
        ErrorCode = errorCode;
    }

    public LockdownException(string message, Exception inner) : base(message, inner) { }

    /// <summary>Raw lockdownd error string, e.g. "PairingDialogResponsePending", "PasswordProtected", "UserDeniedPairing", "InvalidHostID".</summary>
    public string? ErrorCode { get; }

    public bool IsPairingDialogPending => ErrorCode == "PairingDialogResponsePending";
    public bool IsPasswordProtected => ErrorCode == "PasswordProtected";
    public bool IsUserDenied => ErrorCode == "UserDeniedPairing";
    public bool IsInvalidHostId => ErrorCode is "InvalidHostID" or "MissingHostID";
}
