using System.Security.Cryptography;
using BackupPlatform.Agent.Transport;
#if AGENT_PLATFORM_WINDOWS
using BackupPlatform.Agent.Platform.Windows;
#endif

namespace BackupPlatform.Agent.Host;

/// <summary>Where the client-certificate PFX password lives when it is not in the process environment: DPAPI blob on Windows, an owner-only file elsewhere.</summary>
internal static class AgentMtlsPasswordFile
{
    internal static void Install()
    {
#if AGENT_PLATFORM_WINDOWS
        AgentMtlsTransport.ReadPasswordFile = WindowsSecretFile.Read;
#endif
    }

    /// <summary>The password for a new PFX: the environment value if set, else the password file's content, creating the file with a random password when it does not exist yet.</summary>
    internal static string ResolveOrCreate()
    {
        var file = Environment.GetEnvironmentVariable("AGENT_MTLS_CERTIFICATE_PASSWORD_FILE");
        if (!string.IsNullOrEmpty(file) && !File.Exists(file))
        {
            if (!Path.IsPathFullyQualified(file)) throw new InvalidOperationException("The agent mTLS password file path must be absolute.");
            var created = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
            Write(file, created);
            return created;
        }
        return AgentMtlsTransport.ResolvePassword()
            ?? throw new InvalidOperationException("Set AGENT_MTLS_CERTIFICATE_PASSWORD_FILE (preferred) or AGENT_MTLS_CERTIFICATE_PASSWORD.");
    }

    private static void Write(string path, string secret)
    {
#if AGENT_PLATFORM_WINDOWS
        WindowsSecretFile.Write(path, secret);
#else
        var temporary = path + "." + Guid.NewGuid().ToString("N");
        var options = new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write };
        if (!OperatingSystem.IsWindows()) options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
        using (var stream = new FileStream(temporary, options))
            stream.Write(System.Text.Encoding.UTF8.GetBytes(secret));
        File.Move(temporary, path, overwrite: true);
#endif
    }
}
