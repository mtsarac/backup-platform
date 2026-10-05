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
