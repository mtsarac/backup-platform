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
/// storage access; an agent without it is refused, because the backend relays nothing. The backend never opens the replica to decide, because it holds no session for it.
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
            await context.SaveChangesAsync();
        }
        var job = await RetentionKit.AddJobAsync(_world.Harness, agent, null);
        var backup = await RetentionKit.SeedAsync(_world.Harness, agent, job, Accepted, storageProvider: provider, storageTarget: provider);
        // The replica is addressed by its storage connection, which is what an external backup publishes.
        var connectionId = (await _world.ReplicaAsync(backup.ReplicaId)).StorageConnectionId;
        await _world.ExecuteAsync("UPDATE backup_replicas SET \"StorageTarget\" = @target WHERE \"Id\" = @id", ("target", connectionId.ToString("D")), ("id", backup.ReplicaId));
        // The backup was written by a direct-capable agent (the harness declares that on every agent); a later restore on an agent that no longer declares it must be refused.
        if (!direct)
            await _world.ExecuteAsync("DELETE FROM agent_capabilities WHERE \"AgentId\" = @agent AND \"Identifier\" = @identifier", ("agent", agent.AgentId.Value), ("identifier", TaskStorageProtocol.DirectCapability));
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
    public async Task AnAgentWithoutDirectStorageIsRefusedWithNoRelayFallback()
    {
        var seed = await SeedExternalAsync("sftp", direct: false);
        var resolver = new RecordingResolver();
        var store = _world.Store(resolver);

        var created = await store.CreateAsync(Command(seed), default);

        Assert.Equal(RestoreCreateOutcome.SourceRejected, created.Outcome);
        Assert.Equal(RestoreSourceRejection.NoAccessibleReplica, created.Rejection);
        Assert.Null(await ClaimAsync(store, seed.Agent, Now));
        Assert.Equal(0, resolver.Resolutions);
    }
}
