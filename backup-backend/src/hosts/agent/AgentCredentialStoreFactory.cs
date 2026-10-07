using BackupPlatform.Agent.Core.Identity;
#if AGENT_PLATFORM_LINUX
using BackupPlatform.Agent.Platform.Linux.Identity;
#elif AGENT_PLATFORM_WINDOWS
using BackupPlatform.Agent.Platform.Windows.Identity;
#endif

namespace BackupPlatform.Agent.Host;

/// <summary>Selects the credential store of the platform this host was built for.</summary>
internal static class AgentCredentialStoreFactory
{
    public static string ResolveDirectory(string? directory)
    {
        if (directory is not null && !Path.IsPathFullyQualified(directory))
        {
            throw new AgentEnrollmentException("The credential store directory must be an absolute path.");
        }
#if AGENT_PLATFORM_WINDOWS
        return Path.GetFullPath(directory ?? WindowsAgentCredentialStore.DefaultDirectory);
#else
        if (OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException("This agent host build targets Linux.");
        }
        return Path.GetFullPath(directory ?? LinuxAgentCredentialStore.DefaultDirectory);
#endif
    }

    public static IAgentCredentialStore Create(string? directory)
    {
#if AGENT_PLATFORM_LINUX
        if (OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException("This agent host build targets Linux.");
        }
        return new LinuxAgentCredentialStore(directory ?? LinuxAgentCredentialStore.DefaultDirectory);
#elif AGENT_PLATFORM_WINDOWS
        return new WindowsAgentCredentialStore(directory ?? WindowsAgentCredentialStore.DefaultDirectory);
#else
#error The agent host is built for Windows or Linux only.
#endif
    }
}
