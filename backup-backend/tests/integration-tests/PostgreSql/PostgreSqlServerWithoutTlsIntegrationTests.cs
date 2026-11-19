extern alias AgentCore;

using AgentCore::BackupPlatform.Agent.Core.Restore;
using BackupPlatform.Contracts.Backups;
using BackupPlatform.StorageProviders.Abstractions;
using BackupPlatform.StorageProviders.Local;
using Microsoft.Extensions.Logging.Abstractions;
using BackupPlatform.BackupProviders.Abstractions.Database;
using BackupPlatform.BackupProviders.PostgreSql;

namespace BackupPlatform.IntegrationTests.PostgreSql;

/// <summary>The stock container offers no TLS: with encryption on, every way of connecting must say "the server offers no TLS", not "unreachable" or "certificate".</summary>
[Trait("Category", "PostgreSqlProvider")]
public sealed class PostgreSqlServerWithoutTlsIntegrationTests(PostgreSqlProviderFixture fixture) : IClassFixture<PostgreSqlProviderFixture>
{
    public static TheoryData<bool> TrustSettings => new() { false, true };

    private DatabaseConnectionSettings Encrypted(bool trust) =>
        new(fixture.Settings.Endpoint, new DatabaseConnectionOptions(requireEncryption: true, trustServerCertificate: trust));

    [Theory]
    [MemberData(nameof(TrustSettings))]
    public async Task DiscoveryReportsAServerWithoutTls(bool trust)
    {
        var error = await Assert.ThrowsAsync<DatabaseProviderException>(() =>
            fixture.Backup.DiscoverAsync(Encrypted(trust), fixture.Credential, CancellationToken.None));
        Assert.Equal(DatabaseFailureKind.ServerTlsUnavailable, error.Kind);
    }

    [Theory]
    [MemberData(nameof(TrustSettings))]
    public async Task RestorePreCheckReportsAServerWithoutTls(bool trust)
    {
        var report = await fixture.Restore.PreCheckRestoreAsync(Encrypted(trust), fixture.Credential,
            new RestorePreCheckRequest(fixture.Name(), DatabaseRestoreMode.NewDatabase, false, null), CancellationToken.None);
        var failed = Assert.Single(report.Items, item => item.ReasonCode == PostgreSqlReasons.ServerTlsUnavailable);
        Assert.Equal(BackupPreCheckStatus.Failed, failed.Status);
        Assert.Equal(BackupFailureCause.InvalidConfiguration, failed.Cause);
    }

    [Theory]
    [InlineData(RestorePipelinePurpose.Precheck)]
    [InlineData(RestorePipelinePurpose.Execution)]
    public async Task RestorePipelineReportsAServerWithoutTls(RestorePipelinePurpose purpose)
    {
        var tenant = Guid.NewGuid();
        var key = new StorageObjectKey($"{tenant:N}/{Guid.NewGuid():N}/{Guid.NewGuid():N}/{Guid.NewGuid():N}/{ManagedBackupLayout.StagedFileName("source", DateTimeOffset.UtcNow, Guid.NewGuid())}");
        var path = Path.Combine(fixture.DirectoryPath, key.Value.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await File.WriteAllBytesAsync(path, [1]);
        var storage = new LocalStorageProvider(new LocalStorageOptions(fixture.DirectoryPath));
        var pipeline = new DatabaseRestorePipeline([fixture.Restore], [storage], NullLogger<DatabaseRestorePipeline>.Instance);
        var connection = new DatabaseConnectionSettings(
            new DatabaseEndpoint("localhost", fixture.Settings.Endpoint.Port),
            new DatabaseConnectionOptions(requireEncryption: true, trustServerCertificate: false));
        var request = new DatabaseRestorePipelineRequest(DatabaseEngineId.PostgreSql, connection, fixture.Credential, fixture.Name(),
            DatabaseRestoreMode.NewDatabase, StagingArea.SameHost(fixture.DirectoryPath), storage.Id, key, tenant, 1,
            new ContentChecksum(ChecksumAlgorithm.Sha256, new string('a', 64)), purpose: purpose);

        var result = await pipeline.RunRestoreAsync(request, null, CancellationToken.None);

        Assert.Equal("InvalidConfiguration", result.Failure!.Code);
        Assert.Equal("server_tls_unavailable", result.Failure.ReasonCode);
        var failed = Assert.Single(result.PreCheck, item => item.Status == BackupPreCheckStatus.Failed);
        Assert.Equal("server_tls_unavailable", failed.ReasonCode);
        Assert.Equal(BackupFailureCause.InvalidConfiguration, failed.Cause);
    }

    [Fact]
    public async Task BackupPreCheckReportsAServerWithoutTls()
    {
        var report = await fixture.Backup.PreCheckFullBackupAsync(Encrypted(false), fixture.Credential,
            new FullBackupPreCheckRequest("postgres", staging: StagingArea.SameHost(fixture.DirectoryPath)), CancellationToken.None);
        Assert.Contains(report.Items, item => item.ReasonCode == PostgreSqlReasons.ServerTlsUnavailable && item.Status == BackupPreCheckStatus.Failed);
    }
}
