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

    [Fact]
    public async Task BackupPreCheckReportsAServerWithoutTls()
    {
        var report = await fixture.Backup.PreCheckFullBackupAsync(Encrypted(false), fixture.Credential,
            new FullBackupPreCheckRequest("postgres", staging: StagingArea.SameHost(fixture.DirectoryPath)), CancellationToken.None);
        Assert.Contains(report.Items, item => item.ReasonCode == PostgreSqlReasons.ServerTlsUnavailable && item.Status == BackupPreCheckStatus.Failed);
    }
}
