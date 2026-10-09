using BackupPlatform.Agent.Core.Identity;
#if AGENT_PLATFORM_WINDOWS
using BackupPlatform.Agent.Platform.Windows.Identity;
#endif

namespace BackupPlatform.Agent.Host;

internal static class AgentEnrollmentLease
{
    internal static FileStream Acquire(string directory)
    {
        #if AGENT_PLATFORM_LINUX
        if (OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException("This agent host build targets Linux.");
        }
        #endif
        try
        {
#if AGENT_PLATFORM_WINDOWS
            new WindowsAgentCredentialStore(directory).PrepareEnrollmentDirectory();
#else
            var info = new DirectoryInfo(directory);
            if (info.LinkTarget is not null)
            {
                throw new AgentCredentialStoreException("The enrollment directory must not be a symbolic link.");
            }
            Directory.CreateDirectory(directory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            if ((File.GetUnixFileMode(directory) & (UnixFileMode.GroupRead | UnixFileMode.GroupWrite | UnixFileMode.GroupExecute
                | UnixFileMode.OtherRead | UnixFileMode.OtherWrite | UnixFileMode.OtherExecute)) != 0)
            {
                throw new AgentCredentialStoreException("The enrollment directory must be owner-only (mode 0700).");
            }
#endif
            var path = Path.Combine(directory, ".enrollment.lock");
            if (new FileInfo(path).LinkTarget is not null || (File.Exists(path) && (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0))
            {
                throw new AgentCredentialStoreException("The enrollment lock must not be a link.");
            }
            var options = new FileStreamOptions { Mode = FileMode.OpenOrCreate, Access = FileAccess.ReadWrite, Share = FileShare.None };
#if AGENT_PLATFORM_LINUX
            if (File.Exists(path) && File.GetUnixFileMode(path) != (UnixFileMode.UserRead | UnixFileMode.UserWrite))
            {
                throw new AgentCredentialStoreException("The enrollment lock must be an owner-only regular file (mode 0600).");
            }
            options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
#endif
            return new FileStream(path, options);
        }
        catch (IOException)
        {
            throw new AgentEnrollmentException("Another enrollment is in progress, or the secure store cannot be locked. No token was submitted.");
        }
        catch (UnauthorizedAccessException)
        {
            throw new AgentCredentialStoreException("Access to the enrollment store was denied. Run as the agent's service account or choose an owner-only store directory.");
        }
    }
}
