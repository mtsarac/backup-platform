using BackupPlatform.Agent.Core.Connectivity;
using BackupPlatform.Agent.Core.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace BackupPlatform.Agent.Transport;

public static class AgentTransportServiceCollectionExtensions
{
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Registers the HTTP backend client (typed <see cref="HttpClient"/> whose connections are recycled every two minutes
    /// so DNS changes are picked up) and the SignalR push channel. Requires <see cref="AgentBackendEndpoint"/>,
    /// <see cref="AgentBackoff"/>, <see cref="TimeProvider"/> and logging to be registered.
    /// </summary>
    public static IServiceCollection AddAgentTransport(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.AddHttpClient<IAgentBackendClient, HttpAgentBackendClient>(client =>
            {
                client.Timeout = RequestTimeout;
                client.MaxResponseContentBufferSize = 1024 * 1024;
            })
            .ConfigurePrimaryHttpMessageHandler(static () => new SocketsHttpHandler
            {
                PooledConnectionLifetime = TimeSpan.FromMinutes(2),
                AllowAutoRedirect = false,
            })
            .SetHandlerLifetime(Timeout.InfiniteTimeSpan);
        services.AddHttpClient<IBackupTaskClient, HttpBackupTaskClient>(client =>
            {
                client.Timeout = RequestTimeout;
                client.MaxResponseContentBufferSize = 1024 * 1024;
            })
            .ConfigurePrimaryHttpMessageHandler(static () => new SocketsHttpHandler
            {
                PooledConnectionLifetime = TimeSpan.FromMinutes(2),
                AllowAutoRedirect = false,
            })
            .SetHandlerLifetime(Timeout.InfiniteTimeSpan);
        services.TryAddSingleton<IAgentPushChannel, SignalRAgentPushChannel>();
        return services;
    }
}
