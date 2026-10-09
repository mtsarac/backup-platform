using System.Text.Json;
using BackupPlatform.Application.Backups;
using BackupPlatform.Domain.Backups;
using BackupPlatform.Domain.Identity;
using BackupPlatform.ManagedService.Quota;
using Microsoft.EntityFrameworkCore;

namespace BackupPlatform.IntegrationTests.Quota;

/// <summary>The projection is a read model: it is repaired from the sources, recomputed when missing or stale, and administrators' repairs are audited.</summary>
public sealed class UsageReconcileRebuildIntegrationTests(PostgreSqlFixture database) : IClassFixture<PostgreSqlFixture>
{
    private const long Gb = QuotaWorld.Gb;
    private const string Corrected = "tenant_storage_usage.reconcile_corrected";
    private const string Rebuilt = "tenant_storage_usage.rebuilt";
    private static readonly DateTimeOffset Later = QuotaWorld.T0.AddDays(1);
    private readonly QuotaWorld _world = new(database);

    /// <summary>A tenant on a 10 GB plan holding two artifacts (2 GB and 3 GB) on central storage.</summary>
    private async Task<(StoreAgent Agent, BackupJob Job)> SeedFiveGbAsync()
    {
        var agent = await _world.AgentAsync(10 * Gb);
        var job = await _world.JobAsync(agent);
        await _world.PublishCopiesAsync(agent, job, 2 * Gb, ReplicaSpec.Central);
        await _world.PublishCopiesAsync(agent, job, 3 * Gb, ReplicaSpec.Central);
        await _world.AssertUsedAsync(agent.TenantId, 5 * Gb);
        return (agent, job);
    }

    [Fact]
    public async Task ReconcileCorrectsDriftAuditsItForAnAdministratorAndIsANoOpWhenRepeated()
    {
        var (agent, _) = await SeedFiveGbAsync();
        var tenant = agent.TenantId;
        var actor = await _world.ActorAsync();
        await _world.CorruptUsageAsync(tenant, 2 * Gb, 0);

        var result = await _world.Usage().ReconcileAsync(tenant, actor, Later, default);

        Assert.NotNull(result);
        Assert.True(result.Drifted);
        Assert.Equal(2 * Gb, result.PreviousUsedBytes);
        Assert.Equal(0, result.PreviousReservedBytes);
        Assert.Equal(5 * Gb, result.UsedBytes);
        Assert.Equal(0, result.ReservedBytes);
        Assert.Equal(QuotaPolicy.DefaultId, result.PolicyId);
        Assert.Equal(1, result.PolicyVersion);
        var stored = await _world.StoredUsageAsync(tenant);
        Assert.Equal(5 * Gb, stored!.UsedBytes);
        Assert.Equal(Later, stored.CalculatedAt);
        await _world.AssertOracleAsync(tenant, 5 * Gb, 5 * Gb);
        var audit = Assert.Single(await _world.AuditOfTenantUsageAsync(Corrected, tenant));
        Assert.Equal(tenant, audit.TenantId);
        var details = UsageTestKit.DetailsOf(audit);
        Assert.Equal(2 * Gb, details.GetProperty("previousUsedBytes").GetInt64());
        Assert.Equal(5 * Gb, details.GetProperty("usedBytes").GetInt64());
        Assert.Equal("default", details.GetProperty("policyKey").GetString());

        var again = await _world.Usage().ReconcileAsync(tenant, actor, Later.AddHours(1), default);

        Assert.False(again!.Drifted);
        Assert.Equal(5 * Gb, again.PreviousUsedBytes);
        Assert.Single(await _world.AuditOfTenantUsageAsync(Corrected, tenant));
        var verified = await _world.StoredUsageAsync(tenant);
        Assert.Equal(stored.Revision, verified!.Revision);
        Assert.Equal(Later.AddHours(1), verified.CalculatedAt);
        Assert.Equal(5 * Gb, verified.UsedBytes);
    }

    [Fact]
    public async Task TheAutomaticReconcileCorrectsDriftWithoutAnAdministratorAuditEntry()
    {
        var (agent, _) = await SeedFiveGbAsync();
        var tenant = agent.TenantId;
        await _world.CorruptUsageAsync(tenant, Gb, 7 * Gb);

        var result = await _world.Usage().ReconcileAsync(tenant, null, Later, default);

        Assert.True(result!.Drifted);
        Assert.Equal(Gb, result.PreviousUsedBytes);
        Assert.Equal(7 * Gb, result.PreviousReservedBytes);
        var stored = await _world.StoredUsageAsync(tenant);
        Assert.Equal(5 * Gb, stored!.UsedBytes);
        // ReservedBytes is the sum of unreleased reservations, and there are none.
        Assert.Equal(0, stored.ReservedBytes);
        Assert.Empty(await _world.AuditOfTenantUsageAsync(Corrected, tenant));
        Assert.Empty(await _world.AuditOfTenantUsageAsync(Rebuilt, tenant));
    }

    [Fact]
    public async Task ReconcileRecomputesReservedBytesFromTheUnreleasedReservations()
    {
        var agent = await _world.AgentAsync(10 * Gb);
        var job = await _world.JobAsync(agent);
        var tenant = agent.TenantId;
        await _world.PublishCopiesAsync(agent, job, 4 * Gb, ReplicaSpec.Central);
        var started = await _world.RunStore().StartRunAsync(tenant, job.Id, $"reserve-{Guid.NewGuid():N}", QuotaWorld.T0, default);
        Assert.Equal(BackupStartOutcome.Started, started.Outcome);
        // The same job's previous artifact (4 GB) is what the admitted run is expected to add.
        Assert.Equal(4 * Gb, (await _world.StoredUsageAsync(tenant))!.ReservedBytes);
        await _world.CorruptUsageAsync(tenant, 0, 0);

        var result = await _world.Usage().ReconcileAsync(tenant, await _world.ActorAsync(), QuotaWorld.T0.AddMinutes(1), default);

        Assert.True(result!.Drifted);
        Assert.Equal(4 * Gb, result.UsedBytes);
        Assert.Equal(4 * Gb, result.ReservedBytes);
        var stored = await _world.StoredUsageAsync(tenant);
        Assert.Equal(4 * Gb, stored!.UsedBytes);
        Assert.Equal(4 * Gb, stored.ReservedBytes);
    }

    [Fact]
    public async Task RebuildAlwaysRewritesAndAuditsEvenWhenNothingDrifted()
    {
        var (agent, _) = await SeedFiveGbAsync();
        var tenant = agent.TenantId;
        var actor = await _world.ActorAsync();
        var revision = (await _world.StoredUsageAsync(tenant))!.Revision;

        var first = await _world.Usage().RebuildAsync(tenant, actor, Later, default);

        Assert.False(first!.Drifted);
        Assert.Equal(5 * Gb, first.UsedBytes);
        var afterFirst = await _world.StoredUsageAsync(tenant);
        Assert.Equal(revision + 1, afterFirst!.Revision);
        Assert.Equal(Later, afterFirst.CalculatedAt);
        var audit = Assert.Single(await _world.AuditOfTenantUsageAsync(Rebuilt, tenant));
        Assert.Equal(5 * Gb, UsageTestKit.DetailsOf(audit).GetProperty("previousUsedBytes").GetInt64());
        Assert.Equal(5 * Gb, UsageTestKit.DetailsOf(audit).GetProperty("usedBytes").GetInt64());

        await _world.Usage().RebuildAsync(tenant, actor, Later.AddHours(1), default);

        Assert.Equal(revision + 2, (await _world.StoredUsageAsync(tenant))!.Revision);
        Assert.Equal(2, (await _world.AuditOfTenantUsageAsync(Rebuilt, tenant)).Count);
        Assert.Empty(await _world.AuditOfTenantUsageAsync(Corrected, tenant));
    }

    [Fact]
    public async Task RebuildOfADriftedProjectionReportsTheDriftAndAuditsOnlyTheRebuild()
    {
        var (agent, _) = await SeedFiveGbAsync();
        var tenant = agent.TenantId;
        await _world.CorruptUsageAsync(tenant, Gb, 0);

        var result = await _world.Usage().RebuildAsync(tenant, await _world.ActorAsync(), Later, default);

        Assert.True(result!.Drifted);
        Assert.Equal(Gb, result.PreviousUsedBytes);
        Assert.Equal(5 * Gb, result.UsedBytes);
        Assert.Equal(5 * Gb, (await _world.StoredUsageAsync(tenant))!.UsedBytes);
        Assert.Single(await _world.AuditOfTenantUsageAsync(Rebuilt, tenant));
        Assert.Empty(await _world.AuditOfTenantUsageAsync(Corrected, tenant));
    }

    [Fact]
    public async Task ReconcileAndRebuildOfAMissingProjectionComputeItAndReportNoPreviousFigures()
    {
        var (agent, _) = await SeedFiveGbAsync();
        var tenant = agent.TenantId;
        var actor = await _world.ActorAsync();
        await _world.DeleteProjectionAsync(tenant);

        var reconciled = await _world.Usage().ReconcileAsync(tenant, actor, Later, default);

        Assert.True(reconciled!.Drifted);
        Assert.Null(reconciled.PreviousUsedBytes);
        Assert.Null(reconciled.PreviousReservedBytes);
        Assert.Null(reconciled.PreviousPolicyVersion);
        Assert.Equal(5 * Gb, (await _world.StoredUsageAsync(tenant))!.UsedBytes);
        // There was nothing stored to correct, so there is nothing to audit as a correction.
        Assert.Empty(await _world.AuditOfTenantUsageAsync(Corrected, tenant));

        await _world.DeleteProjectionAsync(tenant);
        var rebuilt = await _world.Usage().RebuildAsync(tenant, actor, Later.AddHours(1), default);

        Assert.True(rebuilt!.Drifted);
        Assert.Equal(5 * Gb, (await _world.StoredUsageAsync(tenant))!.UsedBytes);
        var audit = Assert.Single(await _world.AuditOfTenantUsageAsync(Rebuilt, tenant));
        var details = UsageTestKit.DetailsOf(audit);
        Assert.True(!details.TryGetProperty("previousUsedBytes", out var previous) || previous.ValueKind == JsonValueKind.Null);
    }

    [Fact]
    public async Task AnUnknownTenantHasNoUsageAndNothingToReconcileOrRebuild()
    {
        var unknown = TenantId.New();
        var actor = await _world.ActorAsync();

        Assert.Null(await _world.Usage().GetUsageAsync(unknown, default));
        Assert.Null(await _world.Usage().GetQuotaStatusAsync(unknown, DateTimeOffset.UtcNow, default));
        Assert.Null(await _world.Usage().ReconcileAsync(unknown, actor, Later, default));
        Assert.Null(await _world.Usage().ReconcileAsync(unknown, null, Later, default));
        Assert.Null(await _world.Usage().RebuildAsync(unknown, actor, Later, default));
        Assert.Null(await _world.StoredUsageAsync(unknown));
        Assert.Empty(await _world.AuditOfTenantUsageAsync(Rebuilt, unknown));
    }

    [Theory]
    [InlineData(1_000_000_000L, QuotaState.Normal)]
    [InlineData(3_900_000_000L, QuotaState.Normal)]
    [InlineData(4_000_000_000L, QuotaState.Warning)]
    [InlineData(4_500_000_000L, QuotaState.Critical)]
    [InlineData(5_000_000_000L, QuotaState.OverQuota)]
    public async Task AMissingProjectionIsComputedOnFirstReadAndTheStateFollowsTheFigure(long sizeBytes, QuotaState expectedState)
    {
        var agent = await _world.AgentAsync(5 * Gb);
        var job = await _world.JobAsync(agent);
        await _world.PublishCopiesAsync(agent, job, sizeBytes, ReplicaSpec.Central);
        await _world.DeleteProjectionAsync(agent.TenantId);
        Assert.Null(await _world.StoredUsageAsync(agent.TenantId));

        var status = await _world.Usage().GetQuotaStatusAsync(agent.TenantId, DateTimeOffset.UtcNow, default);

        Assert.NotNull(status);
        Assert.Equal(sizeBytes, status.UsedBytes);
        Assert.Equal(0, status.ReservedBytes);
        Assert.Equal(5 * Gb, status.LimitBytes);
        Assert.Equal(5 * Gb - sizeBytes, status.RemainingBytes);
        Assert.Equal(expectedState, status.State);
        Assert.Equal(expectedState != QuotaState.OverQuota, status.NewBackupsAllowed);
        Assert.Equal(expectedState == QuotaState.OverQuota ? "quota_exceeded" : null, status.DenialCode);
        Assert.Equal("default", status.PolicyKey);
        var stored = await _world.StoredUsageAsync(agent.TenantId);
        Assert.Equal(sizeBytes, stored!.UsedBytes);
        await _world.AssertOracleAsync(agent.TenantId, sizeBytes, sizeBytes);
    }

    [Fact]
    public async Task AMissingProjectionIsComputedByTheUsageReadToo()
    {
        var (agent, _) = await SeedFiveGbAsync();
        await _world.DeleteProjectionAsync(agent.TenantId);

        var view = await _world.Usage().GetUsageAsync(agent.TenantId, default);

        Assert.Equal(5 * Gb, view!.UsedBytes);
        Assert.Equal("default", view.PolicyKey);
        Assert.NotNull(await _world.StoredUsageAsync(agent.TenantId));
    }

    [Fact]
    public async Task AProjectionRecordedUnderAnotherPolicyVersionIsRecomputedInsteadOfTrusted()
    {
        var (agent, _) = await SeedFiveGbAsync();
        await using (var context = _world.Database.CreatePlatformContext())
        {
            await context.Database.ExecuteSqlInterpolatedAsync(
                $"UPDATE tenant_storage_usage SET \"PolicyVersion\" = 99, \"UsedBytes\" = 1 WHERE \"TenantId\" = {agent.TenantId.Value}");
        }

        var view = await _world.Usage().GetUsageAsync(agent.TenantId, default);

        Assert.Equal(5 * Gb, view!.UsedBytes);
        Assert.Equal(1, view.PolicyVersion);
        Assert.Equal(1, (await _world.StoredUsageAsync(agent.TenantId))!.PolicyVersion);
    }

    [Fact]
    public async Task APolicyChangedBehindTheProjectionIsNoticedByTheNextReadAndByTheNextPublish()
    {
        var (agent, policy) = await _world.TenantOnPolicyAsync(QuotaAccountingMode.LogicalArtifactBytes, QuotaScopeRule.All);
        var job = await _world.JobAsync(agent);
        await _world.PublishCopiesAsync(agent, job, 2 * Gb, UsageTestKit.AllThree);
        await _world.AssertUsedAsync(agent.TenantId, 2 * Gb);

        // Another node changed the policy; this projection was computed under version 1 and nobody recomputed it yet.
        await using (var context = _world.Database.CreatePlatformContext())
        {
            await context.Database.ExecuteSqlInterpolatedAsync(
                $"UPDATE quota_policies SET \"Version\" = 2, \"AccountingMode\" = {(int)QuotaAccountingMode.PhysicalReplicaBytes} WHERE \"Key\" = {policy.Key}");
        }
        Assert.Equal(2 * Gb, (await _world.StoredUsageAsync(agent.TenantId))!.UsedBytes);

        // Publishing first recomputes the stale figure from the stored artifacts, then adds the new one: 6 GB + 2 replicas of 1 GB.
        await _world.PublishCopiesAsync(agent, job, Gb, ReplicaSpec.Central, ReplicaSpec.SftpLike);

        var stored = await _world.StoredUsageAsync(agent.TenantId);
        Assert.Equal(8 * Gb, stored!.UsedBytes);
        Assert.Equal(2, stored.PolicyVersion);
        Assert.Equal(QuotaAccountingMode.PhysicalReplicaBytes, stored.AccountingMode);
        await _world.AssertOracleAsync(agent.TenantId, 3 * Gb, 8 * Gb);
        await _world.AssertNoDriftAsync(agent.TenantId);
    }
}

/// <summary>The maintenance pass, on a database of its own: it looks at every tenant, so no other test's rows may be around to be counted.</summary>
public sealed class UsageMaintenanceIntegrationTests(PostgreSqlFixture database) : IClassFixture<PostgreSqlFixture>
{
    private const long Gb = QuotaWorld.Gb;

    private static QuotaWorld World(PostgreSqlFixture database, TimeSpan reconcileAfter) =>
        new(database) { QuotaOptions = new QuotaOptions { ReconcileAfter = reconcileAfter, MaintenanceBatchSize = 1000 } };

    private static async Task<StoreAgent> SeedAsync(QuotaWorld world)
    {
        var agent = await world.AgentAsync(10 * Gb);
        var job = await world.JobAsync(agent);
        await world.PublishCopiesAsync(agent, job, 2 * Gb, ReplicaSpec.Central);
        await world.PublishCopiesAsync(agent, job, 3 * Gb, ReplicaSpec.Central);
        return agent;
    }

    [Fact]
    public async Task MaintenanceComputesTheProjectionOfATenantThatHasNone()
    {
        var world = World(database, TimeSpan.FromHours(1));
        var agent = await SeedAsync(world);
        await world.DeleteProjectionAsync(agent.TenantId);

        var report = await world.Usage().RunMaintenanceAsync(DateTimeOffset.UtcNow.AddMinutes(1), default);

        Assert.Empty(report.Errors);
        Assert.True(report.ProjectionsInitialized >= 1);
        var stored = await world.StoredUsageAsync(agent.TenantId);
        Assert.NotNull(stored);
        Assert.Equal(5 * Gb, stored.UsedBytes);
        await world.AssertOracleAsync(agent.TenantId, 5 * Gb, 5 * Gb);
    }

    [Fact]
    public async Task MaintenanceCorrectsDriftOfAnOldProjectionWithoutAuditingItAsAnAdministratorAction()
    {
        var world = World(database, TimeSpan.Zero);
        var agent = await SeedAsync(world);
        await world.CorruptUsageAsync(agent.TenantId, Gb, 3 * Gb);

        var report = await world.Usage().RunMaintenanceAsync(DateTimeOffset.UtcNow.AddMinutes(1), default);

        Assert.Empty(report.Errors);
        Assert.True(report.ProjectionsReconciled >= 1);
        Assert.True(report.DriftCorrected >= 1);
        var stored = await world.StoredUsageAsync(agent.TenantId);
        Assert.Equal(5 * Gb, stored!.UsedBytes);
        Assert.Equal(0, stored.ReservedBytes);
        Assert.Empty(await world.AuditOfTenantUsageAsync("tenant_storage_usage.reconcile_corrected", agent.TenantId));
    }

    [Fact]
    public async Task MaintenanceLeavesAProjectionAloneUntilReconcileAfterHasElapsed()
    {
        var world = World(database, TimeSpan.FromHours(1));
        var agent = await SeedAsync(world);
        await world.CorruptUsageAsync(agent.TenantId, Gb, 0);
        var calculatedAt = (await world.StoredUsageAsync(agent.TenantId))!.CalculatedAt;

        await world.Usage().RunMaintenanceAsync(calculatedAt.AddMinutes(10), default);

        Assert.Equal(Gb, (await world.StoredUsageAsync(agent.TenantId))!.UsedBytes);

        var report = await world.Usage().RunMaintenanceAsync(calculatedAt.AddHours(2), default);

        Assert.Empty(report.Errors);
        Assert.Equal(5 * Gb, (await world.StoredUsageAsync(agent.TenantId))!.UsedBytes);
    }
}
