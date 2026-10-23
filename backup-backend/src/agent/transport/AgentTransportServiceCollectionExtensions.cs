using BackupPlatform.Agent.Core.Backup;
using BackupPlatform.Agent.Core.Connectivity;
using BackupPlatform.Agent.Core.Discovery;
using BackupPlatform.Agent.Core.Restore;
using BackupPlatform.Agent.Core.Tasks;
using BackupPlatform.Agent.Core.Updates;
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
        services.AddHttpClient<IAgentUpdateClient, HttpAgentUpdateClient>(client =>
            {
                client.Timeout = TimeSpan.FromMinutes(10);
                client.MaxResponseContentBufferSize = 65536;
            })
            .ConfigurePrimaryHttpMessageHandler(AgentMtlsTransport.CreateHandler)
            .SetHandlerLifetime(TimeSpan.FromMinutes(2));
        services.AddHttpClient<IAgentBackendClient, HttpAgentBackendClient>(client =>
            {
                client.Timeout = RequestTimeout;
                client.MaxResponseContentBufferSize = 1024 * 1024;
            })
            .ConfigurePrimaryHttpMessageHandler(AgentMtlsTransport.CreateHandler)
            .SetHandlerLifetime(TimeSpan.FromMinutes(2));
        services.AddHttpClient<IBackupTaskClient, HttpBackupTaskClient>(client =>
            {
                client.Timeout = RequestTimeout;
                client.MaxResponseContentBufferSize = 1024 * 1024;
            })
            .ConfigurePrimaryHttpMessageHandler(AgentMtlsTransport.CreateHandler)
            .SetHandlerLifetime(TimeSpan.FromMinutes(2));
        services.AddHttpClient<IAgentCapabilityClient, HttpAgentCapabilityClient>(client =>
            {
                client.Timeout = RequestTimeout;
                client.MaxResponseContentBufferSize = 1024 * 1024;
            })
            .ConfigurePrimaryHttpMessageHandler(AgentMtlsTransport.CreateHandler)
            .SetHandlerLifetime(TimeSpan.FromMinutes(2));
        services.AddHttpClient<IAgentDiscoveryClient, HttpAgentDiscoveryClient>(client =>
            {
                client.Timeout = RequestTimeout;
                client.MaxResponseContentBufferSize = 1024 * 1024;
            })
            .ConfigurePrimaryHttpMessageHandler(AgentMtlsTransport.CreateHandler)
            .SetHandlerLifetime(TimeSpan.FromMinutes(2));
        services.AddHttpClient<IDatabaseCredentialClient, HttpDatabaseCredentialClient>(client =>
            {
                client.Timeout = RequestTimeout;
                client.MaxResponseContentBufferSize = 1024 * 1024;
            })
            .ConfigurePrimaryHttpMessageHandler(AgentMtlsTransport.CreateHandler)
            .SetHandlerLifetime(TimeSpan.FromMinutes(2));
        services.AddHttpClient<IDeleteReplicaTaskClient, HttpDeleteReplicaTaskClient>(client =>
            {
                client.Timeout = RequestTimeout;
                client.MaxResponseContentBufferSize = 1024 * 1024;
            })
            .ConfigurePrimaryHttpMessageHandler(AgentMtlsTransport.CreateHandler)
            .SetHandlerLifetime(TimeSpan.FromMinutes(2));
        services.AddHttpClient<ICapacityProbeTaskClient, HttpCapacityProbeTaskClient>(client =>
            {
                client.Timeout = RequestTimeout;
                client.MaxResponseContentBufferSize = 1024 * 1024;
            })
            .ConfigurePrimaryHttpMessageHandler(AgentMtlsTransport.CreateHandler)
            .SetHandlerLifetime(TimeSpan.FromMinutes(2));
        services.AddHttpClient<IRestoreTaskClient, HttpRestoreTaskClient>(client =>
            {
                client.Timeout = RequestTimeout;
                client.MaxResponseContentBufferSize = 1024 * 1024;
            })
            .ConfigurePrimaryHttpMessageHandler(AgentMtlsTransport.CreateHandler)
            .SetHandlerLifetime(TimeSpan.FromMinutes(2));
        services.AddHttpClient<IDownloadTaskClient, HttpDownloadTaskClient>(client =>
            {
                // The renewable session lease, not a fixed HTTP timeout, bounds a large upload.
                client.Timeout = Timeout.InfiniteTimeSpan;
                client.MaxResponseContentBufferSize = 1024 * 1024;
            })
            .ConfigurePrimaryHttpMessageHandler(AgentMtlsTransport.CreateHandler)
            .SetHandlerLifetime(TimeSpan.FromMinutes(2));
        services.AddHttpClient<IArtifactKeyClient, HttpArtifactKeyClient>(client =>
            {
                client.Timeout = RequestTimeout;
                client.MaxResponseContentBufferSize = 1024 * 1024;
            })
            .ConfigurePrimaryHttpMessageHandler(AgentMtlsTransport.CreateHandler)
            .SetHandlerLifetime(TimeSpan.FromMinutes(2));
        services.AddHttpClient<ITaskStorageProviderFactory, HttpTaskStorageProviderFactory>(client =>
            {
                client.Timeout = Timeout.InfiniteTimeSpan;
                client.MaxResponseContentBufferSize = 1024 * 1024;
            })
            .ConfigurePrimaryHttpMessageHandler(AgentMtlsTransport.CreateHandler)
            .SetHandlerLifetime(TimeSpan.FromMinutes(2));
        services.AddHttpClient<IRetainedUploadAborter, HttpRetainedUploadAborter>(client =>
            {
                client.Timeout = Timeout.InfiniteTimeSpan;
                client.MaxResponseContentBufferSize = 1024 * 1024;
            })
            .ConfigurePrimaryHttpMessageHandler(AgentMtlsTransport.CreateHandler)
            .SetHandlerLifetime(TimeSpan.FromMinutes(2));
        services.TryAddSingleton<IAgentPushChannel, SignalRAgentPushChannel>();
        return services;
    }
}
