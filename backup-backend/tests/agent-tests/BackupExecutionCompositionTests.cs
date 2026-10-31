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
    [InlineData("MinimumAge", "00:00:59", "between 1 minute and 365 days")]
    [InlineData("MinimumAge", "00:00:00", "between 1 minute and 365 days")]
    [InlineData("MinimumAge", "-01:00:00", "between 1 minute and 365 days")]
    [InlineData("MinimumAge", "400.00:00:00", "between 1 minute and 365 days")]
    [InlineData("ScanInterval", "00:00:09", "between 10 seconds and 7 days")]
    [InlineData("ScanInterval", "00:00:00", "between 10 seconds and 7 days")]
    [InlineData("ScanInterval", "-00:01:00", "between 10 seconds and 7 days")]
    [InlineData("ScanInterval", "8.00:00:00", "between 10 seconds and 7 days")]
    public void ACleanupIntervalOutsideItsLimitsIsRejectedByName(string setting, string value, string limits)
    {
        var message = Fail(
            ("Agent:Backup:Staging:Mapping", "SameHost"),
            ("Agent:Backup:Staging:AgentDirectory", Path.Combine(Root, "s")),
            ($"Agent:Backup:Staging:Cleanup:{setting}", value));

        Assert.Contains($"Agent:Backup:Staging:Cleanup:{setting} must be {limits}", message, StringComparison.Ordinal);
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
