using BackupPlatform.Agent.Core.Discovery;
using BackupPlatform.Agent.Transport;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
#if AGENT_PLATFORM_LINUX
using BackupPlatform.Agent.Platform.Linux.Discovery;
#elif AGENT_PLATFORM_WINDOWS
using BackupPlatform.Agent.Platform.Windows.Discovery;
#endif

namespace BackupPlatform.Agent.Host;

/// <summary>
/// Composition of database auto discovery: the detectors of the platform this host was built for, the platform-independent ones, the
/// scanner and the runner the connectivity service starts. Requires the database providers and <c>IBackupTaskCredentialResolver</c>.
/// </summary>
internal static class AgentDiscoveryServiceCollectionExtensions
{
    public static IServiceCollection AddAgentDiscovery(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
#if AGENT_PLATFORM_LINUX
        services.AddSingleton<IInstanceDetector, LinuxSystemdDetector>();
#elif AGENT_PLATFORM_WINDOWS
        services.AddSingleton<IInstanceDetector, WindowsSqlServerDetector>();
        services.AddSingleton<IInstanceDetector, WindowsPostgreSqlDetector>();
#endif
        services.AddSingleton<IInstanceDetector, DockerContainerDetector>();
        services.TryAddSingleton<IListenerProbe, LoopbackListenerProbe>();
        services.AddSingleton<DiscoveryScanner>();
        services.AddSingleton<IDiscoveryScanRunner, DiscoveryScanRunner>();
        return services;
    }
}
