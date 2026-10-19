using BackupPlatform.Domain.Backups;
using BackupPlatform.ManagedService.Quota;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql;

namespace BackupPlatform.IntegrationTests.Backup;

public sealed partial class ManualReplicaRepairIntegrationTests
{
    [Fact]
    public async Task QuarantineUpgradePreservesExistingDataAndRegistersOldUntrackedConflictsOnce()
    {
        const string previous = "20261012200209_CanonicalBackupDestinations";
        var database = new PostgreSqlFixture();
        await database.InitializeAsync();
        try
        {
            await using (var empty = database.CreatePlatformContext())
                await empty.GetService<IMigrator>().MigrateAsync(previous);
            // The old schema predates artifact encryption; current code reads these columns, so they stand in until ArtifactEncryption itself adds them.
            await database.ExecuteAsync("ALTER TABLE backup_artifacts ADD COLUMN \"Format\" varchar(16) NOT NULL DEFAULT 'v1-plain', ADD COLUMN \"EncryptionKeyId\" varchar(32), " +
                "ADD COLUMN \"EncryptionKeyVersion\" integer, ADD COLUMN \"EncryptionWrappedDekBase64\" varchar(4096), ADD COLUMN \"EncryptionRecipientsJson\" varchar(2048)");
            var scenario = new ManualReplicaRepairIntegrationTests(database);
            var setup = await scenario.SetupAsync(QuotaAccountingMode.PhysicalReplicaBytes, new HashSet<string> { "central", "google-drive" });
            var source = setup.Artifact.Replicas.Single(r => r.StorageProvider == "central");
            var now = DateTimeOffset.UtcNow;
            var key = $"legacy-conflict-{Guid.NewGuid():N}.bak";
            foreach (var (connection, targetKey) in new[] { (setup.Sftp.Id, key), (setup.Sftp.Id, key), (setup.Central.Id, source.Key) })
            {
                var operation = new ReplicaRepairOperation(Guid.NewGuid(), setup.Agent.TenantId, setup.Artifact.Id, connection,
                    connection == setup.Sftp.Id ? "sftp" : "central", source.Id, now, totalBytes: Content.Length);
                var token = Guid.NewGuid();
                operation.Claim(token, "old-version", now, TimeSpan.FromMinutes(1));
                operation.SetTargetIntent(source.Id, targetKey);
                operation.Fail(token, ReplicaRepairFailureCodes.AlreadyVerified, "Target object already exists.", now);
                await setup.RepairStore.CreateOperationAsync(operation, default);
            }
            var before = (await scenario.World.StoredUsageAsync(setup.Agent.TenantId))!;
            await database.ExecuteAsync("ALTER TABLE backup_artifacts DROP COLUMN \"Format\", DROP COLUMN \"EncryptionKeyId\", DROP COLUMN \"EncryptionKeyVersion\", DROP COLUMN \"EncryptionWrappedDekBase64\", DROP COLUMN \"EncryptionRecipientsJson\"");
            await using var context = database.CreatePlatformContext();
            await context.GetService<IMigrator>().MigrateAsync();
            var replicas = await context.BackupReplicas.AsNoTracking().Where(r => r.TenantId == setup.Agent.TenantId).ToListAsync();
            var conflict = Assert.Single(replicas, r => r.StorageConnectionId == setup.Sftp.Id && r.Key == key);
            Assert.Equal(BackupReplicaStatus.Quarantined, conflict.Status);
            Assert.Equal("Failed", conflict.VerificationState);
            var retained = Assert.Single(replicas, r => r.Id == source.Id);
            Assert.Equal((source.Key, source.Checksum, source.SizeBytes, BackupReplicaStatus.Available),
                (retained.Key, retained.Checksum, retained.SizeBytes, retained.Status));
            Assert.All(await context.ReplicaRepairOperations.AsNoTracking().Where(r => r.TenantId == setup.Agent.TenantId).ToListAsync(),
                r => Assert.Equal(ReplicaRepairFailureCodes.TargetConflict, r.FailureCode));
            Assert.Null(await scenario.World.StoredUsageAsync(setup.Agent.TenantId));
            var rebuilt = await scenario.World.Usage().GetUsageAsync(setup.Agent.TenantId, default);
            Assert.Equal(before.UsedBytes + Content.Length, rebuilt!.UsedBytes);
            Assert.Equal(0, rebuilt.ReservedBytes);
            await context.GetService<IMigrator>().MigrateAsync();
            Assert.Equal(1, await context.BackupReplicas.CountAsync(r => r.TenantId == setup.Agent.TenantId && r.Key == key));
            var downgrade = await Assert.ThrowsAsync<PostgresException>(() => context.GetService<IMigrator>().MigrateAsync(previous));
            Assert.Equal(PostgresErrorCodes.RaiseException, downgrade.SqlState);
            Assert.Equal(BackupReplicaStatus.Quarantined, (await context.BackupReplicas.AsNoTracking().SingleAsync(r => r.Id == conflict.Id)).Status);
        }
        finally
        {
            await database.DisposeAsync();
        }
    }
}
