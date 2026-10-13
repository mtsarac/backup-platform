using BackupPlatform.Application.Backups;
using BackupPlatform.Domain.Backups;
using BackupPlatform.Domain.Storage;
using BackupPlatform.ManagedService.Administration;
using BackupPlatform.ManagedService.Plans;
using BackupPlatform.ManagedService.Quota;

namespace BackupPlatform.IntegrationTests.Quota;

/// <summary>What a tenant is charged for, end to end: real run lifecycle publishes, real saves delete, and every figure is compared with an independent SQL oracle.</summary>
public sealed class UsageAccountingIntegrationTests(PostgreSqlFixture database) : IClassFixture<PostgreSqlFixture>
{
    private const long Gb = QuotaWorld.Gb;
    private static readonly TimeSpan Lease = TimeSpan.FromMinutes(5);
    private static readonly DateTimeOffset Due = QuotaWorld.T0.AddDays(2);
    private readonly QuotaWorld _world = new(database);

    [Fact]
    public async Task OneArtifactOnThreeProvidersCountsOnceLogicallyAndThreeTimesPhysically()
    {
        var agent = await _world.AgentAsync(10 * Gb);
        var job = await _world.JobAsync(agent);
        await _world.PublishCopiesAsync(agent, job, 2 * Gb, UsageTestKit.AllThree);

        await _world.AssertUsedAsync(agent.TenantId, 2 * Gb);
        await _world.AssertOracleAsync(agent.TenantId, 2 * Gb, 6 * Gb);

        var physical = await _world.CreatePolicyAsync(QuotaAccountingMode.PhysicalReplicaBytes, QuotaScopeRule.All);
        await _world.AssignAsync(agent.TenantId, await _world.PlanAsync(10 * Gb, physical.Key));

        // The assignment brings the projection current in its own transaction: there is no instant with the new plan and the old figure.
        var stored = await _world.StoredUsageAsync(agent.TenantId);
        Assert.NotNull(stored);
        Assert.Equal(6 * Gb, stored.UsedBytes);
        Assert.Equal(physical.Id, stored.PolicyId);
        Assert.Equal(QuotaAccountingMode.PhysicalReplicaBytes, stored.AccountingMode);
        await _world.AssertUsedAsync(agent.TenantId, 6 * Gb);
        await _world.AssertOracleAsync(agent.TenantId, 2 * Gb, 6 * Gb);

        await _world.AssignAsync(agent.TenantId, await _world.PlanAsync(10 * Gb));
        await _world.AssertUsedAsync(agent.TenantId, 2 * Gb);
        Assert.Equal(QuotaPolicy.DefaultId, (await _world.StoredUsageAsync(agent.TenantId))!.PolicyId);
    }

    [Fact]
    public async Task ChangingThePolicyOfAPlanRecomputesItsTenantsWithoutAnyCodeChange()
    {
        var (agent, policy) = await _world.TenantOnPolicyAsync(QuotaAccountingMode.LogicalArtifactBytes, QuotaScopeRule.All, 10 * Gb);
        var job = await _world.JobAsync(agent);
        await _world.PublishCopiesAsync(agent, job, 2 * Gb, UsageTestKit.AllThree);
        await _world.PublishCopiesAsync(agent, job, Gb, ReplicaSpec.SftpLike);
        await _world.AssertUsedAsync(agent.TenantId, 3 * Gb);
        var actor = await _world.ActorAsync();

        var change = await _world.PolicyStore().UpdateAsync(policy.Id, new QuotaPolicyValues(
            $"Usage policy {policy.Key}", QuotaAccountingMode.LogicalArtifactBytes, UsageTestKit.Ownership(StorageOwnership.PlatformManaged),
            QuotaEnforcementMode.BlockNewBackups, 80, 90, true), actor, DateTimeOffset.UtcNow, default);

        Assert.Equal(ManagedServiceOutcome.Changed, change.Outcome);
        Assert.Equal(2, change.Value!.Policy.Version);
        Assert.Equal(1, change.Value.TenantsRecalculated);
        // Already recomputed when UpdateAsync returns, before anybody reads: only the artifact with a central replica is platform-managed.
        var stored = await _world.StoredUsageAsync(agent.TenantId);
        Assert.Equal(2, stored!.PolicyVersion);
        Assert.Equal(2 * Gb, stored.UsedBytes);

        var rebuilt = await _world.Usage().RebuildAsync(agent.TenantId, actor, DateTimeOffset.UtcNow, default);
        Assert.Equal(2 * Gb, rebuilt!.UsedBytes);
        Assert.Equal(2, rebuilt.PolicyVersion);
        await _world.AssertUsedAsync(agent.TenantId, 2 * Gb);
        await _world.AssertOracleAsync(agent.TenantId, 2 * Gb, 2 * Gb, "central");
        await _world.AssertOracleAsync(agent.TenantId, 3 * Gb, 7 * Gb);

        var audit = Assert.Single(await _world.AuditAsync("quota_policy.changed", policy.Id.Value.ToString("D")));
        var details = UsageTestKit.DetailsOf(audit);
        Assert.Equal(1, details.GetProperty("previousVersion").GetInt32());
        Assert.Equal(2, details.GetProperty("version").GetInt32());
        Assert.Equal("""[{"kind":"all"}]""", details.GetProperty("previousIncludedStorageScopes").GetString());
        Assert.Equal("""[{"kind":"ownershipType","values":["PlatformManaged"]}]""", details.GetProperty("includedStorageScopes").GetString());
        // The automatic recomputation is not an administrator's action on the tenant.
        Assert.Empty(await _world.AuditOfTenantUsageAsync("tenant_storage_usage.reconcile_corrected", agent.TenantId));
    }

    [Fact]
    public async Task ChangingThePolicyReferenceOfAPlanRecomputesTheTenantsSubscribedToIt()
    {
        var logical = await _world.CreatePolicyAsync(QuotaAccountingMode.LogicalArtifactBytes, QuotaScopeRule.All);
        var physical = await _world.CreatePolicyAsync(QuotaAccountingMode.PhysicalReplicaBytes, QuotaScopeRule.All);
        var plan = new Plan(
            PlanId.New(), $"Reference plan {Guid.NewGuid():N}", 10 * Gb, logical.Key, true, DateTimeOffset.UtcNow);
        var actor = await _world.ActorAsync();
        Assert.Equal(ManagedServiceOutcome.Changed, (await _world.ManagedStore().CreatePlanAsync(plan, actor, DateTimeOffset.UtcNow, default)).Outcome);
        var agent = await _world.AgentAsync(null, null, subscribe: false);
        await _world.AssignAsync(agent.TenantId, plan.Id);
        var job = await _world.JobAsync(agent);
        await _world.PublishCopiesAsync(agent, job, 2 * Gb, UsageTestKit.AllThree);
        await _world.AssertUsedAsync(agent.TenantId, 2 * Gb);

        var update = await _world.ManagedStore().UpdatePlanAsync(plan.Id, plan.Name, 10 * Gb, physical.Key, actor, DateTimeOffset.UtcNow, default);

        Assert.Equal(ManagedServiceOutcome.Changed, update.Outcome);
        var stored = await _world.StoredUsageAsync(agent.TenantId);
        Assert.Equal(physical.Id, stored!.PolicyId);
        Assert.Equal(6 * Gb, stored.UsedBytes);
        await _world.AssertOracleAsync(agent.TenantId, 2 * Gb, 6 * Gb);
    }

    [Theory]
    [InlineData("platform", "all3", QuotaAccountingMode.LogicalArtifactBytes, 2)]
    [InlineData("platform", "all3", QuotaAccountingMode.PhysicalReplicaBytes, 2)]
    [InlineData("tenant", "all3", QuotaAccountingMode.LogicalArtifactBytes, 2)]
    [InlineData("tenant", "all3", QuotaAccountingMode.PhysicalReplicaBytes, 4)]
    [InlineData("providers", "all3", QuotaAccountingMode.LogicalArtifactBytes, 2)]
    [InlineData("providers", "all3", QuotaAccountingMode.PhysicalReplicaBytes, 4)]
    [InlineData("platform", "sftp", QuotaAccountingMode.LogicalArtifactBytes, 0)]
    [InlineData("platform", "sftp", QuotaAccountingMode.PhysicalReplicaBytes, 0)]
    [InlineData("providers", "sftp", QuotaAccountingMode.PhysicalReplicaBytes, 0)]
    [InlineData("tenant", "sftp", QuotaAccountingMode.LogicalArtifactBytes, 2)]
    [InlineData("all", "sftp", QuotaAccountingMode.LogicalArtifactBytes, 2)]
    [InlineData("all", "sftp", QuotaAccountingMode.PhysicalReplicaBytes, 2)]
    [InlineData("all", "all3", QuotaAccountingMode.LogicalArtifactBytes, 2)]
    [InlineData("all", "all3", QuotaAccountingMode.PhysicalReplicaBytes, 6)]
    public async Task OnlyReplicasInTheScopeOfThePolicyAreCounted(string scopeName, string layout, QuotaAccountingMode mode, long expectedGb)
    {
        // The oracle only filters by one provider, so the expectation is rebuilt from per-provider oracle figures of this single artifact.
        var (scope, providers) = scopeName switch
        {
            "all" => (QuotaScopeRule.All, (string[]?)null),
            "platform" => (UsageTestKit.Ownership(StorageOwnership.PlatformManaged), new[] { "central" }),
            "tenant" => (UsageTestKit.Ownership(StorageOwnership.TenantManaged), new[] { "googledrive", "sftp" }),
            _ => (UsageTestKit.Providers("central", "googledrive"), new[] { "central", "googledrive" })
        };
        var replicas = layout == "sftp" ? new[] { ReplicaSpec.SftpLike } : UsageTestKit.AllThree;
        var (agent, _) = await _world.TenantOnPolicyAsync(mode, scope);
        var job = await _world.JobAsync(agent);

        await _world.PublishCopiesAsync(agent, job, 2 * Gb, replicas);

        await _world.AssertUsedAsync(agent.TenantId, expectedGb * Gb);
        long oracle;
        if (providers is null)
        {
            var (logical, physical) = await _world.SourceUsageAsync(agent.TenantId);
            oracle = mode == QuotaAccountingMode.LogicalArtifactBytes ? logical : physical;
        }
        else
        {
            var perProvider = new List<(long Logical, long Physical)>();
            foreach (var provider in providers)
            {
                perProvider.Add(await _world.SourceUsageAsync(agent.TenantId, provider));
            }
            oracle = mode == QuotaAccountingMode.LogicalArtifactBytes ? perProvider.Max(figure => figure.Logical) : perProvider.Sum(figure => figure.Physical);
        }
        Assert.Equal(expectedGb * Gb, oracle);
        await _world.AssertNoDriftAsync(agent.TenantId);
    }

    [Theory]
    [InlineData("platform", QuotaAccountingMode.LogicalArtifactBytes, 0)]
    [InlineData("platform", QuotaAccountingMode.PhysicalReplicaBytes, 0)]
    [InlineData("tenant", QuotaAccountingMode.LogicalArtifactBytes, 1)]
    [InlineData("tenant", QuotaAccountingMode.PhysicalReplicaBytes, 1)]
    [InlineData("providers", QuotaAccountingMode.PhysicalReplicaBytes, 1)]
    public async Task AnAgentLocalReplicaIsTenantManagedStorage(string scopeName, QuotaAccountingMode mode, long expectedGb)
    {
        var scope = scopeName switch
        {
            "platform" => UsageTestKit.Ownership(StorageOwnership.PlatformManaged),
            "tenant" => UsageTestKit.Ownership(StorageOwnership.TenantManaged),
            _ => UsageTestKit.Providers("local")
        };
        var (agent, _) = await _world.TenantOnPolicyAsync(mode, scope);
        var job = await _world.JobAsync(agent, location: BackupStorageExecutionLocation.Agent);

        await _world.PublishCopiesAsync(agent, job, Gb, UsageTestKit.AgentLocal);

        await _world.AssertUsedAsync(agent.TenantId, expectedGb * Gb);
        await _world.AssertOracleAsync(agent.TenantId, Gb, Gb, "local");
        await _world.AssertNoDriftAsync(agent.TenantId);
    }

    [Theory]
    [InlineData(QuotaAccountingMode.LogicalArtifactBytes)]
    [InlineData(QuotaAccountingMode.PhysicalReplicaBytes)]
    public async Task OnlyVerifiedAbsenceLowersUsageAndTheArtifactStopsCountingWithItsLastReplica(QuotaAccountingMode mode)
    {
        var (agent, _) = await _world.TenantOnPolicyAsync(mode, QuotaScopeRule.All);
        var tenant = agent.TenantId;
        var job = await _world.JobAsync(agent);
        var (_, artifactId) = await _world.PublishCopiesAsync(agent, job, 2 * Gb, UsageTestKit.AllThree);

        async Task ExpectAsync(long logicalGb, long physicalGb)
        {
            await _world.AssertUsedAsync(tenant, (mode == QuotaAccountingMode.LogicalArtifactBytes ? logicalGb : physicalGb) * Gb);
            await _world.AssertOracleAsync(tenant, logicalGb * Gb, physicalGb * Gb);
            await _world.AssertNoDriftAsync(tenant);
        }

        await ExpectAsync(2, 6);

        // A soft delete (and asking for it again) keeps every byte.
        await _world.ChangeArtifactAsync(artifactId, artifact => Assert.Equal(BackupArtifactDeleteOutcome.Requested, artifact.RequestDelete(QuotaWorld.T0, null)));
        await ExpectAsync(2, 6);
        await _world.ChangeArtifactAsync(artifactId, artifact => Assert.Equal(BackupArtifactDeleteOutcome.AlreadyInProgress, artifact.RequestDelete(QuotaWorld.T0, null)));
        await ExpectAsync(2, 6);

        var first = Guid.NewGuid();
        await _world.ChangeArtifactAsync(artifactId, artifact => Assert.True(UsageTestKit.ReplicaOf(artifact, "central").BeginDelete(first, "node-a", Due, Lease)));
        await ExpectAsync(2, 6);
        await _world.ChangeArtifactAsync(artifactId, artifact =>
            Assert.True(UsageTestKit.ReplicaOf(artifact, "central").FailDelete(first, Due, BackupDeleteFailureCodes.IoError, Due.AddMinutes(1))));
        await ExpectAsync(2, 6);

        var second = Guid.NewGuid();
        await _world.ChangeArtifactAsync(artifactId, artifact => Assert.True(UsageTestKit.ReplicaOf(artifact, "central").BeginDelete(second, "node-a", Due.AddMinutes(1), Lease)));
        await ExpectAsync(2, 6);
        await _world.ChangeArtifactAsync(artifactId, artifact => Assert.True(UsageTestKit.ReplicaOf(artifact, "central").CompleteDelete(second, Due.AddMinutes(1))));
        // One of three copies is gone: the artifact is still stored, so its logical size stays.
        await ExpectAsync(2, 4);

        await _world.ChangeArtifactAsync(artifactId, artifact => Assert.False(UsageTestKit.ReplicaOf(artifact, "central").CompleteDelete(second, Due.AddMinutes(2))));
        await ExpectAsync(2, 4);

        foreach (var (provider, during, after) in new[] { ("googledrive", (2L, 4L), (2L, 2L)), ("sftp", (2L, 2L), (0L, 0L)) })
        {
            var token = Guid.NewGuid();
            await _world.ChangeArtifactAsync(artifactId, artifact => Assert.True(UsageTestKit.ReplicaOf(artifact, provider).BeginDelete(token, "node-a", Due, Lease)));
            await ExpectAsync(during.Item1, during.Item2);
            await _world.ChangeArtifactAsync(artifactId, artifact => Assert.True(UsageTestKit.ReplicaOf(artifact, provider).CompleteDelete(token, Due)));
            await ExpectAsync(after.Item1, after.Item2);
        }

        await _world.ChangeArtifactAsync(artifactId, artifact => Assert.Equal(BackupArtifactDeleteOutcome.AlreadyDeleted, artifact.RequestDelete(QuotaWorld.T0, null)));
        await ExpectAsync(0, 0);
    }

    [Fact]
    public async Task CancellingASoftDeleteKeepsUsageAndAReplicaAlreadyBeingDeletedIsNotRestored()
    {
        var (agent, _) = await _world.TenantOnPolicyAsync(QuotaAccountingMode.PhysicalReplicaBytes, QuotaScopeRule.All);
        var job = await _world.JobAsync(agent);
        var (_, artifactId) = await _world.PublishCopiesAsync(agent, job, 2 * Gb, ReplicaSpec.Central, ReplicaSpec.SftpLike);

        await _world.ChangeArtifactAsync(artifactId, artifact => artifact.RequestDelete(QuotaWorld.T0, null));
        await _world.AssertUsedAsync(agent.TenantId, 4 * Gb);
        await _world.ChangeArtifactAsync(artifactId, artifact => Assert.Equal(2, artifact.CancelDelete(QuotaWorld.T0)));
        await _world.AssertUsedAsync(agent.TenantId, 4 * Gb);
        await _world.AssertOracleAsync(agent.TenantId, 2 * Gb, 4 * Gb);

        await _world.ChangeArtifactAsync(artifactId, artifact =>
        {
            artifact.RequestDelete(QuotaWorld.T0, null);
            Assert.True(UsageTestKit.ReplicaOf(artifact, "central").BeginDelete(Guid.NewGuid(), "node-a", Due, Lease));
        });
        await _world.ChangeArtifactAsync(artifactId, artifact => Assert.Equal(1, artifact.CancelDelete(Due)));

        await _world.AssertUsedAsync(agent.TenantId, 4 * Gb);
        await _world.AssertOracleAsync(agent.TenantId, 2 * Gb, 4 * Gb);
        await _world.AssertNoDriftAsync(agent.TenantId);
    }

    [Fact]
    public async Task AnAgentLocalReplicaStopsCountingOnlyWhenTheAgentVerifiesAbsence()
    {
        var (agent, _) = await _world.TenantOnPolicyAsync(QuotaAccountingMode.LogicalArtifactBytes, QuotaScopeRule.All);
        var tenant = agent.TenantId;
        var job = await _world.JobAsync(agent, location: BackupStorageExecutionLocation.Agent);
        var (_, artifactId) = await _world.PublishCopiesAsync(agent, job, Gb, UsageTestKit.AgentLocal);

        async Task ExpectAsync(long bytes)
        {
            await _world.AssertUsedAsync(tenant, bytes);
            await _world.AssertOracleAsync(tenant, bytes, bytes, "local");
            await _world.AssertNoDriftAsync(tenant);
        }

        await ExpectAsync(Gb);
        await _world.ChangeArtifactAsync(artifactId, artifact => artifact.RequestDelete(QuotaWorld.T0, null));
        await ExpectAsync(Gb);

        var operation = Guid.NewGuid();
        await _world.ChangeArtifactAsync(artifactId, artifact => Assert.True(UsageTestKit.ReplicaOf(artifact, "local").DispatchAgentDeletion(operation, Due, Due.AddMinutes(1))));
        await ExpectAsync(Gb);

        var firstToken = Guid.NewGuid();
        await _world.ChangeArtifactAsync(artifactId, artifact => Assert.True(UsageTestKit.ReplicaOf(artifact, "local").ClaimAgentDeletion(operation, firstToken, Due, Lease)));
        await ExpectAsync(Gb);
        await _world.ChangeArtifactAsync(artifactId, artifact => Assert.True(
            UsageTestKit.ReplicaOf(artifact, "local").CompleteAgentDeletion(operation, firstToken, Due, false, BackupDeleteFailureCodes.IoError, Due.AddMinutes(1))));
        await ExpectAsync(Gb);

        var retryAt = Due.AddMinutes(1);
        var secondToken = Guid.NewGuid();
        await _world.ChangeArtifactAsync(artifactId, artifact => Assert.True(UsageTestKit.ReplicaOf(artifact, "local").DispatchAgentDeletion(Guid.NewGuid(), retryAt, retryAt.AddMinutes(1))));
        await ExpectAsync(Gb);
        await _world.ChangeArtifactAsync(artifactId, artifact => Assert.True(UsageTestKit.ReplicaOf(artifact, "local").ClaimAgentDeletion(operation, secondToken, retryAt, Lease)));
        await ExpectAsync(Gb);
        await _world.ChangeArtifactAsync(artifactId, artifact => Assert.True(
            UsageTestKit.ReplicaOf(artifact, "local").CompleteAgentDeletion(operation, secondToken, retryAt, true, null, null)));

        await ExpectAsync(0);
    }

    [Fact]
    public async Task APublishedArtifactIsInTheProjectionTheMomentItsCompletionIsAccepted()
    {
        var agent = await _world.AgentAsync(null);
        var job = await _world.JobAsync(agent);
        Assert.Equal(0, (await _world.StoredUsageAsync(agent.TenantId))!.UsedBytes);

        await _world.PublishCopiesAsync(agent, job, 2 * Gb, UsageTestKit.AllThree);
        var stored = await _world.StoredUsageAsync(agent.TenantId);
        Assert.NotNull(stored);
        Assert.Equal(2 * Gb, stored.UsedBytes);
        Assert.Equal(0, stored.ReservedBytes);

        await _world.PublishCopiesAsync(agent, job, 3 * Gb, ReplicaSpec.Central);
        Assert.Equal(5 * Gb, (await _world.StoredUsageAsync(agent.TenantId))!.UsedBytes);
        await _world.AssertOracleAsync(agent.TenantId, 5 * Gb, 9 * Gb);
        await _world.AssertNoDriftAsync(agent.TenantId);
    }

    [Fact]
    public async Task ADuplicateCompletionCallbackNeverCountsTheArtifactTwice()
    {
        var agent = await _world.AgentAsync(10 * Gb);
        var job = await _world.JobAsync(agent);
        var store = _world.RunStore(AdmitAllBackupAdmission.Instance);
        var started = await store.StartRunAsync(agent.TenantId, job.Id, $"dup-{Guid.NewGuid():N}", QuotaWorld.T0, default);
        Assert.Equal(BackupStartOutcome.Started, started.Outcome);
        await _world.Harness.SnapshotDestinationsAsync(started.Run!.Id, [.. UsageTestKit.AllThree.Select(replica => replica.Provider)]);
        var claim = await BackupStoreHarness.ClaimAsync(store, agent, QuotaWorld.T0);
        await BackupStoreHarness.StartAttemptAsync(store, claim!, QuotaWorld.T0);
        var completion = QuotaWorld.Success(2 * Gb, UsageTestKit.AllThree, QuotaWorld.T0);
        var command = BackupStoreHarness.Command(claim!, QuotaWorld.T0);

        Assert.Equal(BackupMutationResult.Accepted, await store.CompleteAttemptAsync(command, completion, default));
        await _world.AssertUsedAsync(agent.TenantId, 2 * Gb);

        Assert.Equal(BackupMutationResult.Accepted, await store.CompleteAttemptAsync(command, completion, default));
        await _world.AssertUsedAsync(agent.TenantId, 2 * Gb);
        Assert.Equal(1, await _world.Harness.CountArtifactsAsync(started.Run!.Id));

        // A different result for an already finished attempt is refused and changes nothing.
        Assert.Equal(BackupMutationResult.Conflict,
            await store.CompleteAttemptAsync(command, QuotaWorld.Success(5 * Gb, UsageTestKit.AllThree, QuotaWorld.T0), default));
        await _world.AssertUsedAsync(agent.TenantId, 2 * Gb);
        await _world.AssertOracleAsync(agent.TenantId, 2 * Gb, 6 * Gb);
    }

    [Fact]
    public async Task OneTenantsPublishesAndDeletesNeverChangeAnotherTenantsUsage()
    {
        var first = await _world.AgentAsync(10 * Gb);
        var second = await _world.AgentAsync(10 * Gb);
        var firstJob = await _world.JobAsync(first);
        var secondJob = await _world.JobAsync(second);

        var (_, firstArtifact) = await _world.PublishCopiesAsync(first, firstJob, 2 * Gb, UsageTestKit.AllThree);
        await _world.AssertUsedAsync(first.TenantId, 2 * Gb);
        Assert.Equal(0, (await _world.Usage().GetUsageAsync(second.TenantId, default))!.UsedBytes);

        await _world.PublishCopiesAsync(second, secondJob, 3 * Gb, ReplicaSpec.Central);
        await _world.AssertUsedAsync(first.TenantId, 2 * Gb);
        await _world.AssertUsedAsync(second.TenantId, 3 * Gb);

        await _world.ChangeArtifactAsync(firstArtifact, artifact =>
        {
            artifact.RequestDelete(QuotaWorld.T0, null);
            foreach (var replica in artifact.Replicas)
            {
                var token = Guid.NewGuid();
                Assert.True(replica.BeginDelete(token, "node-a", Due, Lease));
                Assert.True(replica.CompleteDelete(token, Due));
            }
        });

        await _world.AssertUsedAsync(first.TenantId, 0);
        await _world.AssertUsedAsync(second.TenantId, 3 * Gb);
        await _world.AssertOracleAsync(first.TenantId, 0, 0);
        await _world.AssertOracleAsync(second.TenantId, 3 * Gb, 3 * Gb);
    }

    [Theory]
    [InlineData(QuotaAccountingMode.LogicalArtifactBytes, 6)]
    [InlineData(QuotaAccountingMode.PhysicalReplicaBytes, 12)]
    public async Task ConcurrentPublishesOfOneTenantLeaveTheProjectionEqualToTheSources(QuotaAccountingMode mode, long expectedGb)
    {
        var (first, _) = await _world.TenantOnPolicyAsync(mode, QuotaScopeRule.All);
        var agents = new List<StoreAgent> { first };
        for (var index = 1; index < 6; index++)
        {
            agents.Add(new StoreAgent(first.TenantId, await _world.Harness.AddAgentToAsync(first.TenantId)));
        }
        var jobs = new List<BackupJob>();
        foreach (var agent in agents)
        {
            jobs.Add(await _world.JobAsync(agent));
        }

        await Task.WhenAll(agents.Select((agent, index) =>
            _world.PublishAsync(_world.RunStore(), agent, jobs[index], Gb, [ReplicaSpec.Central, ReplicaSpec.SftpLike], QuotaWorld.T0)));

        await _world.AssertUsedAsync(first.TenantId, expectedGb * Gb);
        await _world.AssertOracleAsync(first.TenantId, 6 * Gb, 12 * Gb);
        await _world.AssertNoDriftAsync(first.TenantId);
    }

    [Fact]
    public async Task ConcurrentDeletionsOfTheReplicasOfOneArtifactRemoveTheArtifactExactlyOnce()
    {
        var (agent, _) = await _world.TenantOnPolicyAsync(QuotaAccountingMode.LogicalArtifactBytes, QuotaScopeRule.All);
        var job = await _world.JobAsync(agent);
        var (_, artifactId) = await _world.PublishCopiesAsync(agent, job, 2 * Gb, UsageTestKit.AllThree);
        await _world.PublishCopiesAsync(agent, job, Gb, ReplicaSpec.Central);
        var tokens = UsageTestKit.AllThree.ToDictionary(spec => spec.Provider, _ => Guid.NewGuid());
        await _world.ChangeArtifactAsync(artifactId, artifact =>
        {
            artifact.RequestDelete(QuotaWorld.T0, null);
            foreach (var replica in artifact.Replicas)
            {
                Assert.True(replica.BeginDelete(tokens[replica.StorageProvider], "node-a", Due, Lease));
            }
        });
        await _world.AssertUsedAsync(agent.TenantId, 3 * Gb);

        await Task.WhenAll(tokens.Select(pair => _world.ChangeArtifactAsync(artifactId, artifact =>
            Assert.True(UsageTestKit.ReplicaOf(artifact, pair.Key).CompleteDelete(pair.Value, Due)))));

        // The other artifact still counts; the deleted one left once, however the three completions interleaved.
        await _world.AssertUsedAsync(agent.TenantId, Gb);
        await _world.AssertOracleAsync(agent.TenantId, Gb, Gb);
        await _world.AssertNoDriftAsync(agent.TenantId);
    }
}
