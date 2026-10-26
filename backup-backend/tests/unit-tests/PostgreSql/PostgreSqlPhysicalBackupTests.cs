using BackupPlatform.BackupProviders.Abstractions.Database;
using BackupPlatform.BackupProviders.PostgreSql;
using BackupPlatform.BackupProviders.PostgreSql.Tools;
using Microsoft.Extensions.Logging.Abstractions;

namespace BackupPlatform.UnitTests.PostgreSql;

public sealed class PostgreSqlPhysicalBackupTests
{
    [Theory]
    [InlineData(PostgreSqlBackupMode.Physical)]
    [InlineData(PostgreSqlBackupMode.Streaming)]
    public async Task PhysicalPreflightFailsClosedWhenBasebackupAndVerificationToolsAreAbsent(PostgreSqlBackupMode mode)
    {
        var directory = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var provider = new PostgreSqlBackupProvider(NullLogger<PostgreSqlBackupProvider>.Instance, new PostgreSqlProviderOptions(directory, mode));
            var result = await provider.PreCheckFullBackupAsync(
                new DatabaseConnectionSettings(new DatabaseEndpoint("127.0.0.1", port: 1)),
                new PasswordDatabaseCredential("replicator", new DatabaseSecret("not-logged")), new FullBackupPreCheckRequest("orders", [], StagingArea.SameHost(directory)), default);
            Assert.False(result.CanProceed);
            Assert.Contains(result.Items, item => item.ReasonCode == "postgres.physical.tools_missing");
            Assert.DoesNotContain("not-logged", string.Join(" ", result.Items.Select(item => item.Message)));
        }
        finally { Directory.Delete(directory); }
    }
}
