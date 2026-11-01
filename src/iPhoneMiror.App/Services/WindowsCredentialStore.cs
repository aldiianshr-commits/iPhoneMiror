using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;
using iPhoneMiror.Core.Abstractions;

namespace iPhoneMiror.App.Services;

/// <summary>Windows Credential Manager (generic credentials) via advapi32 CredWrite/CredRead/CredDelete.</summary>
public sealed class WindowsCredentialStore : ICredentialStore
{
    private const int CredTypeGeneric = 1;
    private const int CredPersistLocalMachine = 2;
    private const int ErrorNotFound = 1168;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct CREDENTIAL
    {
        public int Flags;
        public int Type;
        public string TargetName;
        public string? Comment;
        public System.Runtime.InteropServices.ComTypes.FILETIME LastWritten;
        public int CredentialBlobSize;
        public IntPtr CredentialBlob;
        public int Persist;
        public int AttributeCount;
        public IntPtr Attributes;
        public string? TargetAlias;
        public string UserName;
    }

    [DllImport("advapi32.dll", EntryPoint = "CredWriteW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool CredWrite(ref CREDENTIAL credential, int flags);

    [DllImport("advapi32.dll", EntryPoint = "CredReadW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool CredRead(string target, int type, int flags, out IntPtr credential);

    [DllImport("advapi32.dll", EntryPoint = "CredDeleteW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool CredDelete(string target, int type, int flags);

    [DllImport("advapi32.dll")]
    private static extern void CredFree(IntPtr buffer);

    public void Write(string target, string userName, string secret)
    {
        var blob = Encoding.Unicode.GetBytes(secret);
        if (blob.Length > 5 * 512) throw new ArgumentException("Secret is too large for Windows Credential Manager.", nameof(secret));
        var ptr = Marshal.AllocHGlobal(blob.Length);
        try
        {
            Marshal.Copy(blob, 0, ptr, blob.Length);
            var cred = new CREDENTIAL
            {
                Type = CredTypeGeneric,
                TargetName = target,
                UserName = userName,
                CredentialBlob = ptr,
                CredentialBlobSize = blob.Length,
                Persist = CredPersistLocalMachine,
                Comment = "iPhoneMiror",
            };
            if (!CredWrite(ref cred, 0)) throw new Win32Exception(Marshal.GetLastWin32Error());
        }
        finally
        {
            Marshal.FreeHGlobal(ptr);
        }
    }

    public (string UserName, string Secret)? Read(string target)
    {
        if (!CredRead(target, CredTypeGeneric, 0, out var ptr))
        {
            var err = Marshal.GetLastWin32Error();
            if (err == ErrorNotFound) return null;
            throw new Win32Exception(err);
        }
        try
        {
            var cred = Marshal.PtrToStructure<CREDENTIAL>(ptr);
            var secret = cred.CredentialBlobSize > 0 ? Marshal.PtrToStringUni(cred.CredentialBlob, cred.CredentialBlobSize / 2) : "";
            return (cred.UserName ?? "", secret);
        }
        finally
        {
            CredFree(ptr);
        }
    }

    public bool Delete(string target) => CredDelete(target, CredTypeGeneric, 0);
}
