using BackupPlatform.Agent.Core.Connectivity;
using BackupPlatform.Agent.Core.Backup;
using BackupPlatform.Agent.Core.Restore;
using BackupPlatform.BackupProviders.Abstractions.Database;
using BackupPlatform.BackupProviders.MSSQL;
using BackupPlatform.BackupProviders.PostgreSql;
using BackupPlatform.BackupProviders.PostgreSql.Tools;
using BackupPlatform.Agent.Core.Tasks;
using BackupPlatform.Agent.Core.Identity;
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
        var postgreSql = configuration.GetSection(AgentHostOptions.SectionName).GetSection(AgentBackupOptions.SectionName)
            .GetSection("PostgreSql").Get<AgentPostgreSqlOptions>();
        var clientTools = new PostgreSqlProviderOptions(postgreSql?.ClientToolsDirectory, postgreSql?.BackupMode ?? PostgreSqlBackupMode.Logical);
        services.AddSingleton<IDatabaseBackupProvider>(provider => new PostgreSqlBackupProvider(
            provider.GetRequiredService<ILogger<PostgreSqlBackupProvider>>(), clientTools, provider.GetRequiredService<TimeProvider>()));
        services.AddSingleton<IDatabaseRestoreProvider>(provider => new PostgreSqlRestoreProvider(
            provider.GetRequiredService<ILogger<PostgreSqlRestoreProvider>>(), clientTools, provider.GetRequiredService<TimeProvider>()));
        services.AddSingleton<IArtifactPreparer, EncryptingArtifactPreparer>();
        services.AddSingleton<IDatabaseBackupPipeline, DatabaseBackupPipeline>();
        services.AddSingleton<IDatabaseRestorePipeline, DatabaseRestorePipeline>();
        // Shared by the pipeline (which holds the staged names of running attempts) and the staging cleanup (which must skip them).
        services.TryAddSingleton<StagingUsageRegistry>();
        services.AddSingleton<BackupPlatform.SnapshotProviders.Abstractions.ISnapshotCommandRunner, BackupPlatform.SnapshotProviders.Abstractions.SnapshotCommandRunner>();
        foreach (var kind in Enum.GetValues<BackupPlatform.SnapshotProviders.Abstractions.SnapshotKind>())
        {
            services.AddSingleton<BackupPlatform.SnapshotProviders.Abstractions.ISnapshotProvider>(provider =>
                new BackupPlatform.SnapshotProviders.Abstractions.NativeSnapshotProvider(kind,
                    provider.GetRequiredService<BackupPlatform.SnapshotProviders.Abstractions.ISnapshotCommandRunner>()));
        }
        var fileSources = configuration.GetSection("Agent:Backup:FileSources").Get<Dictionary<string, BackupPlatform.BackupProviders.FileSystem.FileSystemRoot>>();
        if (fileSources is { Count: > 0 })
        {
#if AGENT_PLATFORM_WINDOWS
            services.AddSingleton<BackupPlatform.BackupProviders.Abstractions.IWindowsFilePermissions, BackupPlatform.Agent.Platform.Windows.WindowsFilePermissions>();
#endif
            services.AddSingleton(provider => new BackupPlatform.BackupProviders.FileSystem.FileSystemBackupProvider(
                provider.GetServices<BackupPlatform.SnapshotProviders.Abstractions.ISnapshotProvider>(),
                provider.GetService<BackupPlatform.BackupProviders.Abstractions.IWindowsFilePermissions>()));
            services.AddSingleton(provider => new BackupPlatform.BackupProviders.FileSystem.FileSystemBackupEngine(fileSources,
                provider.GetRequiredService<BackupPlatform.BackupProviders.FileSystem.FileSystemBackupProvider>(), provider.GetRequiredService<TimeProvider>()));
            services.AddSingleton<IDatabaseBackupProvider>(provider => provider.GetRequiredService<BackupPlatform.BackupProviders.FileSystem.FileSystemBackupEngine>());
            services.AddSingleton<IDatabaseRestoreProvider>(provider => provider.GetRequiredService<BackupPlatform.BackupProviders.FileSystem.FileSystemBackupEngine>());
        }

        var options = configuration.GetSection(AgentHostOptions.SectionName).GetSection(AgentBackupOptions.SectionName).Get<AgentBackupOptions>();
        var capacityRoots = (options?.LocalStorage?.Volumes ?? [])
            .Where(entry => LocalVolumes.IsValidName(entry.Key) && AgentBackupOptionsValidation.IsAbsolute(entry.Value))
            .ToDictionary(entry => entry.Key, entry => entry.Value, StringComparer.Ordinal);
        if (options?.LocalStorage?.RootDirectory is { } capacityRoot && AgentBackupOptionsValidation.IsAbsolute(capacityRoot))
            capacityRoots[LocalVolumes.DefaultName] = capacityRoot;
        services.AddSingleton(provider => new BackupPlatform.Agent.Core.Storage.CapacityProbeTaskHandler(
            provider.GetRequiredService<BackupPlatform.Contracts.Storage.ISftpCapacityProbeExecutor>(),
            provider.GetRequiredService<ILogger<BackupPlatform.Agent.Core.Storage.CapacityProbeTaskHandler>>(), capacityRoots));
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
                services.AddSingleton(provider =>
                {
                    var time = provider.GetRequiredService<TimeProvider>();
                    return new StagingCleaner(
                        new AgentStagingDirectory(staging.AgentDirectory),
                        provider.GetRequiredService<StagingUsageRegistry>(),
                        // Cleanup age is judged against file timestamps written by this machine's own clock.
                        time is ServerAlignedTimeProvider aligned ? aligned.Local : time,
                        provider.GetRequiredService<ILogger<StagingCleaner>>(),
                        async (attemptId, token) =>
                        {
                            var identity = await provider.GetRequiredService<IAgentCredentialStore>().LoadAsync(token).ConfigureAwait(false);
                            if (identity is null) return false;
                            var result = await provider.GetRequiredService<IBackupTaskClient>().CanReleaseRetainedTransferAsync(identity, attemptId, token).ConfigureAwait(false);
                            return result.IsSuccess && result.Value;
                        });
                });
                services.AddHostedService<AgentStagingCleanupHostedService>();
            }
        }

        IStorageProvider? defaultLocal = null;
        if (options.LocalStorage?.RootDirectory is { } localRoot && AgentBackupOptionsValidation.IsAbsolute(localRoot))
        {
            defaultLocal = new LocalStorageProvider(new LocalStorageOptions(localRoot));
            services.AddSingleton(defaultLocal);
        }
        var volumes = (options.LocalStorage?.Volumes ?? [])
            .Where(entry => LocalVolumes.IsValidName(entry.Key) && AgentBackupOptionsValidation.IsAbsolute(entry.Value))
            .ToDictionary(entry => entry.Key, entry => (IStorageProvider)new LocalStorageProvider(new LocalStorageOptions(entry.Value)), StringComparer.Ordinal);
        services.AddSingleton(new LocalVolumes(defaultLocal, volumes));

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
        if (options.PostgreSql?.ClientToolsDirectory is { } clientTools && !IsAbsolute(clientTools))
            yield return prefix + "PostgreSql:ClientToolsDirectory must be an absolute directory path.";
        if (options.PostgreSql is { } pg && !Enum.IsDefined(pg.BackupMode))
            yield return prefix + "PostgreSql:BackupMode must be Logical, Physical or Streaming.";
        foreach (var pair in options.FileSources ?? [])
        {
            if (!LocalVolumes.IsValidName(pair.Key) || !IsAbsolute(pair.Value.RootDirectory))
                yield return prefix + "FileSources requires simple source references and absolute root directories.";
            if (pair.Value.SnapshotKind is { } kind && !Enum.IsDefined(kind))
                yield return prefix + "FileSources snapshot kind must be Vss, Lvm, Btrfs or Zfs.";
        }

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

        if (options.LocalStorage is { RootDirectory: not null } local && !IsAbsolute(local.RootDirectory))
        {
            yield return prefix + "LocalStorage:RootDirectory must be an absolute path.";
        }

        foreach (var (name, directory) in options.LocalStorage?.Volumes ?? [])
        {
            if (!LocalVolumes.IsValidName(name))
            {
                yield return prefix + "LocalStorage:Volumes has a name that is not one lowercase key (letters, digits, '-') or is the reserved name 'local'.";
            }
            else if (!IsAbsolute(directory))
            {
                yield return prefix + $"LocalStorage:Volumes:{name} must be an absolute path.";
            }
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
