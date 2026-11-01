using System.Security.Cryptography;
using iPhoneMiror.Core.Abstractions;

namespace iPhoneMiror.App.Services;

/// <summary>Protects pair records at rest with DPAPI (current user scope).</summary>
public sealed class DpapiDataProtector : IDataProtector
{
    private static readonly byte[] Entropy = "iPhoneMiror.PairRecord.v1"u8.ToArray();

    public byte[] Protect(byte[] plaintext) => ProtectedData.Protect(plaintext, Entropy, DataProtectionScope.CurrentUser);

    public byte[] Unprotect(byte[] protectedData) => ProtectedData.Unprotect(protectedData, Entropy, DataProtectionScope.CurrentUser);
}
