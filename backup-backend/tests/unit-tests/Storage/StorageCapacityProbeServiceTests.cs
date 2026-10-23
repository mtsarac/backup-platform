using BackupPlatform.Application.Agents;
using BackupPlatform.Application.Backups;
using BackupPlatform.Application.Platform;
using BackupPlatform.Contracts.Storage;
using BackupPlatform.Domain.Identity;
using BackupPlatform.Domain.Storage;
using BackupPlatform.Storage;

namespace BackupPlatform.UnitTests.Storage;

public sealed class StorageCapacityProbeServiceTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 24, 12, 0, 0, TimeSpan.Zero);

    [Theory]
    [InlineData(null, null, null, StorageCapacityStatus.CapacityUnavailable)]
    [InlineData(0L, 0L, 0L, StorageCapacityStatus.CapacityUnavailable)]
    [InlineData(100L, null, null, StorageCapacityStatus.CapacityUnavailable)]
    [InlineData(null, 10L, 90L, StorageCapacityStatus.CapacityUnavailable)]
    [InlineData(100L, 10L, null, StorageCapacityStatus.Available)]
    [InlineData(100L, null, 50L, StorageCapacityStatus.Available)]
    [InlineData(100L, 80L, null, StorageCapacityStatus.Warning)]
    [InlineData(100L, null, 10L, StorageCapacityStatus.Critical)]
    public void Unknown_capacity_is_never_reported_as_available(long? total, long? used, long? free, string expected) =>
        Assert.Equal(expected, StorageCapacityStatus.Evaluate(total, used, free));

    [Theory]
    [InlineData(StorageProviderTypes.S3)]
    [InlineData(StorageProviderTypes.Smb)]
    public async Task S3_and_SMB_report_not_measurable_without_storing_a_snapshot_or_asking_an_agent(string provider)
    {
        var world = new World(provider);

        var probed = await world.Service.ProbeAsync(world.Tenant, world.Connection.Id, default);
        var read = await world.Service.GetCapacityAsync(world.Tenant, world.Connection.Id, default);

        foreach (var response in new[] { probed, read })
        {
            Assert.Equal(StorageCapacityStatus.NotMeasurable, response!.Status);
            Assert.Null(response.TotalBytes);
            Assert.NotEmpty(response.ErrorMessage!);
        }
        Assert.Empty(world.Snapshots.Saved);
        Assert.Equal(0, world.Tasks.Enqueued);
    }

    [Theory]
    [InlineData(StorageProviderTypes.Local)]
    [InlineData(StorageProviderTypes.Sftp)]
    public async Task Local_and_SFTP_are_measured_by_an_agent(string provider)
    {
        var world = new World(provider);

        var response = await world.Service.ProbeAsync(world.Tenant, world.Connection.Id, default);

        Assert.Equal("Pending", response!.Status);
        Assert.Equal(1, world.Tasks.Enqueued);
        Assert.Empty(world.Snapshots.Saved);
    }

    [Fact]
    public async Task A_provider_with_no_probe_at_all_is_stored_as_unavailable()
    {
        var world = new World("ftp");

        var response = await world.Service.ProbeAsync(world.Tenant, world.Connection.Id, default);

        Assert.Equal(StorageCapacityStatus.CapacityUnavailable, response!.Status);
        Assert.Equal(StorageCapacityStatus.CapacityUnavailable, Assert.Single(world.Snapshots.Saved).Status);
    }

    private sealed class World
    {
        public TenantId Tenant { get; } = TenantId.New();
        public StorageConnection Connection { get; }
        public FakeSnapshots Snapshots { get; } = new();
        public FakeTasks Tasks { get; } = new();
        public StorageCapacityProbeService Service { get; }

        public World(string provider)
        {
            Connection = new StorageConnection(Guid.NewGuid(), Tenant, "Target", provider, StorageOwnership.TenantManaged, null, null, false, Now);
            Service = new StorageCapacityProbeService(new FakeConnections(Connection), Snapshots, Tasks, timeProvider: new FixedTime());
        }
    }

    private sealed class FixedTime : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => Now;
    }

    private sealed class FakeConnections(StorageConnection connection) : IStorageConnectionStore
    {
        public Task<StorageConnectionRecord?> FindAsync(Guid id, TenantId? tenantScope, CancellationToken cancellationToken) =>
            Task.FromResult<StorageConnectionRecord?>(id == connection.Id ? new StorageConnectionRecord(connection, null, 0, 0) : null);
        public Task<IReadOnlyList<StorageConnectionRecord>?> ListAsync(TenantId tenantId, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<PagedResult<StorageConnectionRecord>> SearchAsync(StorageConnectionFilter filter, int page, int pageSize, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<StorageConnectionResult> CreateAsync(StorageConnection connection, int? maxPerTenant, StorageActor actor, IReadOnlyDictionary<string, object?> auditDetails, DateTimeOffset now, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<StorageConnectionResult> MutateAsync(Guid id, TenantId? tenantScope, StorageMutation mutation, StorageActor actor, DateTimeOffset now, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<StorageConnectionResult> DeleteAsync(Guid id, TenantId? tenantScope, StorageActor actor, DateTimeOffset now, CancellationToken cancellationToken) => throw new NotSupportedException();
    }

    private sealed class FakeSnapshots : IStorageCapacitySnapshotStore
    {
        public List<StorageCapacitySnapshot> Saved { get; } = [];
        public Task<StorageCapacitySnapshot?> GetLatestAsync(TenantId tenantId, Guid connectionId, CancellationToken cancellationToken) => Task.FromResult(Saved.LastOrDefault());
        public Task<IReadOnlyList<StorageCapacitySnapshot>> GetHistoryAsync(TenantId tenantId, Guid connectionId, int limit, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<StorageCapacitySnapshot>>(Saved.AsEnumerable().Reverse().Take(limit).ToArray());
        public Task SaveAsync(StorageCapacitySnapshot snapshot, CancellationToken cancellationToken) { Saved.Add(snapshot); return Task.CompletedTask; }
    }

    private sealed class FakeTasks : IStorageCapacityTaskQueue
    {
        public int Enqueued { get; private set; }
        public Task<bool> CanUseSnapshotAsync(TenantId tenantId, Guid connectionId, DateTimeOffset now, CancellationToken cancellationToken) => Task.FromResult(true);
        public Task<StorageCapacityResponse> EnqueueAsync(TenantId tenantId, Guid connectionId, DateTimeOffset now, CancellationToken cancellationToken)
        {
            Enqueued++;
            return Task.FromResult(new StorageCapacityResponse { ConnectionId = connectionId, Status = "Pending", ReportedAt = now });
        }
        public Task<StorageCapacityResponse?> GetPendingAsync(TenantId tenantId, Guid connectionId, DateTimeOffset now, CancellationToken cancellationToken) => Task.FromResult<StorageCapacityResponse?>(null);
        public Task<StorageCapacityResponse?> RefreshPendingAsync(TenantId tenantId, Guid connectionId, DateTimeOffset now, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<CapacityProbeTaskRequest?> ClaimAsync(AgentIdentity identity, DateTimeOffset now, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<bool> IsLiveAsync(AgentIdentity identity, Guid taskId, Guid token, DateTimeOffset now, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<BackupMutationResult> CompleteAsync(AgentIdentity identity, Guid taskId, CapacityProbeCompletionRequest request, DateTimeOffset now, CancellationToken cancellationToken) => throw new NotSupportedException();
    }
}
