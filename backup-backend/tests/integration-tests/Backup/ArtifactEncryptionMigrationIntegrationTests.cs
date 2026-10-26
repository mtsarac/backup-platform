using BackupPlatform.Domain.Backups;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace BackupPlatform.IntegrationTests.Backup;

public sealed class ArtifactEncryptionMigrationIntegrationTests
{
    [Fact]
    public async Task UpgradeLeavesLegacyArtifactBytesChecksumsReplicasAndDownloadHoldsUntouched()
    {
        await using var database = new PostgreSqlFixture();
        await database.InitializeAsync();
        await using var scenario = await DownloadApiScenario.CreateAsync(database);
        var prepared = await scenario.CreateSessionAsync();
        var id = prepared.GetProperty("sessionId").GetGuid();
        var tenant = scenario.Backup.Backup.Setup.Tenant.Id;
        var before = (await scenario.Store().GetArtifactAsync(tenant, scenario.Accepted.ArtifactId, default))!;
        var hold = (await scenario.Store().GetAsync(tenant, id, default))!;
        await using (var context = database.CreatePlatformContext())
        {
            var migrator = context.GetService<IMigrator>();
            var migrations = (await context.Database.GetAppliedMigrationsAsync()).ToArray();
            var encryption = Array.IndexOf(migrations, "20261018212736_ArtifactEncryption");
            Assert.True(encryption > 0);
            await migrator.MigrateAsync(migrations[encryption - 1]);
            await migrator.MigrateAsync();
            Assert.False(context.Database.HasPendingModelChanges());
        }
        var after = (await scenario.Store().GetArtifactAsync(tenant, before.Id, default))!;
        Assert.Equal(BackupArtifact.PlainFormat, after.Format);
        Assert.Null(after.EncryptionKeyId);
        Assert.Null(after.EncryptionKeyVersion);
        Assert.Null(after.EncryptionWrappedDekBase64);
        Assert.Null(after.EncryptionRecipientsJson);
        Assert.Equal((before.SourceChecksum, before.ArtifactChecksum, before.SizeBytes), (after.SourceChecksum, after.ArtifactChecksum, after.SizeBytes));
        Assert.Equal(before.Replicas.Select(r => (r.Id, r.Key, r.Checksum, r.SizeBytes, r.Status)), after.Replicas.Select(r => (r.Id, r.Key, r.Checksum, r.SizeBytes, r.Status)));
        var retained = (await scenario.Store().GetAsync(tenant, id, default))!;
        Assert.Equal((hold.State, hold.ExpiresAt, hold.ReplicaId), (retained.State, retained.ExpiresAt, retained.ReplicaId));
        Assert.False(retained.Decrypted);
        Assert.Null(retained.BreakGlassRequestId);
        Assert.Equal(scenario.Bytes, await File.ReadAllBytesAsync(scenario.FilePath));
        using var content = await scenario.Admin.GetAsync(prepared.GetProperty("downloadUrl").GetString()!);
        Assert.Equal(System.Net.HttpStatusCode.OK, content.StatusCode);
        Assert.Equal(scenario.Bytes, await content.Content.ReadAsByteArrayAsync());
    }
}
