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
                throw new AgentCredentialStoreException(AgentText.Get(AgentMessage.DirectoryIsLink));
            }
            Directory.CreateDirectory(directory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            if ((File.GetUnixFileMode(directory) & (UnixFileMode.GroupRead | UnixFileMode.GroupWrite | UnixFileMode.GroupExecute
                | UnixFileMode.OtherRead | UnixFileMode.OtherWrite | UnixFileMode.OtherExecute)) != 0)
            {
                throw new AgentCredentialStoreException(AgentText.Get(AgentMessage.DirectoryNotOwnerOnly));
            }
#endif
            var path = Path.Combine(directory, ".enrollment.lock");
            if (new FileInfo(path).LinkTarget is not null || (File.Exists(path) && (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0))
            {
                throw new AgentCredentialStoreException(AgentText.Get(AgentMessage.LockIsLink));
            }
            var options = new FileStreamOptions { Mode = FileMode.OpenOrCreate, Access = FileAccess.ReadWrite, Share = FileShare.None };
#if AGENT_PLATFORM_LINUX
            if (File.Exists(path) && File.GetUnixFileMode(path) != (UnixFileMode.UserRead | UnixFileMode.UserWrite))
            {
                throw new AgentCredentialStoreException(AgentText.Get(AgentMessage.LockNotOwnerOnly));
            }
            options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
#endif
            return new FileStream(path, options);
        }
        catch (IOException)
        {
            throw new AgentEnrollmentException(AgentText.Get(AgentMessage.EnrollmentBusy));
        }
        catch (UnauthorizedAccessException)
        {
            throw new AgentCredentialStoreException(AgentText.Get(AgentMessage.StoreAccessDenied));
        }
    }
}
