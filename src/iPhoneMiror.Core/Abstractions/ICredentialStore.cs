namespace iPhoneMiror.Core.Abstractions;

/// <summary>Secure secret storage (Windows Credential Manager on Windows).</summary>
public interface ICredentialStore
{
    void Write(string target, string userName, string secret);
    (string UserName, string Secret)? Read(string target);
    bool Delete(string target);
}

/// <summary>Protects small blobs at rest (DPAPI on Windows).</summary>
public interface IDataProtector
{
    byte[] Protect(byte[] plaintext);
    byte[] Unprotect(byte[] protectedData);
}
