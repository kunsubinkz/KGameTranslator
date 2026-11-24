using System.Runtime.InteropServices;

namespace GameTranslator.Platform.Windows;

/// <summary>Lưu API key trong Windows Credential Manager (target `KGameTranslator/claude`, `KGameTranslator/gemini`).</summary>
public static partial class CredentialStore
{
    private const uint CredTypeGeneric = 1;
    private const uint PersistLocalMachine = 2;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct CREDENTIAL
    {
        public uint Flags;
        public uint Type;
        public string TargetName;
        public string? Comment;
        public long LastWritten;
        public uint CredentialBlobSize;
        public nint CredentialBlob;
        public uint Persist;
        public uint AttributeCount;
        public nint Attributes;
        public string? TargetAlias;
        public string? UserName;
    }

    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool CredWriteW(ref CREDENTIAL credential, uint flags);

    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool CredReadW(string target, uint type, uint flags, out nint credential);

    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool CredDeleteW(string target, uint type, uint flags);

    [DllImport("advapi32.dll")]
    private static extern void CredFree(nint buffer);

    public static string Target(string account) => "KGameTranslator/" + account;

    public static string? Read(string account)
    {
        if (!CredReadW(Target(account), CredTypeGeneric, 0, out var ptr)) return null;
        try
        {
            var cred = Marshal.PtrToStructure<CREDENTIAL>(ptr);
            if (cred.CredentialBlob == 0 || cred.CredentialBlobSize == 0) return null;
            return Marshal.PtrToStringUni(cred.CredentialBlob, (int)cred.CredentialBlobSize / 2);
        }
        finally { CredFree(ptr); }
    }

    public static bool Write(string account, string secret)
    {
        if (string.IsNullOrEmpty(secret)) return Delete(account);
        var blob = Marshal.StringToCoTaskMemUni(secret);
        try
        {
            var cred = new CREDENTIAL
            {
                Type = CredTypeGeneric,
                TargetName = Target(account),
                UserName = account,
                CredentialBlobSize = (uint)(secret.Length * 2),
                CredentialBlob = blob,
                Persist = PersistLocalMachine,
            };
            return CredWriteW(ref cred, 0);
        }
        finally { Marshal.FreeCoTaskMem(blob); }
    }

    public static bool Delete(string account) => CredDeleteW(Target(account), CredTypeGeneric, 0);
}
