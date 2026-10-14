using BackupPlatform.Agents.Capabilities;
using BackupPlatform.Application.Backups;
using BackupPlatform.Contracts.Backups;
using BackupPlatform.Domain.Audit;
using BackupPlatform.Domain.Backups;
using BackupPlatform.Domain.Capabilities;
using BackupPlatform.Domain.Identity;
using BackupPlatform.Infrastructure.Backups;
using Microsoft.EntityFrameworkCore;
using static BackupPlatform.IntegrationTests.BackupStoreHarness;

namespace BackupPlatform.IntegrationTests;

/// <summary>Limits are tenant-scoped rows and a tenant's total is split among its live uploads in the database, so any backend node agrees.</summary>
public sealed class UploadBandwidthStoreIntegrationTests(PostgreSqlFixture fixture) : IClassFixture<PostgreSqlFixture>
{
    private const long Limit = 1_000_000;
    private static readonly TimeSpan Ttl = TimeSpan.FromSeconds(10);
    private readonly BackupStoreHarness _kit = new(fixture);
    private PostgreSqlUploadBandwidthStore Bandwidth => new(fixture.Options);

    private async Task<StoreAgent> AddPacingAgentAsync(TenantId tenant)
    {
        var agent = new StoreAgent(tenant, await _kit.AddAgentToAsync(tenant));
        await using var context = fixture.CreatePlatformContext();
        context.AgentCapabilities.Add(new AgentCapabilityRecord(agent.AgentId, tenant, BackupTaskProtocol.UploadBandwidthLimitCapability, CapabilitySupport.Supported, null, "fixture", T0));
        await context.SaveChangesAsync();
        return agent;
    }

    /// <summary>A running attempt of a new job on a new agent of the tenant.</summary>
    private async Task<(BackupClaim Claim, StoreAgent Agent, BackupJob Job)> RunningAsync(TenantId tenant, DateTimeOffset at)
    {
        var agent = await AddPacingAgentAsync(tenant);
        var job = await _kit.AddJobAsync(agent);
        var store = _kit.Store(Limits());
        await _kit.StartRunAsync(store, job, Guid.NewGuid().ToString(), at);
        var claim = (await ClaimAsync(store, agent, at))!;
        await StartAttemptAsync(store, claim, at);
        return (claim, agent, job);
    }

    private async Task<long> GrantedAsync(BackupClaim claim, DateTimeOffset at) =>
        (await Bandwidth.GrantAsync(Command(claim, at), false, Ttl, default))!.TenantShare!.Value;

    private async Task<long> TotalGrantedAsync(TenantId tenant)
    {
        await using var context = fixture.CreatePlatformContext();
        return await context.UploadBandwidthGrants.Where(grant => grant.TenantId == tenant).SumAsync(grant => grant.BytesPerSecond);
    }

    private Task<bool> SetAsync(TenantId tenant, UploadBandwidthScope scope, Guid id, long? bytesPerSecond) =>
        Bandwidth.SetAsync(tenant, scope, id, bytesPerSecond, UserId.New(), T0, default);

    [Fact]
    public async Task Limits_are_set_changed_removed_and_audited_per_tenant()
    {
        var one = await _kit.AddAgentAsync();
        var other = await _kit.AddAgentAsync();
        var job = await _kit.AddJobAsync(one);

        Assert.True(await SetAsync(one.TenantId, UploadBandwidthScope.Tenant, one.TenantId.Value, 5_000_000));
        Assert.True(await SetAsync(one.TenantId, UploadBandwidthScope.Agent, one.AgentId.Value, 2_000_000));
        Assert.True(await SetAsync(one.TenantId, UploadBandwidthScope.Job, job.Id, 1_000_000));
        Assert.True(await SetAsync(one.TenantId, UploadBandwidthScope.Job, job.Id, 1_000_000));
        Assert.True(await SetAsync(one.TenantId, UploadBandwidthScope.Job, job.Id, 1_500_000));

        var settings = await Bandwidth.GetAsync(one.TenantId, default);
        Assert.Equal(5_000_000, settings.Tenant);
        Assert.Equal(2_000_000, settings.Agents[one.AgentId.Value]);
        Assert.Equal(1_500_000, settings.Jobs[job.Id]);
        var foreign = await Bandwidth.GetAsync(other.TenantId, default);
        Assert.Null(foreign.Tenant);
        Assert.Empty(foreign.Agents);
        Assert.Empty(foreign.Jobs);

        Assert.True(await SetAsync(one.TenantId, UploadBandwidthScope.Job, job.Id, null));
        Assert.True(await SetAsync(one.TenantId, UploadBandwidthScope.Job, job.Id, null));
        Assert.Empty((await Bandwidth.GetAsync(one.TenantId, default)).Jobs);

        await using var context = fixture.CreatePlatformContext();
        var events = await context.AuditEvents.Where(audit => audit.TenantId == one.TenantId && audit.Action == AuditActions.UploadBandwidthLimitChanged).ToListAsync();
        // tenant set, agent set, job set, job changed, job removed: the repeated identical set and the repeated removal changed nothing and are not events.
        Assert.Equal(5, events.Count);
        var changed = events.Select(audit => (audit.TargetType, Details: System.Text.Json.JsonDocument.Parse(audit.DetailsJson).RootElement))
            .Single(audit => audit.TargetType == "backup_job" && audit.Details.GetProperty("bytesPerSecond").ValueKind == System.Text.Json.JsonValueKind.Number
                && audit.Details.GetProperty("previousBytesPerSecond").ValueKind == System.Text.Json.JsonValueKind.Number);
        Assert.Equal(1_000_000, changed.Details.GetProperty("previousBytesPerSecond").GetInt64());
        Assert.Equal(1_500_000, changed.Details.GetProperty("bytesPerSecond").GetInt64());
        Assert.Contains(events, audit => audit.TargetType == "backup_job" && System.Text.Json.JsonDocument.Parse(audit.DetailsJson).RootElement.GetProperty("bytesPerSecond").ValueKind == System.Text.Json.JsonValueKind.Null);
        Assert.All(events, audit => Assert.Equal(AuditActorKind.TenantAdministrator, audit.ActorKind));
    }

    [Fact]
    public async Task An_agent_or_job_of_another_tenant_is_not_found_and_nothing_is_written()
    {
        var mine = await _kit.AddAgentAsync();
        var theirs = await _kit.AddAgentAsync();
        var theirJob = await _kit.AddJobAsync(theirs);

        Assert.False(await SetAsync(mine.TenantId, UploadBandwidthScope.Agent, theirs.AgentId.Value, 2_000_000));
        Assert.False(await SetAsync(mine.TenantId, UploadBandwidthScope.Job, theirJob.Id, 2_000_000));
        Assert.False(await SetAsync(mine.TenantId, UploadBandwidthScope.Agent, Guid.NewGuid(), 2_000_000));
        Assert.False(await SetAsync(mine.TenantId, UploadBandwidthScope.Tenant, theirs.TenantId.Value, 2_000_000));

        await using var context = fixture.CreatePlatformContext();
        Assert.False(await context.UploadBandwidthLimits.AnyAsync(limit => limit.TenantId == mine.TenantId || limit.TenantId == theirs.TenantId));
        Assert.False(await context.AuditEvents.AnyAsync(audit => audit.Action == AuditActions.UploadBandwidthLimitChanged && (audit.TenantId == mine.TenantId || audit.TenantId == theirs.TenantId)));
    }

    [Fact]
    public async Task Every_ceiling_that_applies_to_an_attempt_is_reported_and_unlimited_means_none()
    {
        var tenant = await _kit.AddTenantAsync();
        var (claim, agent, job) = await RunningAsync(tenant, T0);
        var (otherClaim, _, otherJob) = await RunningAsync(tenant, T0);
        await SetAsync(tenant, UploadBandwidthScope.Agent, agent.AgentId.Value, 2_000_000);
        await SetAsync(tenant, UploadBandwidthScope.Job, job.Id, 1_000_000);
        await SetAsync(tenant, UploadBandwidthScope.Job, otherJob.Id, 9_000_000);

        var mine = await Bandwidth.GrantAsync(Command(claim, T0.AddSeconds(1)), false, Ttl, default);
        Assert.Equal(new AttemptUploadLimits(1_000_000, 2_000_000, null), mine);
        Assert.Equal(new AttemptUploadLimits(9_000_000, null, null), await Bandwidth.GrantAsync(Command(otherClaim, T0.AddSeconds(1)), false, Ttl, default));
        Assert.Equal(mine, await Bandwidth.ReadAsync(tenant, agent.AgentId, claim.Attempt.Id, default));
        Assert.Null(await Bandwidth.ReadAsync(tenant, AgentId.New(), claim.Attempt.Id, default));
        Assert.Equal(0, await TotalGrantedAsync(tenant));
    }

    [Fact]
    public async Task Two_uploads_of_one_tenant_never_hold_more_than_the_tenant_limit_and_converge_on_equal_slices()
    {
        var tenant = await _kit.AddTenantAsync();
        await SetAsync(tenant, UploadBandwidthScope.Tenant, tenant.Value, Limit);
        var (first, _, _) = await RunningAsync(tenant, T0);
        var (second, _, _) = await RunningAsync(tenant, T0);

        Assert.Equal(Limit, await GrantedAsync(first, T0.AddSeconds(1)));
        Assert.Equal(0, await GrantedAsync(second, T0.AddSeconds(1)));
        Assert.True(await TotalGrantedAsync(tenant) <= Limit);

        Assert.Equal(Limit / 2, await GrantedAsync(first, T0.AddSeconds(4)));
        Assert.True(await TotalGrantedAsync(tenant) <= Limit);
        Assert.Equal(Limit / 2, await GrantedAsync(second, T0.AddSeconds(4)));
        Assert.Equal(Limit, await TotalGrantedAsync(tenant));

        // The first one ends: its slice returns at once and the other takes the whole limit on its next renewal.
        await Bandwidth.GrantAsync(Command(first, T0.AddSeconds(5)), release: true, Ttl, default);
        Assert.Equal(Limit / 2, await TotalGrantedAsync(tenant));
        Assert.Equal(Limit, await GrantedAsync(second, T0.AddSeconds(7)));
    }

    [Fact]
    public async Task Other_tenants_are_independent()
    {
        var one = await _kit.AddTenantAsync();
        var two = await _kit.AddTenantAsync();
        await SetAsync(one, UploadBandwidthScope.Tenant, one.Value, Limit);
        var (mine, _, _) = await RunningAsync(one, T0);
        var (theirs, _, _) = await RunningAsync(two, T0);

        Assert.Equal(Limit, await GrantedAsync(mine, T0.AddSeconds(1)));

        var unlimited = await Bandwidth.GrantAsync(Command(theirs, T0.AddSeconds(1)), false, Ttl, default);
        Assert.Null(unlimited!.Tenant);
        Assert.Null(unlimited.TenantShare);
        Assert.Equal(0, await TotalGrantedAsync(two));
    }

    [Fact]
    public async Task A_slice_is_freed_when_its_attempt_ends_its_lease_runs_out_or_its_renewals_stop()
    {
        var tenant = await _kit.AddTenantAsync();
        await SetAsync(tenant, UploadBandwidthScope.Tenant, tenant.Value, Limit);
        var store = _kit.Store(Limits());
        var (first, _, _) = await RunningAsync(tenant, T0);
        var (second, _, _) = await RunningAsync(tenant, T0);
        var (third, _, _) = await RunningAsync(tenant, T0);
        await GrantedAsync(first, T0.AddSeconds(1));
        await GrantedAsync(second, T0.AddSeconds(1));
        await GrantedAsync(third, T0.AddSeconds(1));

        // first: the attempt ends (cancelled/failed/succeeded) without ever releasing.
        await CompleteAsync(store, first, Failed("PreCheckFailed"), T0.AddSeconds(2));
        // second: renewals stop; its slice expires after the ttl even though the attempt is still leased.
        // third renews every few seconds.
        Assert.Equal(Limit / 2, await GrantedAsync(third, T0.AddSeconds(3)));
        Assert.Equal(Limit / 2, await GrantedAsync(third, T0.AddSeconds(8)));
        Assert.Equal(Limit, await GrantedAsync(third, T0.AddSeconds(12)));
        Assert.Null(await Bandwidth.GrantAsync(Command(first, T0.AddSeconds(13)), false, Ttl, default));
    }

    [Fact]
    public async Task A_lease_that_is_wrong_expired_or_not_running_gets_nothing()
    {
        var tenant = await _kit.AddTenantAsync();
        await SetAsync(tenant, UploadBandwidthScope.Tenant, tenant.Value, Limit);
        var (running, agent, job) = await RunningAsync(tenant, T0);
        var command = Command(running, T0.AddSeconds(1));

        Assert.Null(await Bandwidth.GrantAsync(command with { LeaseToken = Guid.NewGuid() }, false, Ttl, default));
        Assert.Null(await Bandwidth.GrantAsync(command with { LeaseToken = Guid.Empty }, false, Ttl, default));
        Assert.Null(await Bandwidth.GrantAsync(command with { AgentId = AgentId.New() }, false, Ttl, default));
        Assert.Null(await Bandwidth.GrantAsync(command with { TenantId = TenantId.New() }, false, Ttl, default));
        Assert.Null(await Bandwidth.GrantAsync(command with { Now = T0 + Lease + TimeSpan.FromSeconds(1) }, false, Ttl, default));
        Assert.Equal(0, await TotalGrantedAsync(tenant));

        // Claimed but never started: not an upload yet.
        var pending = await AddPacingAgentAsync(tenant);
        var queued = await _kit.AddJobAsync(pending);
        var store = _kit.Store(Limits());
        await _kit.StartRunAsync(store, queued, Guid.NewGuid().ToString(), T0);
        var leased = (await ClaimAsync(store, pending, T0))!;
        Assert.Null(await Bandwidth.GrantAsync(Command(leased, T0.AddSeconds(1)), false, Ttl, default));
        Assert.NotNull(agent);
        Assert.NotNull(job);
    }

    [Fact]
    public async Task Removing_the_tenant_limit_drops_the_slices_and_stops_dividing()
    {
        var tenant = await _kit.AddTenantAsync();
        await SetAsync(tenant, UploadBandwidthScope.Tenant, tenant.Value, Limit);
        var (claim, _, _) = await RunningAsync(tenant, T0);
        await GrantedAsync(claim, T0.AddSeconds(1));
        Assert.Equal(Limit, await TotalGrantedAsync(tenant));

        await SetAsync(tenant, UploadBandwidthScope.Tenant, tenant.Value, null);
        var policy = await Bandwidth.GrantAsync(Command(claim, T0.AddSeconds(2)), false, Ttl, default);

        Assert.Null(policy!.TenantShare);
        Assert.Equal(0, await TotalGrantedAsync(tenant));
    }

    [Fact]
    public async Task Concurrent_renewals_from_many_nodes_never_oversubscribe_the_limit()
    {
        var tenant = await _kit.AddTenantAsync();
        await SetAsync(tenant, UploadBandwidthScope.Tenant, tenant.Value, Limit);
        var claims = new List<BackupClaim>();
        for (var i = 0; i < 6; i++) claims.Add((await RunningAsync(tenant, T0)).Claim);

        for (var round = 0; round < 4; round++)
        {
            var at = T0.AddSeconds(1 + round * 3);
            // Separate store instances: what separate backend processes would hold.
            await Task.WhenAll(claims.Select(claim => new PostgreSqlUploadBandwidthStore(fixture.Options).GrantAsync(Command(claim, at), false, Ttl, default)));
            Assert.True(await TotalGrantedAsync(tenant) <= Limit, $"round {round}");
        }

        for (var round = 0; round < 3; round++)
            foreach (var claim in claims) await GrantedAsync(claim, T0.AddSeconds(20 + round));
        await using var context = fixture.CreatePlatformContext();
        var shares = await context.UploadBandwidthGrants.Where(grant => grant.TenantId == tenant).Select(grant => grant.BytesPerSecond).ToListAsync();
        Assert.Equal(6, shares.Count);
        Assert.True(shares.Sum() <= Limit);
        Assert.All(shares, share => Assert.Equal(Limit / 6, share));
    }

    [Fact]
    public async Task A_run_a_limit_applies_to_waits_for_an_agent_that_can_pace_its_uploads()
    {
        var agent = await _kit.AddAgentAsync();
        var job = await _kit.AddJobAsync(agent);
        var other = await _kit.AddJobAsync(agent);
        var store = _kit.Store(Limits());
        await _kit.StartRunAsync(store, job, "a", T0);
        await SetAsync(agent.TenantId, UploadBandwidthScope.Job, job.Id, 1_000_000);

        Assert.Null(await ClaimAsync(store, agent, T0));

        await using (var context = fixture.CreatePlatformContext())
        {
            context.AgentCapabilities.Add(new AgentCapabilityRecord(agent.AgentId, agent.TenantId, BackupTaskProtocol.UploadBandwidthLimitCapability,
                CapabilitySupport.Supported, null, "fixture", T0));
            await context.SaveChangesAsync();
        }
        Assert.NotNull(await ClaimAsync(store, agent, T0.AddSeconds(1)));
        Assert.NotNull(other);
    }

    [Fact]
    public async Task A_run_no_limit_applies_to_starts_on_an_agent_without_the_capability()
    {
        var agent = await _kit.AddAgentAsync();
        var limited = await _kit.AddJobAsync(agent);
        var free = await _kit.AddJobAsync(agent);
        var store = _kit.Store(Limits());
        await _kit.StartRunAsync(store, free, "free", T0);
        await SetAsync(agent.TenantId, UploadBandwidthScope.Job, limited.Id, 1_000_000);

        Assert.Equal(free.Id, (await ClaimAsync(store, agent, T0))!.Run.JobId);
    }
}
