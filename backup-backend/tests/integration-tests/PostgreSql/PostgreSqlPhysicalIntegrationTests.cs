using System.Formats.Tar;
using BackupPlatform.BackupProviders.Abstractions.Database;
using BackupPlatform.BackupProviders.PostgreSql;
using BackupPlatform.BackupProviders.PostgreSql.Tools;
using Microsoft.Extensions.Logging.Abstractions;

namespace BackupPlatform.IntegrationTests.PostgreSql;

[Trait("Category", "PostgreSqlPhysical")]
public sealed class PostgreSqlPhysicalIntegrationTests(PostgreSqlProviderFixture fixture) : IClassFixture<PostgreSqlProviderFixture>
{
    [Theory]
    [InlineData(PostgreSqlBackupMode.Physical, "fetch")]
    [InlineData(PostgreSqlBackupMode.Streaming, "stream")]
    public async Task RealPhysicalCopyVerifiesManifestAndWalAndCannotEnterLogicalRestore(PostgreSqlBackupMode mode, string walMethod)
    {
        var database = await fixture.DatabaseAsync(populated: true);
        var provider = new PostgreSqlBackupProvider(NullLogger<PostgreSqlBackupProvider>.Instance, fixture.Options with { BackupMode = mode });
        var result = await provider.ExecuteFullBackupAsync(fixture.Settings, fixture.Credential,
            new FullBackupRequest(database, StagingArea.SameHost(fixture.DirectoryPath), Guid.NewGuid().ToString("N") + ".tar",
                new FullBackupOptions(checksum: BackupOptionMode.IfSupported, compression: BackupOptionMode.Off)), null, default);
        Assert.True(result.Status == FullBackupStatus.Succeeded, result.Failure?.ReasonCode + ": " + result.Failure?.Message);
        Assert.Equal(BackupVerificationStatus.Passed, result.Verification.Status);
        Assert.Equal("cluster", result.Source!.Details["backup-scope"]);
        Assert.Equal(walMethod, result.Source.Details["wal-method"]);
        Assert.Equal("offline-cluster", result.Source.Details["restore-method"]);
        Assert.Equal("physical-tar", result.Source.Details["archive-format"]);
        var file = Assert.IsType<StagedBackupFile>(result.StagedFile);
        var entries = new List<string>();
        await using (var input = File.OpenRead(file.AgentPath.Value))
        await using (var archive = new TarReader(input))
        {
            while (await archive.GetNextEntryAsync() is { } entry) entries.Add(entry.Name.TrimStart('.', '/'));
        }
        Assert.Contains("backup-platform.json", entries);
        Assert.Contains("data/PG_VERSION", entries);
        Assert.Contains("data/backup_manifest", entries);
        Assert.Contains(entries, entry => entry.StartsWith("data/pg_wal/", StringComparison.Ordinal));
        Assert.DoesNotContain(Directory.EnumerateDirectories(fixture.DirectoryPath), directory => Path.GetFileName(directory).StartsWith(".physical-", StringComparison.Ordinal));
        var inspected = await fixture.Restore.InspectBackupAsync(fixture.Settings, fixture.Credential,
            new RestoreInspectionRequest(file.ServerPath, true, agentFile: file.AgentPath), default);
        Assert.NotNull(inspected.Failure);
        Assert.Null(inspected.Inspection);
    }

    [Fact]
    public async Task Failed_progress_reporting_cannot_publish_an_unobserved_physical_backup()
    {
        var database = await fixture.DatabaseAsync(populated: true);
        var provider = new PostgreSqlBackupProvider(NullLogger<PostgreSqlBackupProvider>.Instance,
            fixture.Options with { BackupMode = PostgreSqlBackupMode.Physical });
        var name = Guid.NewGuid().ToString("N") + ".tar";
        var result = await provider.ExecuteFullBackupAsync(fixture.Settings, fixture.Credential,
            new FullBackupRequest(database, StagingArea.SameHost(fixture.DirectoryPath), name,
                new FullBackupOptions(checksum: BackupOptionMode.IfSupported, compression: BackupOptionMode.Off)),
            new FailedProgress(), default);
        Assert.Equal(FullBackupStatus.Failed, result.Status);
        Assert.Equal("postgres.physical.operation_failed", result.Failure?.ReasonCode);
        Assert.Null(result.StagedFile);
        Assert.False(File.Exists(Path.Combine(fixture.DirectoryPath, name)));
        Assert.DoesNotContain(Directory.EnumerateDirectories(fixture.DirectoryPath),
            path => Path.GetFileName(path).StartsWith(".physical-", StringComparison.Ordinal));
    }

    private sealed class FailedProgress : IProgress<BackupProgress>
    {
        public void Report(BackupProgress value) => throw new IOException("Progress sink is unavailable.");
    }
}
