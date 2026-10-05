using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace BackupPlatform.Agent.Host;

/// <summary>
/// How the host plugs into the service manager of the platform it was built for. The Generic Host stays as it is: on Windows the
/// standard Windows Service lifetime is added, which only takes effect when the process was started by the Service Control
/// Manager (a console start, including <c>dotnet run</c> and Ctrl+C, behaves exactly as before). Stop requests become the host's
/// graceful shutdown. Nothing here lives in Agent.Core.
/// </summary>
internal static class AgentPlatformHosting
{
    /// <summary>The service name used by the Windows lifetime and as the Event Log source.</summary>
    internal const string WindowsServiceName = "BackupAgent";

    public static IServiceCollection AddAgentPlatformHosting(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
#if AGENT_PLATFORM_WINDOWS
        // The Windows lifetime also routes logging to the Application event log under the service name. That source must already exist
        // (the installer registers it): a virtual service account is not allowed to create one.
        services.AddWindowsService(options => options.ServiceName = WindowsServiceName);
#endif
        return services;
    }
}
