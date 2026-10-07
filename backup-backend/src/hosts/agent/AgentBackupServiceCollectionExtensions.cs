using BackupPlatform.Agent.Core.Backup;
using BackupPlatform.Agent.Core.Restore;
using BackupPlatform.BackupProviders.Abstractions.Database;
using BackupPlatform.BackupProviders.MSSQL;
using BackupPlatform.StorageProviders.Abstractions;
using BackupPlatform.StorageProviders.Central;
using BackupPlatform.StorageProviders.Local;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;

namespace BackupPlatform.Agent.Host;

/// <summary>
/// The composition root of backup execution: the one place that knows which engine provider, which storage providers and which
/// artifact preparer exist. Everything else (the pipeline, an orchestrator) sees only the abstractions and resolves providers by
/// identifier. Storage providers are registered only for the parts of <c>Agent:Backup</c> that are configured.
/// </summary>
public static class AgentBackupServiceCollectionExtensions
{
    public static IServiceCollection AddAgentBackupExecution(this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        services.TryAddSingleton(TimeProvider.System);
        services.AddSingleton<IDatabaseBackupProvider, SqlServerBackupProvider>();
        services.AddSingleton<IDatabaseRestoreProvider, SqlServerRestoreProvider>();
        services.AddSingleton<IArtifactPreparer, PassThroughArtifactPreparer>();
        services.AddSingleton<IDatabaseBackupPipeline, DatabaseBackupPipeline>();
        services.AddSingleton<IDatabaseRestorePipeline, DatabaseRestorePipeline>();
        // Shared by the pipeline (which holds the staged names of running attempts) and the staging cleanup (which must skip them).
        services.TryAddSingleton<StagingUsageRegistry>();

        var options = configuration.GetSection(AgentHostOptions.SectionName).GetSection(AgentBackupOptions.SectionName).Get<AgentBackupOptions>();
        if (options is null)
        {
            return services;
        }

        // Values were validated at start-up by AgentHostOptionsValidator; a value that is invalid here is simply not registered so the
        // validator reports it instead of the container failing with a less helpful error.
        if (AgentBackupOptionsValidation.TryCreateStaging(options.Staging, out var staging))
        {
            services.AddSingleton(staging);
            if (AgentBackupOptionsValidation.TryCreateCleanup(options.Staging?.Cleanup, out var cleanup))
            {
                services.AddSingleton(cleanup);
                services.AddSingleton(provider => new StagingCleaner(
                    new AgentStagingDirectory(staging.AgentDirectory),
                    provider.GetRequiredService<StagingUsageRegistry>(),
                    provider.GetRequiredService<TimeProvider>(),
                    provider.GetRequiredService<ILogger<StagingCleaner>>()));
                services.AddHostedService<AgentStagingCleanupHostedService>();
            }
        }

        if (options.LocalStorage?.RootDirectory is { } localRoot && AgentBackupOptionsValidation.IsAbsolute(localRoot))
        {
            services.AddSingleton<IStorageProvider>(_ => new LocalStorageProvider(new LocalStorageOptions(localRoot)));
        }

        if (options.CentralStorage is { RootDirectory: { } centralRoot, TenantScope: { } scope }
            && AgentBackupOptionsValidation.TryCreateCentral(centralRoot, scope, out var central))
        {
            services.AddSingleton<IStorageProvider>(_ => new CentralStorageProvider(central));
        }

        return services;
    }
}

/// <summary>Turns <see cref="AgentBackupOptions"/> into domain-level values and explains what is wrong when that is not possible.</summary>
internal static class AgentBackupOptionsValidation
{
    private static readonly TimeSpan MinimumCleanupAge = TimeSpan.FromMinutes(1);
    private static readonly TimeSpan MaximumCleanupAge = TimeSpan.FromDays(365);
    private static readonly TimeSpan MinimumScanInterval = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan MaximumScanInterval = TimeSpan.FromDays(7);

    public static bool IsAbsolute(string path) => !string.IsNullOrWhiteSpace(path) && !path.Any(char.IsControl) && Path.IsPathFullyQualified(path);

    public static bool TryCreateStaging(AgentBackupStagingOptions? options, [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out StagingArea? staging)
    {
        staging = null;
        if (options is not { Mapping: { } mapping, AgentDirectory: { } agentDirectory } || !IsAbsolute(agentDirectory))
        {
            return false;
        }

        switch (mapping)
        {
            case StagingMappingKind.SameHost when options.ServerDirectory is null || options.ServerDirectory == agentDirectory:
                staging = StagingArea.SameHost(agentDirectory);
                return true;
            case StagingMappingKind.Mapped
                when !string.IsNullOrWhiteSpace(options.ServerDirectory)
                    && !options.ServerDirectory.Any(char.IsControl)
                    && options.ServerDirectory != agentDirectory:
                staging = StagingArea.Mapped(new DatabaseServerPath(options.ServerDirectory), new AgentPath(agentDirectory));
                return true;
            default:
                return false;
        }
    }

    public static bool TryCreateCentral(string root, string scope, [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out CentralStorageOptions? options)
    {
        try
        {
            options = new CentralStorageOptions(root, scope);
            return true;
        }
        catch (ArgumentException)
        {
            options = null;
            return false;
        }
    }

    /// <summary>Applies the defaults to the parts of the cleanup section that are absent; false when a present value is outside its range.</summary>
    public static bool TryCreateCleanup(AgentStagingCleanupOptions? options, [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out StagingCleanupOptions? cleanup)
    {
        var minimumAge = options?.MinimumAge ?? StagingCleanupOptions.DefaultMinimumAge;
        var scanInterval = options?.ScanInterval ?? StagingCleanupOptions.DefaultScanInterval;
        if (minimumAge < MinimumCleanupAge || minimumAge > MaximumCleanupAge
            || scanInterval < MinimumScanInterval || scanInterval > MaximumScanInterval)
        {
            cleanup = null;
            return false;
        }

        cleanup = new StagingCleanupOptions { Enabled = options?.Enabled ?? true, MinimumAge = minimumAge, ScanInterval = scanInterval };
        return true;
    }

    /// <summary>One message per problem, each naming the setting. Empty when the section is valid or absent.</summary>
    public static IEnumerable<string> Validate(AgentBackupOptions? options)
    {
        if (options is null)
        {
            yield break;
        }

        var prefix = $"{AgentHostOptions.SectionName}:{AgentBackupOptions.SectionName}:";

        if (options.Staging is { } staging)
        {
            if (staging.Mapping is null)
            {
                yield return prefix + "Staging:Mapping is required (SameHost or Mapped): the relation between the database server's and the agent's staging directory is never guessed.";
            }
            else if (!Enum.IsDefined(staging.Mapping.Value))
            {
                yield return prefix + "Staging:Mapping must be SameHost or Mapped.";
            }
            else if (staging.AgentDirectory is null || !IsAbsolute(staging.AgentDirectory))
            {
                yield return prefix + "Staging:AgentDirectory must be an absolute path as the agent process sees it.";
            }
            else if (staging.Mapping == StagingMappingKind.Mapped)
            {
                if (string.IsNullOrWhiteSpace(staging.ServerDirectory) || staging.ServerDirectory.Any(char.IsControl))
                {
                    yield return prefix + "Staging:ServerDirectory is required for a Mapped staging area: the directory as the database server sees it.";
                }
                else if (staging.ServerDirectory == staging.AgentDirectory)
                {
                    yield return prefix + "Staging:ServerDirectory is identical to AgentDirectory; declare the mapping as SameHost.";
                }
            }
            else if (staging.Mapping == StagingMappingKind.SameHost && staging.ServerDirectory is { } server && server != staging.AgentDirectory)
            {
                yield return prefix + "Staging:ServerDirectory differs from AgentDirectory; declare the mapping as Mapped or omit ServerDirectory.";
            }

            if (staging.Cleanup is { } cleanup)
            {
                if (cleanup.MinimumAge is { } minimumAge && (minimumAge < MinimumCleanupAge || minimumAge > MaximumCleanupAge))
                {
                    yield return prefix + "Staging:Cleanup:MinimumAge must be between 1 minute and 365 days; it has to outlast the longest backup, because a younger file may still be in use.";
                }

                if (cleanup.ScanInterval is { } scanInterval && (scanInterval < MinimumScanInterval || scanInterval > MaximumScanInterval))
                {
                    yield return prefix + "Staging:Cleanup:ScanInterval must be between 10 seconds and 7 days.";
                }
            }
        }

        if (options.LocalStorage is { } local && (local.RootDirectory is null || !IsAbsolute(local.RootDirectory)))
        {
            yield return prefix + "LocalStorage:RootDirectory must be an absolute path.";
        }

        if (options.CentralStorage is { } central)
        {
            if (central.RootDirectory is null || !IsAbsolute(central.RootDirectory))
            {
                yield return prefix + "CentralStorage:RootDirectory must be an absolute path.";
            }
            else if (central.TenantScope is null || !TryCreateCentral(central.RootDirectory, central.TenantScope, out _))
            {
                yield return prefix + "CentralStorage:TenantScope must be one lowercase storage key segment (ASCII letters, digits, '.', '_', '-').";
            }
        }
    }
}
