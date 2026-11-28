using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;

namespace BackupPlatform.Agent.Platform.Windows;

/// <summary>
/// A secret kept as a machine-scope DPAPI blob: a copy of the file taken off this machine (backup, disk image) is useless, and
/// the secret never lives in the service registry key, which any local user can read. Access to the file itself is the directory ACL.
/// </summary>
[SupportedOSPlatform("windows")]
public static class WindowsSecretFile
{
    private static readonly byte[] Entropy = "backup-platform/agent/mtls-pfx-password/v1"u8.ToArray();

    public static void Write(string path, string secret)
    {
        var plain = Encoding.UTF8.GetBytes(secret);
        try
        {
            var temporary = path + "." + Guid.NewGuid().ToString("N");
            File.WriteAllBytes(temporary, ProtectedData.Protect(plain, Entropy, DataProtectionScope.LocalMachine));
            File.Move(temporary, path, overwrite: true);
        }
        finally { CryptographicOperations.ZeroMemory(plain); }
    }

    public static string Read(string path)
    {
        var plain = ProtectedData.Unprotect(File.ReadAllBytes(path), Entropy, DataProtectionScope.LocalMachine);
        try { return Encoding.UTF8.GetString(plain); }
        finally { CryptographicOperations.ZeroMemory(plain); }
    }
}
