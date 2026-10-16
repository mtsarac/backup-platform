using BackupPlatform.Agent.Core.Backup;
using BackupPlatform.Agent.Host;
using BackupPlatform.BackupProviders.Abstractions.Database;
using BackupPlatform.StorageProviders.Abstractions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

namespace BackupPlatform.AgentTests;

public sealed class BackupExecutionCompositionTests
{
    private static readonly string Root = Path.Combine(Path.GetTempPath(), "agent-backup-composition-test");

    private static IConfiguration Configuration(params (string Key, string? Value)[] values) =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(
                new[] { new KeyValuePair<string, string?>("Agent:Backend:BaseUri", "https://backup.example.test/") }
                    .Concat(values.Select(v => new KeyValuePair<string, string?>(v.Key, v.Value))))
            .Build();

    private static ServiceProvider Build(params (string Key, string? Value)[] values)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddAgentHost(Configuration(values));
        return services.BuildServiceProvider();
    }

    private static string Fail(params (string Key, string? Value)[] values)
    {
        var options = new AgentHostOptions();
        Configuration(values).GetSection("Agent").Bind(options);
        var result = new AgentHostOptionsValidator().Validate(null, options);
        Assert.True(result.Failed);
        return result.FailureMessage;
    }

    [Fact]
    public void WithoutABackupSectionThePipelineExistsButNoStorageOrStagingIsRegistered()
    {
        using var provider = Build();

        Assert.IsType<DatabaseBackupPipeline>(provider.GetRequiredService<IDatabaseBackupPipeline>());
        Assert.Contains(provider.GetServices<IDatabaseBackupProvider>(), p => p.Engine == DatabaseEngineId.MsSql);
        Assert.Contains(provider.GetServices<IDatabaseBackupProvider>(), p => p.Engine.Value == "postgres");
        Assert.Equal(["mssql", "postgres"], provider.GetServices<IDatabaseRestoreProvider>().Select(p => p.Engine.Value).Order(StringComparer.Ordinal));
        Assert.Empty(provider.GetServices<IStorageProvider>());
        Assert.Null(provider.GetService<StagingArea>());
    }

    [Fact]
    public void ConfiguredStoragesAndAMappedStagingAreaAreRegisteredByIdentifier()
    {
        using var provider = Build(
            ("Agent:Backup:Staging:Mapping", "Mapped"),
            ("Agent:Backup:Staging:ServerDirectory", "/var/opt/mssql/staging"),
            ("Agent:Backup:Staging:AgentDirectory", Path.Combine(Root, "staging")),
            ("Agent:Backup:LocalStorage:RootDirectory", Path.Combine(Root, "local")),
            ("Agent:Backup:CentralStorage:RootDirectory", Path.Combine(Root, "central")),
            ("Agent:Backup:CentralStorage:TenantScope", "tenant-a"));

        var storages = provider.GetServices<IStorageProvider>().ToDictionary(s => s.Id);
        Assert.Equal([StorageProviderId.Central, StorageProviderId.Local], storages.Keys.OrderBy(id => id.Value));
        Assert.All(storages.Values, storage => Assert.Equal(StorageTopology.AgentDirect, storage.Topology));

        var staging = provider.GetRequiredService<StagingArea>();
        Assert.Equal(StagingMappingKind.Mapped, staging.Kind);
        Assert.Equal("/var/opt/mssql/staging", staging.ServerDirectory.Value);
        Assert.Equal(Path.Combine(Root, "staging"), staging.AgentDirectory.Value);
        Assert.NotNull(provider.GetRequiredService<IDatabaseBackupPipeline>());
    }

    [Fact]
    public void ASameHostStagingAreaNeedsOnlyTheAgentDirectory()
    {
        using var provider = Build(
            ("Agent:Backup:Staging:Mapping", "SameHost"),
            ("Agent:Backup:Staging:AgentDirectory", Path.Combine(Root, "staging")));

        var staging = provider.GetRequiredService<StagingArea>();
        Assert.Equal(StagingMappingKind.SameHost, staging.Kind);
        Assert.Equal(staging.AgentDirectory.Value, staging.ServerDirectory.Value);
    }

    [Fact]
    public void AValidBackupSectionPassesStartupValidation()
    {
        var options = new AgentHostOptions
        {
            Backend = { BaseUri = "https://backup.example.test/" },
            Backup = new AgentBackupOptions
            {
                Staging = new AgentBackupStagingOptions { Mapping = StagingMappingKind.Mapped, ServerDirectory = @"D:\SqlStaging", AgentDirectory = Path.Combine(Root, "s") },
                LocalStorage = new AgentLocalStorageOptions { RootDirectory = Path.Combine(Root, "l") },
                CentralStorage = new AgentCentralStorageOptions { RootDirectory = Path.Combine(Root, "c"), TenantScope = "0b1f6c2e-8d0a-4f7e-9a53-2f4c1d9d7a10" }
            }
        };

        Assert.True(new AgentHostOptionsValidator().Validate(null, options).Succeeded);
    }

    [Theory]
    [InlineData("")]
    [InlineData("relative/pg-tools")]
    public void PostgreSqlClientToolsDirectoryMustBeAbsoluteWhenSet(string directory) =>
        Assert.Contains("PostgreSql:ClientToolsDirectory", Fail(("Agent:Backup:PostgreSql:ClientToolsDirectory", directory)), StringComparison.Ordinal);

    [Fact]
    public void PostgreSqlClientToolsDirectoryBindsWithoutChangingOtherHostConfiguration()
    {
        using var provider = Build(("Agent:Backup:PostgreSql:ClientToolsDirectory", Root));
        var options = provider.GetRequiredService<IOptions<AgentHostOptions>>().Value;
        Assert.Equal(Root, options.Backup!.PostgreSql!.ClientToolsDirectory);
        Assert.Equal(2, provider.GetServices<IDatabaseBackupProvider>().Count());
        Assert.Equal(2, provider.GetServices<IDatabaseRestoreProvider>().Count());
    }

    [Fact]
    public void TheRelationBetweenServerAndAgentStagingDirectoriesIsNeverGuessed()
    {
        Assert.Contains("Agent:Backup:Staging:Mapping is required", Fail(("Agent:Backup:Staging:AgentDirectory", Path.Combine(Root, "s"))), StringComparison.Ordinal);
        Assert.Contains("Staging:ServerDirectory is required for a Mapped", Fail(
            ("Agent:Backup:Staging:Mapping", "Mapped"), ("Agent:Backup:Staging:AgentDirectory", Path.Combine(Root, "s"))), StringComparison.Ordinal);
        Assert.Contains("declare the mapping as SameHost", Fail(
            ("Agent:Backup:Staging:Mapping", "Mapped"),
            ("Agent:Backup:Staging:AgentDirectory", Path.Combine(Root, "s")),
            ("Agent:Backup:Staging:ServerDirectory", Path.Combine(Root, "s"))), StringComparison.Ordinal);
        Assert.Contains("declare the mapping as Mapped", Fail(
            ("Agent:Backup:Staging:Mapping", "SameHost"),
            ("Agent:Backup:Staging:AgentDirectory", Path.Combine(Root, "s")),
            ("Agent:Backup:Staging:ServerDirectory", "/elsewhere")), StringComparison.Ordinal);
    }

    [Fact]
    public void RelativeDirectoriesAndInvalidTenantScopesAreRejectedByName()
    {
        Assert.Contains("Staging:AgentDirectory must be an absolute path", Fail(
            ("Agent:Backup:Staging:Mapping", "SameHost"), ("Agent:Backup:Staging:AgentDirectory", "relative/staging")), StringComparison.Ordinal);
        Assert.Contains("LocalStorage:RootDirectory must be an absolute path", Fail(("Agent:Backup:LocalStorage:RootDirectory", "relative")), StringComparison.Ordinal);
        Assert.Contains("CentralStorage:RootDirectory must be an absolute path", Fail(
            ("Agent:Backup:CentralStorage:TenantScope", "tenant-a")), StringComparison.Ordinal);
        foreach (var scope in new[] { "Tenant-A", "a/b", "..", "tenant one" })
        {
            Assert.Contains("CentralStorage:TenantScope must be one lowercase", Fail(
                ("Agent:Backup:CentralStorage:RootDirectory", Path.Combine(Root, "c")), ("Agent:Backup:CentralStorage:TenantScope", scope)), StringComparison.Ordinal);
        }
    }

    private static ServiceCollection Services(params (string Key, string? Value)[] values)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddAgentHost(Configuration(values));
        return services;
    }

    private static bool HasCleanupHostedService(IServiceCollection services) =>
        services.Any(descriptor => descriptor.ServiceType == typeof(IHostedService) && descriptor.ImplementationType == typeof(AgentStagingCleanupHostedService));

    [Fact]
    public void TheStagingCleanupExistsOnlyWhenAStagingAreaIsConfigured()
    {
        var unconfigured = Services();
        using var unconfiguredProvider = unconfigured.BuildServiceProvider();
        Assert.False(HasCleanupHostedService(unconfigured));
        Assert.Null(unconfiguredProvider.GetService<StagingCleaner>());
        Assert.Null(unconfiguredProvider.GetService<StagingCleanupOptions>());

        var storageOnly = Services(("Agent:Backup:LocalStorage:RootDirectory", Path.Combine(Root, "local")));
        using var storageOnlyProvider = storageOnly.BuildServiceProvider();
        Assert.False(HasCleanupHostedService(storageOnly));
        Assert.Null(storageOnlyProvider.GetService<StagingCleaner>());

        var configured = Services(
            ("Agent:Backup:Staging:Mapping", "SameHost"),
            ("Agent:Backup:Staging:AgentDirectory", Path.Combine(Root, "staging")));
        using var configuredProvider = configured.BuildServiceProvider();
        Assert.True(HasCleanupHostedService(configured));
        Assert.NotNull(configuredProvider.GetRequiredService<StagingCleaner>());
        Assert.Same(configuredProvider.GetRequiredService<StagingCleaner>(), configuredProvider.GetRequiredService<StagingCleaner>());
    }

    [Fact]
    public void AnInvalidStagingAreaRegistersNoCleanupAndLeavesTheComplaintToTheValidator()
    {
        var services = Services(("Agent:Backup:Staging:Mapping", "SameHost"), ("Agent:Backup:Staging:AgentDirectory", "relative/staging"));

        Assert.False(HasCleanupHostedService(services));
    }

    [Fact]
    public void TheRegistryTheCleanupConsultsIsTheOneSingletonThePipelineHoldsInto()
    {
        using var provider = Build(
            ("Agent:Backup:Staging:Mapping", "SameHost"),
            ("Agent:Backup:Staging:AgentDirectory", Path.Combine(Root, "staging")));

        Assert.Same(provider.GetRequiredService<StagingUsageRegistry>(), provider.GetRequiredService<StagingUsageRegistry>());
    }

    [Fact]
    public void TheCleanupDefaultsAreEnabledTwelveHoursAndHourly()
    {
        using var provider = Build(
            ("Agent:Backup:Staging:Mapping", "SameHost"),
            ("Agent:Backup:Staging:AgentDirectory", Path.Combine(Root, "staging")));

        var options = provider.GetRequiredService<StagingCleanupOptions>();
        Assert.True(options.Enabled);
        Assert.Equal(TimeSpan.FromHours(12), options.MinimumAge);
        Assert.Equal(TimeSpan.FromHours(1), options.ScanInterval);
    }

    [Fact]
    public void ConfiguredCleanupValuesReplaceTheDefaultsOneByOne()
    {
        using var provider = Build(
            ("Agent:Backup:Staging:Mapping", "SameHost"),
            ("Agent:Backup:Staging:AgentDirectory", Path.Combine(Root, "staging")),
            ("Agent:Backup:Staging:Cleanup:Enabled", "false"),
            ("Agent:Backup:Staging:Cleanup:MinimumAge", "1.00:00:00"));

        var options = provider.GetRequiredService<StagingCleanupOptions>();
        Assert.False(options.Enabled);
        Assert.Equal(TimeSpan.FromDays(1), options.MinimumAge);
        Assert.Equal(TimeSpan.FromHours(1), options.ScanInterval);
    }

    [Fact]
    public void ACleanupSectionWithinTheLimitsPassesStartupValidation()
    {
        var options = new AgentHostOptions
        {
            Backend = { BaseUri = "https://backup.example.test/" },
            Backup = new AgentBackupOptions
            {
                Staging = new AgentBackupStagingOptions
                {
                    Mapping = StagingMappingKind.SameHost,
                    AgentDirectory = Path.Combine(Root, "s"),
                    Cleanup = new AgentStagingCleanupOptions { Enabled = false, MinimumAge = TimeSpan.FromMinutes(1), ScanInterval = TimeSpan.FromSeconds(10) }
                }
            }
        };

        Assert.True(new AgentHostOptionsValidator().Validate(null, options).Succeeded);
    }

    [Theory]
    [InlineData("00:00:59")]
    [InlineData("00:00:00")]
    [InlineData("-01:00:00")]
    [InlineData("400.00:00:00")]
    public void ACleanupMinimumAgeOutsideItsLimitsIsRejectedByName(string value)
    {
        var message = Fail(
            ("Agent:Backup:Staging:Mapping", "SameHost"),
            ("Agent:Backup:Staging:AgentDirectory", Path.Combine(Root, "s")),
            ("Agent:Backup:Staging:Cleanup:MinimumAge", value));

        Assert.Contains("Agent:Backup:Staging:Cleanup:MinimumAge must be between 1 minute and 365 days", message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("00:00:09")]
    [InlineData("00:00:00")]
    [InlineData("-00:01:00")]
    [InlineData("8.00:00:00")]
    public void ACleanupScanIntervalOutsideItsLimitsIsRejectedByName(string value)
    {
        var message = Fail(
            ("Agent:Backup:Staging:Mapping", "SameHost"),
            ("Agent:Backup:Staging:AgentDirectory", Path.Combine(Root, "s")),
            ("Agent:Backup:Staging:Cleanup:ScanInterval", value));

        Assert.Contains("Agent:Backup:Staging:Cleanup:ScanInterval must be between 10 seconds and 7 days", message, StringComparison.Ordinal);
    }

    [Fact]
    public void AnInvalidCleanupValueRegistersNoCleanupWhileTheValidatorStillReportsIt()
    {
        var values = new (string Key, string? Value)[]
        {
            ("Agent:Backup:Staging:Mapping", "SameHost"),
            ("Agent:Backup:Staging:AgentDirectory", Path.Combine(Root, "s")),
            ("Agent:Backup:Staging:Cleanup:MinimumAge", "00:00:05")
        };

        Assert.False(HasCleanupHostedService(Services(values)));
        Assert.Contains("Staging:Cleanup:MinimumAge", Fail(values), StringComparison.Ordinal);
    }

    [Fact]
    public async Task AnInvalidBackupSectionFailsHostStartupNamingTheSetting()
    {
        var builder = Host.CreateEmptyApplicationBuilder(new HostApplicationBuilderSettings());
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Agent:Backend:BaseUri"] = "https://backup.example.test/",
            ["Agent:Backup:LocalStorage:RootDirectory"] = "relative"
        });
        builder.Services.AddLogging();
        builder.Services.AddAgentHost(builder.Configuration);
        using var host = builder.Build();

        var error = await Assert.ThrowsAsync<OptionsValidationException>(() => host.StartAsync());

        Assert.Contains("Agent:Backup:LocalStorage:RootDirectory must be an absolute path", error.Message, StringComparison.Ordinal);
    }
}
