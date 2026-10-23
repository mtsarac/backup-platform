using BackupPlatform.Agents.Capabilities;
using BackupPlatform.Application.Backups;
using BackupPlatform.Application.Restores;
using BackupPlatform.Contracts.Backups;
using BackupPlatform.Contracts.Restores;
using BackupPlatform.Domain.Capabilities;
using BackupPlatform.Domain.Identity;
using BackupPlatform.Domain.Restores;
using BackupPlatform.StorageProviders.Abstractions;
using static BackupPlatform.IntegrationTests.RestoreWorld;

namespace BackupPlatform.IntegrationTests;

/// <summary>
/// An SFTP or Google Drive replica is restored by the agent itself (also the R1 verification restore) when the target agent advertises direct
/// storage access; otherwise the backend relay stays. The backend never opens the replica to decide, because it holds no session for it.
/// </summary>
public sealed class RestoreDirectSourceTests(PostgreSqlFixture fixture) : IClassFixture<PostgreSqlFixture>
{
    private readonly RestoreWorld _world = new(fixture);

    private sealed class RecordingResolver : IBackupStorageResolver
    {
        public int Resolutions { get; private set; }
        public IReadOnlyList<BackupManagedStorage> ManagedStorages => [];
        public IStorageProvider? Resolve(TenantId tenantId, string storageProvider, string storageTarget)
        {
            Resolutions++;
            return null;
        }
    }

    private async Task<RestoreSeed> SeedExternalAsync(string provider, bool direct)
    {
        var agent = await _world.Harness.AddAgentAsync();
        await _world.EnableRestoreAsync(agent);
        await using (var context = fixture.CreatePlatformContext())
        {
            context.AgentCapabilities.Add(new AgentCapabilityRecord(agent.AgentId, agent.TenantId, RestoreTaskTypes.MssqlVerificationCapability, CapabilitySupport.Supported, null, "revision-2", Accepted));
            if (direct)
                context.AgentCapabilities.Add(new AgentCapabilityRecord(agent.AgentId, agent.TenantId, TaskStorageProtocol.DirectCapability, CapabilitySupport.Supported, null, "revision-3", Accepted));
            await context.SaveChangesAsync();
        }
        var job = await RetentionKit.AddJobAsync(_world.Harness, agent, null);
        var backup = await RetentionKit.SeedAsync(_world.Harness, agent, job, Accepted, storageProvider: provider, storageTarget: provider);
        // The replica is addressed by its storage connection, which is what an external backup publishes.
        var connectionId = (await _world.ReplicaAsync(backup.ReplicaId)).StorageConnectionId;
        await _world.ExecuteAsync("UPDATE backup_replicas SET \"StorageTarget\" = @target WHERE \"Id\" = @id", ("target", connectionId.ToString("D")), ("id", backup.ReplicaId));
        return new RestoreSeed(agent, backup);
    }

    [Theory]
    [InlineData("sftp", RestorePurpose.Execution)]
    [InlineData("google-drive", RestorePurpose.Execution)]
    [InlineData("sftp", RestorePurpose.Verification)]
    [InlineData("google-drive", RestorePurpose.Verification)]
    public async Task AnExternalReplicaIsReadDirectlyByADirectCapableAgentWithoutAnyBackendProbe(string provider, RestorePurpose purpose)
    {
        var seed = await SeedExternalAsync(provider, direct: true);
        var resolver = new RecordingResolver();
        var store = _world.Store(resolver);
        var command = Command(seed) with { Purpose = purpose };
        if (purpose == RestorePurpose.Verification) command = command with { DatabaseName = PlatformDatabaseNames.NewVerificationName() };

        var created = await store.CreateAsync(command, default);
        Assert.Equal(RestoreCreateOutcome.Created, created.Outcome);
        Assert.Equal(RestoreSourceAccess.AgentDirect, created.Operation!.SourceAccess);
        Assert.Equal(1, await _world.ActivePinsAsync(seed.ReplicaId));

        var claim = await MustClaimAsync(store, seed.Agent, Now);

        Assert.Equal((seed.ReplicaId, RestoreSourceAccess.AgentDirect), (claim.Replica.Id, claim.Operation.SourceAccess));
        Assert.Equal(0, resolver.Resolutions);
    }

    [Fact]
    public async Task AnAgentWithoutDirectStorageKeepsTheBackendRelayAndTheBackendProbe()
    {
        var seed = await SeedExternalAsync("sftp", direct: false);
        var resolver = new RecordingResolver();
        var store = _world.Store(resolver);

        var created = await store.CreateAsync(Command(seed), default);

        Assert.Equal(RestoreCreateOutcome.Created, created.Outcome);
        Assert.Equal(RestoreSourceAccess.BackendRelayed, created.Operation!.SourceAccess);
        // The relay path must still prove the replica readable before it hands the restore out; the resolver finds no storage, so there is nothing to claim.
        Assert.Null(await ClaimAsync(store, seed.Agent, Now));
        Assert.True(resolver.Resolutions > 0);
    }
}
