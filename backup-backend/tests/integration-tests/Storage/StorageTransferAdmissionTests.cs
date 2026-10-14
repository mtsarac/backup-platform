using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using BackupPlatform.Agents;
using BackupPlatform.Api.Security;
using BackupPlatform.Application.Backups;
using BackupPlatform.Application.Restores;
using BackupPlatform.Contracts.Agents;
using BackupPlatform.Contracts.Backups;
using BackupPlatform.Domain.Identity;
using BackupPlatform.Domain.Restores;
using BackupPlatform.Domain.Storage;
using BackupPlatform.Infrastructure.Backups;
using BackupPlatform.Infrastructure.Persistence;
using BackupPlatform.Infrastructure.Restores;
using BackupPlatform.StorageProviders.Abstractions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Npgsql;

namespace BackupPlatform.IntegrationTests.Storage;

public sealed class StorageTransferAdmissionTests(PostgreSqlFixture fixture) : IClassFixture<PostgreSqlFixture>
{
    private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(15);
    private static readonly string Checksum = new('a', 64);
    private sealed record Target(TenantSetup Tenant, EnrolledAgent Agent, Guid ConnectionId, TaskClaim Claim, string Key);
    private static Target AnotherKey(Target target) => target with
    {
        Key = target.Key[..(target.Key.LastIndexOf('/') + 1)] + ManagedBackupLayout.StagedFileName("master", DateTimeOffset.UtcNow, Guid.NewGuid())
    };

    [Theory]
    [InlineData(4, 0)]
    [InlineData(5, 1)]
    [InlineData(6, 2)]
    [InlineData(8, 4)]
    [InlineData(12, 4)]
    public async Task Pool_budget_and_tenant_limits_leave_normal_transactions_available(int poolSize, int slots)
    {
        await using var world = await RelayWorld.CreateAsync(fixture, poolSize);
        var first = await world.TargetAsync();
        var second = await world.TargetAsync();
        var third = await world.TargetAsync();
        var admission = world.World.Factory.Services.GetRequiredService<AgentTransferAdmission>();
        Assert.Equal(slots, admission.GlobalSlots);
        Assert.Equal(Math.Min(2, slots), admission.TenantSlots);
        var pending = new List<Task<HttpResponseMessage>>();
        try
        {
            for (var index = 0; index < slots; index++)
            {
                var target = AnotherKey(index < 2 ? first : second);
                var gate = world.Provider.Block(target.Key);
                pending.Add(world.SendAsync(target, index == 0 ? "upload" : "verify"));
                await gate.Entered.Task.WaitAsync(Deadline);
            }
            var calls = world.Provider.Calls;
            foreach (var operation in new[] { "upload", "verify", "delete" })
            {
                using var rejected = await world.SendAsync(AnotherKey(third), operation).WaitAsync(Deadline);
                Assert.Equal(HttpStatusCode.TooManyRequests, rejected.StatusCode);
                Assert.Equal(TimeSpan.FromSeconds(1), rejected.Headers.RetryAfter!.Delta);
                Assert.Equal("retry_later", AgentWorld.ReadJson(rejected).GetProperty("action").GetString());
            }
            Assert.Equal(calls, world.Provider.Calls);
            using var normal = await world.World.HeartbeatAsync(first.Agent).WaitAsync(Deadline);
            Assert.Equal(HttpStatusCode.OK, normal.StatusCode);
            await using var database = PlatformDbContext.CreateForPlatformOperations(world.Options);
            await using var transaction = await database.Database.BeginTransactionAsync().WaitAsync(Deadline);
            Assert.True(await database.Tenants.AnyAsync().WaitAsync(Deadline));
            await transaction.CommitAsync().WaitAsync(Deadline);
        }
        finally
        {
            world.Provider.ReleaseAll();
            foreach (var request in pending)
            {
                using var response = await request.WaitAsync(Deadline);
                Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            }
        }
        await world.AssertPoolReturnedAsync();
    }

    [Fact]
    public async Task One_tenant_cannot_monopolize_slots_and_invalid_identity_is_fenced_before_admission()
    {
        await using var world = await RelayWorld.CreateAsync(fixture, 8);
        var first = await world.TargetAsync();
        var second = await world.TargetAsync();
        var pending = new List<Task<HttpResponseMessage>>();
        try
        {
            for (var index = 0; index < 2; index++)
            {
                var target = AnotherKey(first);
                var gate = world.Provider.Block(target.Key);
                pending.Add(world.SendAsync(target, index == 0 ? "upload" : "verify"));
                await gate.Entered.Task.WaitAsync(Deadline);
            }
            using var rejected = await world.SendAsync(first, "delete").WaitAsync(Deadline);
            Assert.Equal(HttpStatusCode.TooManyRequests, rejected.StatusCode);
            using var foreign = await world.SendAsync(first with { Agent = second.Agent }, "verify").WaitAsync(Deadline);
            Assert.Equal(HttpStatusCode.Conflict, foreign.StatusCode);
            Assert.Equal("lease_conflict", AgentWorld.ReadJson(foreign).GetProperty("error").GetString());
            var otherGate = world.Provider.Block(second.Key);
            pending.Add(world.SendAsync(second, "delete"));
            await otherGate.Entered.Task.WaitAsync(Deadline);
            Assert.Equal(3, world.Provider.Calls);
        }
        finally
        {
            world.Provider.ReleaseAll();
            foreach (var request in pending)
            {
                using var response = await request.WaitAsync(Deadline);
                Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            }
        }
        await world.AssertPoolReturnedAsync();
    }

    [Fact]
    public async Task Retention_cannot_claim_the_upload_intent_until_the_writer_releases_its_transfer_lease()
    {
        await using var world = await RelayWorld.CreateAsync(fixture, 5);
        var target = await world.TargetAsync();
        var gate = world.Provider.Block(target.Key);
        var pending = world.SendAsync(target, "upload");
        Guid replicaId;
        try
        {
            await gate.Entered.Task.WaitAsync(Deadline);
            // A terminal failure exposes the retained intent to cleanup while its old remote writer is still unwinding.
            using var failed = await world.World.SendAgentAsync(HttpMethod.Post, $"/api/agent/tasks/{target.Claim.AttemptId}/complete",
                target.Agent.CredentialId, target.Agent.Secret, body: new BackupTaskCompletionRequest
                {
                    LeaseToken = target.Claim.LeaseToken, Status = "Failed", FailureCode = "StorageUnavailable"
                }).WaitAsync(Deadline);
            Assert.Equal(HttpStatusCode.NoContent, failed.StatusCode);
            await using var db = fixture.CreatePlatformContext();
            replicaId = await db.BackupReplicas.Where(row => row.Key == target.Key).Select(row => row.Id).SingleAsync();
            var retention = new PostgreSqlBackupRetentionStore(world.Options);
            var claims = await retention.ClaimDueDeletionsAsync("admission-test", DateTimeOffset.UtcNow, TimeSpan.FromMinutes(1), 500, default).WaitAsync(Deadline);
            Assert.DoesNotContain(claims, claim => claim.ReplicaId == replicaId);
        }
        finally
        {
            gate.Release.TrySetResult();
            using var response = await pending.WaitAsync(Deadline);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        }
        var after = await new PostgreSqlBackupRetentionStore(world.Options)
            .ClaimDueDeletionsAsync("admission-test", DateTimeOffset.UtcNow, TimeSpan.FromMinutes(1), 500, default).WaitAsync(Deadline);
        Assert.Contains(after, claim => claim.ReplicaId == replicaId);
        await world.AssertPoolReturnedAsync();
    }

    [Theory]
    [InlineData("upload")]
    [InlineData("verify")]
    [InlineData("delete")]
    public async Task Provider_failure_returns_the_connection_and_permit_without_changing_storage_errors(string operation)
    {
        await using var world = await RelayWorld.CreateAsync(fixture, 5);
        var target = await world.TargetAsync();
        var gate = world.Provider.Block(target.Key);
        gate.Failure = new StorageException(StorageFailureKind.Io, "Controlled provider failure.");
        var pending = world.SendAsync(target, operation);
        try
        {
            await gate.Entered.Task.WaitAsync(Deadline);
        }
        finally
        {
            gate.Release.TrySetResult();
        }
        using var failed = await pending.WaitAsync(Deadline);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, failed.StatusCode);
        Assert.Equal("Io", AgentWorld.ReadJson(failed).GetProperty("failureKind").GetString());
        using var next = await world.SendAsync(AnotherKey(target), "verify").WaitAsync(Deadline);
        Assert.Equal(HttpStatusCode.OK, next.StatusCode);
        await world.AssertPoolReturnedAsync();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Request_cancellation_or_host_shutdown_releases_the_transfer_lease(bool shutdown)
    {
        await using var world = await RelayWorld.CreateAsync(fixture, 5);
        var target = await world.TargetAsync();
        var gate = world.Provider.Block(target.Key);
        using var cancellation = new CancellationTokenSource();
        var pending = world.SendAsync(target, "upload", cancellation.Token);
        try
        {
            await gate.Entered.Task.WaitAsync(Deadline);
            if (shutdown) world.World.Factory.Services.GetRequiredService<IHostApplicationLifetime>().StopApplication();
            else cancellation.Cancel();
            await gate.Exited.Task.WaitAsync(Deadline);
            await world.WaitForNoLeasesAsync(target.ConnectionId, target.Key);
            try
            {
                using var response = await pending.WaitAsync(Deadline);
                Assert.False(response.IsSuccessStatusCode);
            }
            catch (OperationCanceledException) { }
            if (!shutdown)
            {
                await world.WaitForAdmissionAsync(target.Tenant.Tenant.Id);
                using var next = await world.SendAsync(AnotherKey(target), "verify").WaitAsync(Deadline);
                Assert.Equal(HttpStatusCode.OK, next.StatusCode);
            }
            await world.AssertPoolReturnedAsync();
        }
        finally
        {
            cancellation.Cancel();
            gate.Release.TrySetResult();
        }
    }

    [Fact]
    public async Task Cancellation_while_waiting_for_another_writers_lease_leaves_that_lease_alone()
    {
        await using var world = await RelayWorld.CreateAsync(fixture, 5);
        var target = await world.TargetAsync();
        await world.HoldLeaseAsync(target.ConnectionId, target.Key, TimeSpan.FromMinutes(5));
        using var cancellation = new CancellationTokenSource();
        var pending = world.SendAsync(target, "verify", cancellation.Token);
        try
        {
            await Task.Delay(700);
            Assert.False(pending.IsCompleted, "a verification waits for the key's live lease");
            cancellation.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending.WaitAsync(Deadline));
            Assert.Equal(0, world.Provider.Calls);
            Assert.Equal(1, await world.LeaseCountAsync(target.ConnectionId, target.Key));
            await world.AssertPoolReturnedAsync();
        }
        finally
        {
            cancellation.Cancel();
            await world.DeleteLeaseAsync(target.ConnectionId, target.Key);
        }
        using var next = await world.SendAsync(target, "verify").WaitAsync(Deadline);
        Assert.Equal(HttpStatusCode.OK, next.StatusCode);
        await world.WaitForNoLeasesAsync(target.ConnectionId, target.Key);
    }

    [Fact]
    public async Task No_database_connection_or_lock_is_held_while_a_transfer_runs()
    {
        await using var world = await RelayWorld.CreateAsync(fixture, 5);
        var target = await world.TargetAsync();
        var gate = world.Provider.Block(target.Key);
        var pending = world.SendAsync(target, "upload");
        try
        {
            await gate.Entered.Task.WaitAsync(Deadline);
            // The provider call is in flight (it may take hours when paced): the lease row is the only thing holding the key.
            Assert.Equal(1, await world.LeaseCountAsync(target.ConnectionId, target.Key));
            Assert.Equal(0, await world.ActiveSessionsAsync());
            await world.AssertPoolReturnedAsync();
        }
        finally
        {
            gate.Release.TrySetResult();
        }
        using var response = await pending.WaitAsync(Deadline);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        await world.WaitForNoLeasesAsync(target.ConnectionId, target.Key);
    }

    [Theory]
    [InlineData("upload")]
    [InlineData("verify")]
    public async Task A_holder_whose_lease_was_taken_over_stops_its_provider_call_and_answers_a_lease_conflict(string operation)
    {
        await using var world = await RelayWorld.CreateAsync(fixture, 5, new BackupTransferLeaseOptions(TimeSpan.FromSeconds(6), TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(2)));
        var target = await world.TargetAsync();
        var gate = world.Provider.Block(target.Key);
        var pending = world.SendAsync(target, operation);
        try
        {
            await gate.Entered.Task.WaitAsync(Deadline);
            await world.TakeOverLeaseAsync(target.ConnectionId, target.Key);
            await gate.Exited.Task.WaitAsync(Deadline);
        }
        finally
        {
            gate.Release.TrySetResult();
        }
        using var response = await pending.WaitAsync(Deadline);
        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal("lease_conflict", AgentWorld.ReadJson(response).GetProperty("error").GetString());
        await world.AssertPoolReturnedAsync();
    }

    [Fact]
    public async Task Restore_verification_uses_the_same_budget_and_rechecks_its_lease_after_waiting()
    {
        await using var world = await RelayWorld.CreateAsync(fixture, 5);
        var source = await world.TargetAsync();
        var restore = await world.RestoreAsync(source);
        var writer = await world.TargetAsync();
        var gate = world.Provider.Block(writer.Key);
        var pending = world.SendAsync(writer, "upload");
        try
        {
            await gate.Entered.Task.WaitAsync(Deadline);
            using var rejected = await world.SendRestoreAsync(source, restore).WaitAsync(Deadline);
            Assert.Equal(HttpStatusCode.TooManyRequests, rejected.StatusCode);
            Assert.Equal(1, world.Provider.Calls);
        }
        finally
        {
            gate.Release.TrySetResult();
            using var response = await pending.WaitAsync(Deadline);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        }
        var restoreGate = world.Provider.Block(source.Key);
        var verifying = world.SendRestoreAsync(source, restore);
        try
        {
            await restoreGate.Entered.Task.WaitAsync(Deadline);
            using var rejected = await world.SendAsync(AnotherKey(writer), "upload").WaitAsync(Deadline);
            Assert.Equal(HttpStatusCode.TooManyRequests, rejected.StatusCode);
            Assert.Equal(2, world.Provider.Calls);
        }
        finally
        {
            restoreGate.Release.TrySetResult();
            using var response = await verifying.WaitAsync(Deadline);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        }
        await world.HoldLeaseAsync(source.ConnectionId, source.Key, TimeSpan.FromMinutes(5));
        var waiting = world.SendRestoreAsync(source, restore);
        try
        {
            await Task.Delay(700);
            Assert.False(waiting.IsCompleted);
            await using var db = fixture.CreatePlatformContext();
            await db.Database.ExecuteSqlInterpolatedAsync($"UPDATE restore_attempts SET \"LeaseToken\"={Guid.NewGuid()} WHERE \"Id\"={restore.AttemptId}");
        }
        finally
        {
            await world.DeleteLeaseAsync(source.ConnectionId, source.Key);
        }
        using var fenced = await waiting.WaitAsync(Deadline);
        Assert.Equal(HttpStatusCode.Conflict, fenced.StatusCode);
        Assert.Equal("lease_conflict", AgentWorld.ReadJson(fenced).GetProperty("error").GetString());
        Assert.Equal(2, world.Provider.Calls);
        await world.AssertPoolReturnedAsync();
    }

    private sealed class RelayWorld(PostgreSqlFixture fixture, AgentWorld agentWorld, DbContextOptions<PlatformDbContext> options,
        string connectionString, string applicationName, BlockingProvider provider) : IAsyncDisposable
    {
        private readonly List<(TenantId Tenant, Guid Id)> _runs = [];
        private readonly List<(TenantId Tenant, Guid Id)> _restores = [];

        public AgentWorld World { get; } = agentWorld;
        public DbContextOptions<PlatformDbContext> Options { get; } = options;
        public string ApplicationName { get; } = applicationName;
        public BlockingProvider Provider { get; } = provider;

        public static async Task<RelayWorld> CreateAsync(PostgreSqlFixture fixture, int poolSize, BackupTransferLeaseOptions? leaseTiming = null)
        {
            var name = $"relay-admission-{Guid.NewGuid():N}";
            var connectionString = new NpgsqlConnectionStringBuilder(fixture.ConnectionString)
            {
                Pooling = true, MaxPoolSize = poolSize, MinPoolSize = 0, ApplicationName = name,
                NoResetOnClose = true, Timeout = 3, CommandTimeout = 5
            }.ConnectionString;
            var options = new DbContextOptionsBuilder<PlatformDbContext>().UseNpgsql(connectionString).Options;
            var provider = new BlockingProvider();
            var world = await AgentWorld.CreateAsync(fixture, limits: new AgentRateLimitOptions { PollPerAgent = 1000 }, configureServices: services =>
            {
                services.RemoveAll<DbContextOptions<PlatformDbContext>>();
                services.AddSingleton(options);
                services.RemoveAll<IBackupStorageResolver>();
                services.AddSingleton<IBackupStorageResolver>(new RelayResolver(provider));
                if (leaseTiming is not null)
                {
                    services.RemoveAll<BackupTransferLeaseService>();
                    services.AddSingleton(new BackupTransferLeaseService(options, leaseTiming));
                }
            });
            return new RelayWorld(fixture, world, options, connectionString, name, provider);
        }

        public async Task<Target> TargetAsync()
        {
            var tenant = await World.CreateTenantAsync();
            await World.EntitleAsync(tenant.Tenant);
            var agent = await World.DeclareBackupCapabilityAsync(await World.EnrollAsync(tenant));
            var id = Guid.NewGuid();
            await using (var db = fixture.CreatePlatformContext())
            {
                var connection = new StorageConnection(id, tenant.Tenant.Id, "Blocked relay", "google-drive", StorageOwnership.TenantManaged,
                    "tenant", new Dictionary<string, string>(), false, DateTimeOffset.UtcNow);
                connection.AttachSecret($"sc-{id:N}-relay", ["refreshToken"], DateTimeOffset.UtcNow);
                db.StorageConnections.Add(connection);
                await db.SaveChangesAsync();
            }
            var root = BackupApiScenario.RootOf(tenant);
            using var created = await tenant.Admin.SendAsync(HttpMethod.Post, root + "/backup-jobs", new
            {
                name = "Admission test", agentId = agent.AgentId,
                configuration = new BackupExecutionConfiguration
                {
                    Database = "master", Host = "sql-server", IntegratedSecurity = true,
                    Destinations = [BackupTestDestinations.Relayed("google-drive", id, primary: true)]
                }
            });
            Assert.Equal(HttpStatusCode.Created, created.StatusCode);
            var job = AgentWorld.ReadJson(created).GetProperty("id").GetGuid();
            using var csrf = await tenant.Admin.Client.GetAsync("/api/auth/antiforgery");
            using var start = new HttpRequestMessage(HttpMethod.Post, root + $"/backup-jobs/{job}/runs");
            start.Headers.Add("X-CSRF-TOKEN", AgentWorld.ReadJson(csrf).GetProperty("requestToken").GetString());
            start.Headers.Add("Idempotency-Key", Guid.NewGuid().ToString("N"));
            using var started = await tenant.Admin.Client.SendAsync(start);
            Assert.Equal(HttpStatusCode.OK, started.StatusCode);
            var run = AgentWorld.ReadJson(started).GetProperty("id").GetGuid();
            _runs.Add((tenant.Tenant.Id, run));
            using var polled = await World.PollAsync(agent);
            var task = Assert.Single(AgentWorld.ReadJson(polled).GetProperty("tasks").EnumerateArray()).GetProperty("backup");
            var claim = new TaskClaim(run, task.GetProperty("attemptId").GetGuid(), task.GetProperty("attemptNumber").GetInt32(), task.GetProperty("leaseToken").GetGuid());
            using var running = await World.SendAgentAsync(HttpMethod.Post, $"/api/agent/tasks/{claim.AttemptId}/start", agent.CredentialId, agent.Secret,
                body: new BackupTaskStartRequest { LeaseToken = claim.LeaseToken, SoftwareVersion = "1.2.3", ProtocolVersion = "1.0", Platform = "linux" });
            Assert.Equal(HttpStatusCode.OK, running.StatusCode);
            var key = ManagedBackupLayout.AttemptScope(tenant.Tenant.Id.Value, job, run, claim.AttemptId) + "/" + RetentionKit.ManagedFileName;
            return new Target(tenant, agent, id, claim, key);
        }

        public async Task<HttpResponseMessage> SendAsync(Target target, string operation, CancellationToken cancellationToken = default)
        {
            using var request = Request(target, target.Claim.AttemptId, target.Claim.LeaseToken, operation);
            return await World.Anonymous.SendAsync(request, cancellationToken);
        }

        private static HttpRequestMessage Request(Target target, Guid attempt, Guid lease, string operation, Guid? restore = null)
        {
            var request = new HttpRequestMessage(operation == "upload" ? HttpMethod.Put : operation == "delete" ? HttpMethod.Delete : HttpMethod.Post,
                $"/api/agent/{(restore is null ? "tasks" : "restore-tasks")}/{attempt}/storage/{(operation == "verify" ? "verify" : "content")}");
            request.Headers.TryAddWithoutValidation("Authorization", AgentAuthorizationScheme.FormatHeaderValue(target.Agent.CredentialId, target.Agent.Secret));
            request.Headers.Add(AgentProtocolHeader.Name, "1.0");
            request.Headers.Add(TaskStorageProtocol.LeaseHeader, lease.ToString("D"));
            request.Headers.Add(TaskStorageProtocol.KeyHeader, target.Key);
            if (restore is { } operationId) request.Headers.Add(TaskStorageProtocol.RestoreHeader, operationId.ToString("D"));
            else request.Headers.Add(TaskStorageProtocol.DestinationHeader, target.ConnectionId.ToString("D"));
            if (operation == "upload")
            {
                request.Headers.Add(TaskStorageProtocol.ChecksumHeader, Checksum);
                request.Content = new ByteArrayContent([1, 2, 3]);
                request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
            }
            else if (operation == "verify") request.Content = JsonContent.Create(new TaskStorageVerifyRequest(3, Checksum));
            return request;
        }

        public async Task<RestoreTask> RestoreAsync(Target target)
        {
            var now = World.Clock.GetUtcNow();
            using var completed = await World.SendAgentAsync(HttpMethod.Post, $"/api/agent/tasks/{target.Claim.AttemptId}/complete", target.Agent.CredentialId, target.Agent.Secret,
                body: new BackupTaskCompletionRequest
                {
                    LeaseToken = target.Claim.LeaseToken, Status = "Succeeded",
                    Artifact = new BackupTaskArtifact
                    {
                        SizeBytes = 3, SourceChecksum = Checksum, ArtifactChecksum = Checksum, CreatedAt = now,
                        SourceMetadataJson = System.Text.Json.JsonSerializer.Serialize(new
                        {
                            engine = "mssql", databaseName = "master", backupType = "Full",
                            backupStartedAt = now.AddSeconds(-10), backupFinishedAt = now.AddSeconds(-5),
                            backupVerification = new { state = "Passed", method = "VerifyOnly" }
                        }),
                        AppliedOptionsJson = "{\"copyOnly\":true,\"checksum\":true,\"compression\":true}",
                        Replicas = [new BackupTaskReplica
                        {
                            StorageProvider = "google-drive", StorageTarget = target.ConnectionId.ToString("D"), StorageConnectionId = target.ConnectionId,
                            Key = target.Key, SizeBytes = 3, Checksum = Checksum, VerificationState = "Verified", VerificationMethod = "ReadBack", CreatedAt = now, VerifiedAt = now
                        }]
                    },
                    Destinations = [new BackupTaskDestinationResult
                    {
                        StorageProvider = "google-drive", StorageConnectionId = target.ConnectionId, State = "Verified", Upload = "Succeeded", Verification = "Verified",
                        Key = target.Key, StartedAt = now.AddSeconds(-1), CompletedAt = now
                    }]
                });
            Assert.Equal(HttpStatusCode.NoContent, completed.StatusCode);
            using var capability = await World.SyncCapabilitiesAsync(target.Agent, AgentWorld.CapabilitySnapshot(AgentWorld.CapabilityReport(RestoreApiScenario.Capability)));
            Assert.Equal(HttpStatusCode.OK, capability.StatusCode);
            await using var db = fixture.CreatePlatformContext();
            var artifact = await db.BackupArtifacts.Where(row => row.AttemptId == target.Claim.AttemptId).Select(row => row.Id).SingleAsync();
            using var scope = World.Factory.Services.CreateScope();
            var store = scope.ServiceProvider.GetRequiredService<IRestoreStore>();
            var created = await store.CreateAsync(RestoreWorld.Command(target.Tenant.Tenant.Id, artifact, new AgentId(target.Agent.AgentId), now: now), default);
            Assert.Equal(RestoreCreateOutcome.Created, created.Outcome);
            _restores.Add((target.Tenant.Tenant.Id, created.Operation!.Id));
            using var poll = await World.PollAsync(target.Agent);
            var snapshot = Assert.Single(AgentWorld.ReadJson(poll).GetProperty("tasks").EnumerateArray()).GetProperty("restore");
            return new RestoreTask(snapshot.GetProperty("operationId").GetGuid(), snapshot.GetProperty("attemptId").GetGuid(), snapshot.GetProperty("leaseToken").GetGuid(), snapshot.Clone());
        }

        public async Task<HttpResponseMessage> SendRestoreAsync(Target target, RestoreTask restore)
        {
            using var request = Request(target, restore.AttemptId, restore.LeaseToken, "verify", restore.OperationId);
            return await World.Anonymous.SendAsync(request);
        }

        public async Task WaitForNoLeasesAsync(Guid connectionId, string key)
        {
            using var deadline = new CancellationTokenSource(Deadline);
            while (await LeaseCountAsync(connectionId, key) != 0) await Task.Delay(50, deadline.Token);
        }

        /// <summary>Leases on this key (the database is shared with other tests, so the whole table says nothing).</summary>
        public async Task<long> LeaseCountAsync(Guid connectionId, string key)
        {
            await using var db = fixture.CreatePlatformContext();
            var lockId = BackupTransferLock.Id(connectionId, key);
            return await db.Database.SqlQuery<long>($"SELECT count(*) AS \"Value\" FROM backup_transfer_leases WHERE \"LockId\" = {lockId}").SingleAsync();
        }

        /// <summary>A live lease of some other writer on the key.</summary>
        public async Task HoldLeaseAsync(Guid connectionId, string key, TimeSpan ttl)
        {
            await using var db = fixture.CreatePlatformContext();
            var lockId = BackupTransferLock.Id(connectionId, key);
            var seconds = ttl.TotalSeconds;
            await db.Database.ExecuteSqlInterpolatedAsync($"INSERT INTO backup_transfer_leases (\"LockId\",\"OwnerId\",\"ExpiresAt\") VALUES ({lockId}, {Guid.NewGuid()}, clock_timestamp() + make_interval(secs => {seconds}))");
        }

        public async Task TakeOverLeaseAsync(Guid connectionId, string key)
        {
            await using var db = fixture.CreatePlatformContext();
            var lockId = BackupTransferLock.Id(connectionId, key);
            await db.Database.ExecuteSqlInterpolatedAsync($"UPDATE backup_transfer_leases SET \"OwnerId\" = {Guid.NewGuid()} WHERE \"LockId\" = {lockId}");
        }

        public async Task DeleteLeaseAsync(Guid connectionId, string key)
        {
            await using var db = fixture.CreatePlatformContext();
            var lockId = BackupTransferLock.Id(connectionId, key);
            await db.Database.ExecuteSqlInterpolatedAsync($"DELETE FROM backup_transfer_leases WHERE \"LockId\" = {lockId}");
        }

        /// <summary>Sessions of the backend (by application name) that are running a statement or sitting in a transaction right now.</summary>
        public async Task<long> ActiveSessionsAsync()
        {
            await using var connection = new NpgsqlConnection(fixture.ConnectionString);
            await connection.OpenAsync();
            await using var query = new NpgsqlCommand("SELECT count(*) FROM pg_stat_activity WHERE application_name=@name AND state <> 'idle'", connection);
            query.Parameters.AddWithValue("name", ApplicationName);
            return (long)(await query.ExecuteScalarAsync())!;
        }

        public async Task WaitForAdmissionAsync(TenantId tenant)
        {
            using var deadline = new CancellationTokenSource(Deadline);
            var admission = World.Factory.Services.GetRequiredService<AgentTransferAdmission>();
            while (true)
            {
                deadline.Token.ThrowIfCancellationRequested();
                using var permit = admission.TryAcquire(tenant);
                if (permit is { IsAcquired: true }) return;
                await Task.Yield();
            }
        }

        public async Task AssertPoolReturnedAsync()
        {
            var connections = new List<NpgsqlConnection>();
            using var deadline = new CancellationTokenSource(Deadline);
            try
            {
                for (var index = 0; index < new NpgsqlConnectionStringBuilder(connectionString).MaxPoolSize; index++)
                {
                    var connection = new NpgsqlConnection(connectionString);
                    connections.Add(connection);
                    await connection.OpenAsync(deadline.Token);
                    await using var query = new NpgsqlCommand("SELECT NOT EXISTS(SELECT 1 FROM pg_locks WHERE pid=pg_backend_pid() AND locktype='advisory')", connection);
                    Assert.Equal(true, await query.ExecuteScalarAsync(deadline.Token));
                }
            }
            finally
            {
                foreach (var connection in connections) await connection.DisposeAsync();
            }
        }

        public async ValueTask DisposeAsync()
        {
            try
            {
                Provider.ReleaseAll();
                try
                {
                    await World.DisposeAsync();
                }
                finally
                {
                    await CancelWorkAsync();
                }
            }
            finally
            {
                await using var connection = new NpgsqlConnection(connectionString);
                NpgsqlConnection.ClearPool(connection);
            }
        }

        private async Task CancelWorkAsync()
        {
            using var deadline = new CancellationTokenSource(Deadline);
            var now = World.Clock.GetUtcNow();
            var restores = new PostgreSqlRestoreStore(Options);
            foreach (var (tenant, id) in _restores)
            {
                var operation = await restores.RequestCancellationAsync(tenant, id, now, deadline.Token);
                Assert.NotNull(operation);
                if (!operation.IsTerminal)
                {
                    var attempt = operation.CurrentAttempt;
                    Assert.Equal(BackupMutationResult.Accepted, await restores.CompleteAsync(
                        new RestoreLeaseCommand(tenant, operation.AgentId, attempt.Id, attempt.LeaseToken!.Value, now),
                        new RestoreCompletion(RestoreStatus.Cancelled, RestoreFailureCodes.Cancelled, RestoreTargetState.NotModified, null), deadline.Token));
                }
                Assert.True((await restores.GetAsync(tenant, id, deadline.Token))!.IsTerminal);
            }

            var backups = new PostgreSqlBackupRunStore(Options, AdmitAllBackupAdmission.Instance);
            foreach (var (tenant, id) in _runs)
            {
                var run = await backups.RequestCancellationAsync(tenant, id, now, deadline.Token);
                Assert.NotNull(run);
                if (!run.IsTerminal)
                {
                    var attempt = run.CurrentAttempt;
                    Assert.Equal(BackupMutationResult.Accepted, await backups.CompleteAttemptAsync(
                        new BackupLeaseCommand(tenant, run.AgentId, attempt.Id, attempt.LeaseToken!.Value, now),
                        new BackupPlatform.Application.Backups.BackupCompletion("Cancelled", null, null), deadline.Token));
                }
                Assert.True((await backups.GetRunAsync(tenant, id, deadline.Token))!.IsTerminal);
            }
        }
    }

    private sealed class RelayResolver(BlockingProvider provider) : IBackupStorageResolver
    {
        public IStorageProvider Resolve(TenantId tenantId, string storageProvider, string storageTarget) => provider;
        public IReadOnlyList<BackupManagedStorage> ManagedStorages => [];
    }

    private sealed class Gate
    {
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Exited { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Exception? Failure { get; set; }
    }

    private sealed class BlockingProvider : IStorageProvider
    {
        private readonly ConcurrentDictionary<string, Gate> _gates = new();
        private int _calls;
        public int Calls => Volatile.Read(ref _calls);
        public StorageProviderId Id { get; } = new("google-drive");
        public StorageTopology Topology => StorageTopology.BackendRelayed;
        public StorageCapabilities Capabilities { get; } = new([StorageCapabilityIds.Read, StorageCapabilityIds.Delete, StorageCapabilityIds.VerifyAbsence]);
        public Gate Block(string key) => _gates.GetOrAdd(key, _ => new Gate());
        public void ReleaseAll() { foreach (var gate in _gates.Values) gate.Release.TrySetResult(); }

        private async Task WaitAsync(StorageObjectKey key, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _calls);
            if (!_gates.TryGetValue(key.Value, out var gate)) return;
            gate.Entered.TrySetResult();
            try
            {
                await gate.Release.Task.WaitAsync(cancellationToken);
                if (gate.Failure is { } failure) throw failure;
            }
            finally { gate.Exited.TrySetResult(); }
        }

        public async Task<StorageObjectMetadata> PutAsync(StorageObjectKey key, Stream content, StoragePutOptions options, IProgress<long>? progress, CancellationToken cancellationToken)
        {
            await WaitAsync(key, cancellationToken);
            return new(new StorageLocation(Id, key), options.ExpectedLength!.Value, options.ExpectedChecksum, DateTimeOffset.UtcNow);
        }
        public async Task<StorageVerificationResult> VerifyAsync(StorageObjectKey key, StorageVerificationExpectation expected, CancellationToken cancellationToken)
        {
            await WaitAsync(key, cancellationToken);
            return new(new StorageLocation(Id, key), StorageVerificationStatus.Verified, StorageVerificationMethod.ReadBack, expected.SizeBytes, expected.Checksum);
        }
        public async Task<bool> DeleteAsync(StorageObjectKey key, CancellationToken cancellationToken) { await WaitAsync(key, cancellationToken); return true; }
        public Task<bool> ExistsAsync(StorageObjectKey key, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<StorageObjectMetadata?> GetMetadataAsync(StorageObjectKey key, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<Stream> OpenReadAsync(StorageObjectKey key, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult<Stream>(new MemoryStream([1, 2, 3], writable: false));
        }
        public IAsyncEnumerable<StorageEntry> EnumerateAsync(StorageObjectKey? prefix, DateTimeOffset modifiedBefore, CancellationToken cancellationToken) => throw new NotSupportedException();
    }
}
