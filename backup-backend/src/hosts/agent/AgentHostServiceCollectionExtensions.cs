using BackupPlatform.Agent.Core.Connectivity;
using BackupPlatform.Agent.Core.Identity;
using BackupPlatform.Agent.Transport;
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

        services.TryAddSingleton(TimeProvider.System);
        services.AddSingleton(static provider => provider.GetRequiredService<IOptions<AgentHostOptions>>().Value.Connectivity);
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
        services.TryAddSingleton<ICapabilityRevisionSource, NullCapabilityRevisionSource>();

        services.AddAgentTransport();
        services.AddAgentBackupExecution(configuration);
        services.AddSingleton<AgentConnectivityService>();
        services.AddSingleton<IAgentConnectivityStatus>(static provider => provider.GetRequiredService<AgentConnectivityService>());
        services.AddHostedService<AgentConnectivityWorker>();
        return services;
    }
}
