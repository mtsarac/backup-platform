using BackupPlatform.Agent.Core.Connectivity;
using BackupPlatform.Agent.Core.Identity;
using BackupPlatform.Agent.Core.Restore;
using BackupPlatform.Agent.Core.Tasks;
using BackupPlatform.Agent.Transport;
using BackupPlatform.BackupProviders.Abstractions.Database;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;

namespace BackupPlatform.Agent.Host;

public static class AgentHostServiceCollectionExtensions
{
    /// <summary>Wires options (validated at startup), credential store, transport, connectivity service and its worker.</summary>
    public static IServiceCollection AddAgentHost(this IServiceCollection services, Microsoft.Extensions.Configuration.IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        services.AddOptions<AgentHostOptions>()
            .Bind(configuration.GetSection(AgentHostOptions.SectionName))
            .ValidateOnStart();
        services.AddSingleton<IValidateOptions<AgentHostOptions>, AgentHostOptionsValidator>();

        services.TryAddSingleton<TimeProvider>(static _ => new ServerAlignedTimeProvider(TimeProvider.System));
        services.AddSingleton(static provider =>
        {
            var connectivity = provider.GetRequiredService<IOptions<AgentHostOptions>>().Value.Connectivity;
            connectivity.PollWhilePushConnected = true;
            return connectivity;
        });
        services.AddSingleton(static provider =>
        {
            var connectivity = provider.GetRequiredService<IOptions<AgentHostOptions>>().Value.Connectivity;
            return new AgentBackoff(connectivity.ReconnectBaseDelay, connectivity.ReconnectMaxDelay);
        });
        services.AddSingleton(static provider =>
            new AgentBackendEndpoint(new Uri(provider.GetRequiredService<IOptions<AgentHostOptions>>().Value.Backend.BaseUri!)));
        services.AddSingleton<IAgentCredentialStore>(static provider =>
            AgentCredentialStoreFactory.Create(provider.GetRequiredService<IOptions<AgentHostOptions>>().Value.CredentialStoreDirectory));
        services.AddSingleton(static _ => AgentSoftwareInfo.Detect(typeof(AgentHostServiceCollectionExtensions).Assembly));
        services.AddSingleton(static _ => AgentCapabilityCatalog.CreateDefault());
        services.AddSingleton<ICapabilityRevisionSource, CatalogCapabilityRevisionSource>();

#if AGENT_PLATFORM_WINDOWS
        services.AddSingleton<BackupPlatform.StorageProviders.Abstractions.IWindowsNetworkSession, BackupPlatform.Agent.Platform.Windows.WindowsNetworkSession>();
#endif
        services.AddAgentTransport();
        services.AddAgentBackupExecution(configuration);
        services.TryAddSingleton<IBackupTaskCredentialResolver, BackupTaskCredentialResolver>();
        services.AddSingleton(provider => new BackupTaskRequestFactory(
            provider.GetRequiredService<IBackupTaskCredentialResolver>(),
            provider.GetServices<StagingArea>(),
            provider.GetService<ITaskStorageProviderFactory>(),
            PacedUploads(provider),
            provider.GetService<LocalVolumes>()));
        services.AddSingleton<BackupTaskWorker>();
        services.AddSingleton(provider => new RestoreTaskRequestFactory(
            provider.GetRequiredService<IBackupTaskCredentialResolver>(),
            provider.GetServices<StagingArea>(),
            provider.GetService<ITaskStorageProviderFactory>(),
            provider.GetService<LocalVolumes>(),
            provider.GetService<IArtifactKeyClient>()));
        services.AddSingleton<RestoreTaskWorker>();
        services.AddSingleton<RestoreCleanupWorker>();
        services.TryAddSingleton<IDeleteReplicaProviderResolver, DeleteReplicaProviderResolver>();
        services.AddSingleton<DeleteReplicaTaskWorker>();
        services.AddSingleton<ReplicaRepairTaskWorker>();
        services.AddSingleton<DownloadTaskWorker>();
        services.AddSingleton(provider => configuration.GetSection("Agent:Backup:Sftp")
            .Get<BackupPlatform.StorageProviders.Sftp.SftpNetworkOptions>() ?? new BackupPlatform.StorageProviders.Sftp.SftpNetworkOptions());
        services.AddSingleton<BackupPlatform.Contracts.Storage.ISftpCapacityProbeExecutor, Storage.SftpCapacityProbeExecutor>();
        services.AddSingleton<CapacityProbeTaskWorker>();
        services.AddHostedService<AgentCapacityTaskHostedService>();
        services.AddSingleton<IAgentTaskSink, AgentTaskDispatcher>();
        services.AddHostedService<AgentDeletionTaskHostedService>();
        services.AddHostedService<AgentRepairTaskHostedService>();
        services.AddHostedService<AgentBackupTaskHostedService>();
        services.AddHostedService<AgentRestoreTaskHostedService>();
        services.AddHostedService<AgentRestoreCleanupHostedService>();
        services.AddHostedService<AgentDownloadTaskHostedService>();
        services.AddAgentDiscovery();
        services.AddSingleton<AgentConnectivityService>();
        services.AddSingleton<IAgentConnectivityStatus>(static provider => provider.GetRequiredService<AgentConnectivityService>());
        services.AddHostedService<AgentConnectivityWorker>();
        services.AddAgentAutoUpdates(configuration);
        return services;
    }

    private static Func<BackupPlatform.Contracts.Backups.BackupTaskSnapshot, BackupPlatform.Agent.Core.Backup.IUploadBandwidth> PacedUploads(IServiceProvider provider)
    {
        var client = provider.GetRequiredService<IBackupTaskClient>();
        var identities = provider.GetRequiredService<IAgentCredentialStore>();
        var time = provider.GetRequiredService<TimeProvider>();
        return task => new UploadBandwidthGovernor(client, identities, task.AttemptId, task.LeaseToken, time);
    }
}
