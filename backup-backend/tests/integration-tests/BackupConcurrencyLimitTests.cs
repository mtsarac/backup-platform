using BackupPlatform.Application.Backups;
using BackupPlatform.Domain.Backups;
using BackupPlatform.Domain.Identity;
using BackupPlatform.Infrastructure.Backups;
using Npgsql;
using static BackupPlatform.IntegrationTests.BackupStoreHarness;

namespace BackupPlatform.IntegrationTests;

/// <summary>
/// A slot is a live (Leased/Running, unexpired) attempt. The agent, tenant and global limits are enforced by counting those rows under
/// a database lock, so they hold across store instances, backend restarts and crashed agents.
/// </summary>
public sealed class BackupConcurrencyLimitTests(PostgreSqlFixture fixture) : IClassFixture<PostgreSqlFixture>
{
    private readonly BackupStoreHarness _kit = new(fixture);

    [Fact]
    public async Task AgentsAreStrictlySerialByDefault()
    {
        Assert.Equal(1, new BackupExecutionOptions().AgentMaxConcurrentBackups);
        var agent = await _kit.AddAgentAsync();
        var backlog = await BacklogAsync(_kit, agent, 2);
        // Only the global limit is lifted (it spans the shared database); the agent limit is the untouched default.
        var store = _kit.Store(new BackupExecutionOptions { GlobalMaxConcurrentBackups = Unbounded });

        var first = (await ClaimAsync(store, agent, T0))!;
        Assert.Equal(backlog.Runs[0].Id, first.Run.Id);
        Assert.Null(await ClaimAsync(store, agent, T0));
        await StartAttemptAsync(store, first, T0.AddSeconds(1));
        Assert.Null(await ClaimAsync(store, agent, T0.AddSeconds(2)));
        Assert.Null(await ClaimAsync(_kit.Store(new BackupExecutionOptions { GlobalMaxConcurrentBackups = Unbounded }), agent, T0.AddSeconds(3)));
    }

    [Fact]
    public async Task ConfiguredAgentLimitAdmitsExactlyThatManyConcurrentAttempts()
    {
        var agent = await _kit.AddAgentAsync();
        var backlog = await BacklogAsync(_kit, agent, 4);
        var store = _kit.Store(Limits(agent: 2));

        var first = (await ClaimAsync(store, agent, T0))!;
        var second = (await ClaimAsync(store, agent, T0))!;
        Assert.Equal(new[] { backlog.Runs[0].Id, backlog.Runs[1].Id }, new[] { first.Run.Id, second.Run.Id });
        await StartAttemptAsync(store, first, T0.AddSeconds(1));
        Assert.Null(await ClaimAsync(store, agent, T0.AddSeconds(2)));

        await CompleteAsync(store, first, Failed("PreCheckFailed"), T0.AddSeconds(3));
        Assert.Equal(backlog.Runs[2].Id, (await ClaimAsync(store, agent, T0.AddSeconds(4)))!.Run.Id);
        Assert.Null(await ClaimAsync(store, agent, T0.AddSeconds(5)));
    }

    [Fact]
    public async Task RenewingTheLeaseKeepsTheSlotUntilTheRenewedExpiry()
    {
        var agent = await _kit.AddAgentAsync();
        var backlog = await BacklogAsync(_kit, agent, 2);
        var store = _kit.Store(Limits(agent: 1));
        var held = (await ClaimAsync(store, agent, T0))!;
        await StartAttemptAsync(store, held, T0.AddSeconds(10));

        var renewed = await store.RenewLeaseAsync(Command(held, T0.AddSeconds(100)), Lease, default);
        Assert.Equal(T0.AddSeconds(220), renewed!.LeaseExpiresAt);

        Assert.Null(await ClaimAsync(store, agent, T0 + Lease)); // the original expiry would have freed the slot
        Assert.Null(await ClaimAsync(store, agent, T0.AddSeconds(219)));
        Assert.Equal(backlog.Runs[1].Id, (await ClaimAsync(store, agent, T0.AddSeconds(220)))!.Run.Id);
    }

    [Fact]
    public async Task TenantLimitCapsOneTenantWithoutBlockingAnother()
    {
        var tenantA = await _kit.AddTenantAsync();
        var tenantB = await _kit.AddTenantAsync();
        // The agent limit is out of the way, so the tenant limit is the only thing that can stop tenant A's third agent.
        var store = _kit.Store(Limits(agent: 5, tenant: 2));
        var a = new List<StoreAgent>();
        for (var i = 0; i < 3; i++)
        {
            a.Add(new StoreAgent(tenantA, await _kit.AddAgentToAsync(tenantA)));
            await BacklogAsync(_kit, a[i], 1);
        }
        var b = new StoreAgent(tenantB, await _kit.AddAgentToAsync(tenantB));
        await BacklogAsync(_kit, b, 1);

        var firstA = (await ClaimAsync(store, a[0], T0))!;
        Assert.NotNull(await ClaimAsync(store, a[1], T0));
        Assert.Null(await ClaimAsync(store, a[2], T0));
        Assert.NotNull(await ClaimAsync(store, b, T0)); // the global limit is unbounded here: only tenant B's own count matters
        Assert.Null(await ClaimAsync(store, a[2], T0.AddSeconds(1)));

        await CompleteAsync(store, firstA, Failed("PreCheckFailed"), T0.AddSeconds(2));
        Assert.NotNull(await ClaimAsync(store, a[2], T0.AddSeconds(3)));
    }

    [Fact]
    public async Task TenantOverridesReplaceTheDefaultForOnlyTheNamedTenantWhateverTheSpellingOfItsId()
    {
        var raised = await _kit.AddTenantAsync();
        var plain = await _kit.AddTenantAsync();
        var lowered = await _kit.AddTenantAsync();
        var respelled = await _kit.AddTenantAsync();
        var store = _kit.Store(Limits(agent: 1, tenant: 2, overrides: new()
        {
            [raised.Value.ToString()] = 3,
            [lowered.Value.ToString("B").ToUpperInvariant()] = 1, // {ABC...}: a configuration file may spell the same id differently
            [respelled.Value.ToString("N").ToUpperInvariant()] = 4
        }));

        async Task<int> ClaimsOfAsync(TenantId tenant)
        {
            var claims = 0;
            for (var i = 0; i < 5; i++) // five agents, each with work, so only the tenant limit decides
            {
                var agent = new StoreAgent(tenant, await _kit.AddAgentToAsync(tenant));
                await BacklogAsync(_kit, agent, 1);
                if (await ClaimAsync(store, agent, T0) is not null) claims++;
            }
            return claims;
        }

        Assert.Equal(3, await ClaimsOfAsync(raised));
        Assert.Equal(2, await ClaimsOfAsync(plain));
        Assert.Equal(1, await ClaimsOfAsync(lowered));
        Assert.Equal(4, await ClaimsOfAsync(respelled));
    }

    [Fact]
    public Task GlobalLimitCapsLiveAttemptsAcrossAllTenants() => WithIsolatedDatabaseAsync(async kit =>
    {
        var store = kit.Store(Limits(agent: 1, tenant: 2, global: 3));
        var agents = new Dictionary<string, StoreAgent>();
        foreach (var tenantName in new[] { "A", "B", "C", "D" })
        {
            var tenant = await kit.AddTenantAsync();
            foreach (var number in new[] { "1", "2" })
            {
                var agent = new StoreAgent(tenant, await kit.AddAgentToAsync(tenant));
                agents[tenantName + number] = agent;
                await BacklogAsync(kit, agent, 1);
            }
        }

        Assert.NotNull(await ClaimAsync(store, agents["A1"], T0));
        var b1 = (await ClaimAsync(store, agents["B1"], T0))!;
        Assert.NotNull(await ClaimAsync(store, agents["C1"], T0));
        // Tenant D has nothing running and agent D1 is idle: only the global limit, filled by three other tenants, can refuse it.
        Assert.Null(await ClaimAsync(store, agents["D1"], T0.AddSeconds(1)));
        Assert.Null(await ClaimAsync(store, agents["A2"], T0.AddSeconds(1)));
        Assert.Equal(3, (await LiveSlotsAsync(kit.Database.ConnectionString, T0.AddSeconds(1))).Sum(slot => slot.Count));

        await CompleteAsync(store, b1, Failed("PreCheckFailed"), T0.AddSeconds(2)); // a slot freed by another tenant is enough
        Assert.NotNull(await ClaimAsync(store, agents["D1"], T0.AddSeconds(3)));
        Assert.Null(await ClaimAsync(store, agents["A2"], T0.AddSeconds(3)));

        // A1 and C1 leased at T0 and went silent; their expired leases stop counting globally.
        Assert.NotNull(await ClaimAsync(store, agents["A2"], T0 + Lease));
    });

    [Fact]
    public Task SimultaneousClaimsFromIndependentBackendsNeverExceedAnyLimit() => WithIsolatedDatabaseAsync(async kit =>
    {
        const int tenants = 4, agentsPerTenant = 3, pollsPerAgent = 3;
        var limits = Limits(agent: 2, tenant: 3, global: 7);
        var agents = new List<StoreAgent>();
        for (var t = 0; t < tenants; t++)
        {
            var tenant = await kit.AddTenantAsync();
            for (var a = 0; a < agentsPerTenant; a++) agents.Add(new StoreAgent(tenant, await kit.AddAgentToAsync(tenant)));
        }
        // More queued runs per agent than it can ever claim below, so a null claim always means "a limit said no".
        await Task.WhenAll(agents.Select(agent => BacklogAsync(kit, agent, 12)));

        // Saturation: 36 polls hit the database at once, each through its own store instance. Demand (12 agents) exceeds the
        // global limit, so exactly the limit must be granted, no more and no less. Repeated because races are probabilistic.
        for (var round = 0; round < 3; round++)
        {
            var claims = (await Task.WhenAll(agents.SelectMany(agent => Enumerable.Range(0, pollsPerAgent)
                .Select(_ => Task.Run(() => ClaimAsync(kit.Store(limits), agent, T0)))))).OfType<BackupClaim>().ToList();

            Assert.Equal(7, claims.Count);
            Assert.Equal(7, claims.Select(claim => claim.Attempt.Id).Distinct().Count());
            var live = await LiveSlotsAsync(kit.Database.ConnectionString, T0);
            Assert.Equal(7, live.Sum(slot => slot.Count));
            Assert.All(live, slot => Assert.True(slot.Count <= 2, "agent limit exceeded"));
            Assert.All(live.GroupBy(slot => slot.Tenant), tenant => Assert.True(tenant.Sum(slot => slot.Count) <= 3, "tenant limit exceeded"));

            await Task.WhenAll(claims.Select(claim => Task.Run(() => CompleteAsync(kit.Store(limits), claim, Failed("PreCheckFailed"), T0.AddSeconds(1)))));
            Assert.Empty(await LiveSlotsAsync(kit.Database.ConnectionString, T0));
        }

        // Churn: every agent keeps claiming and finishing while a monitor samples the table. The limits must hold at every instant,
        // not only once the dust has settled.
        using var stop = new CancellationTokenSource();
        var monitor = MonitorPeaksAsync(kit.Database.ConnectionString, stop.Token);
        var granted = await Task.WhenAll(agents.Select(agent => Task.Run(async () =>
        {
            var mine = new List<Guid>();
            for (var i = 0; i < 6; i++)
            {
                var claim = await ClaimAsync(kit.Store(limits), agent, T0);
                if (claim is null) { await Task.Yield(); continue; }
                mine.Add(claim.Attempt.Id);
                await StartAttemptAsync(kit.Store(limits), claim, T0.AddSeconds(1));
                await CompleteAsync(kit.Store(limits), claim, Failed("PreCheckFailed"), T0.AddSeconds(2));
            }
            return mine;
        })));
        await stop.CancelAsync();
        var peak = await monitor;

        var attempts = granted.SelectMany(ids => ids).ToList();
        Assert.NotEmpty(attempts);
        Assert.Equal(attempts.Count, attempts.Distinct().Count());
        Assert.True(peak.Global <= 7, $"global peak {peak.Global}");
        Assert.True(peak.Tenant <= 3, $"tenant peak {peak.Tenant}");
        Assert.True(peak.Agent <= 2, $"agent peak {peak.Agent}");
        Assert.Empty(await LiveSlotsAsync(kit.Database.ConnectionString, T0));
    });

    [Theory]
    [InlineData("Succeeded")]
    [InlineData("Failed")]
    [InlineData("Cancelled")]
    [InlineData("AgentLost")]
    public async Task SlotIsReleasedWhenTheHoldingAttemptEnds(string ending)
    {
        var agent = await _kit.AddAgentAsync();
        var backlog = await BacklogAsync(_kit, agent, 2);
        var store = _kit.Store(Limits(agent: 1));
        var held = (await ClaimAsync(store, agent, T0))!;
        await StartAttemptAsync(store, held, T0.AddSeconds(1));
        Assert.Null(await ClaimAsync(store, agent, T0.AddSeconds(2)));

        var freedAt = T0.AddSeconds(30);
        switch (ending)
        {
            case "Succeeded":
                await CompleteAsync(store, held, Success(freedAt), freedAt);
                break;
            case "Failed":
                await CompleteAsync(store, held, Failed("PreCheckFailed"), freedAt);
                break;
            case "Cancelled":
                Assert.Equal(BackupStatus.Running, (await store.RequestCancellationAsync(agent.TenantId, held.Run.Id, T0.AddSeconds(10), default))!.Status);
                // Cancellation is cooperative: the slot stays taken until the agent has actually stopped.
                Assert.Null(await ClaimAsync(store, agent, T0.AddSeconds(11)));
                await CompleteAsync(store, held, Cancelled(), freedAt);
                break;
            default:
                freedAt = T0 + Lease;
                await store.ExpireStaleAsync(freedAt, default); // database-wide: other tests' abandoned leases may be flipped too
                Assert.Equal(BackupStatus.AgentLost, (await store.GetRunAsync(agent.TenantId, held.Run.Id, default))!.Attempts.Single().Status);
                break;
        }

        Assert.Equal(backlog.Runs[1].Id, (await ClaimAsync(store, agent, freedAt))!.Run.Id);
        Assert.Equal(1, Assert.Single(await LiveSlotsAsync(fixture.ConnectionString, freedAt), slot => slot.Agent == agent.AgentId.Value).Count);
    }

    [Fact]
    public async Task ExpiredLeaseFreesTheSlotWithoutWaitingForTheStaleDetector()
    {
        var agent = await _kit.AddAgentAsync();
        var backlog = await BacklogAsync(_kit, agent, 2);
        var store = _kit.Store(Limits(agent: 1));
        var lost = (await ClaimAsync(store, agent, T0))!;
        await StartAttemptAsync(store, lost, T0);
        var expiry = T0 + Lease;

        Assert.Null(await ClaimAsync(store, agent, expiry.AddSeconds(-1)));
        var next = await ClaimAsync(store, agent, expiry); // no ExpireStaleAsync call anywhere in this test

        Assert.Equal(backlog.Runs[1].Id, next!.Run.Id);
        var run = (await store.GetRunAsync(agent.TenantId, lost.Run.Id, default))!;
        Assert.Equal(BackupStatus.AgentLost, run.Attempts.Single().Status);
        Assert.Equal(BackupStatus.RetryPending, run.Status); // same outcome as if the stale detector had found it: AgentLost is retried
        Assert.Equal(BackupMutationResult.Conflict, await store.CompleteAttemptAsync(Command(lost, expiry), Failed("InternalError"), default));
        Assert.Null(await store.RenewLeaseAsync(Command(lost, expiry), Lease, default));
    }

    [Fact]
    public async Task ExpiredLeaseOfASilentAgentNoLongerCountsAgainstTheTenant()
    {
        var tenant = await _kit.AddTenantAsync();
        var silent = new StoreAgent(tenant, await _kit.AddAgentToAsync(tenant));
        var other = new StoreAgent(tenant, await _kit.AddAgentToAsync(tenant));
        await BacklogAsync(_kit, silent, 1);
        var backlog = await BacklogAsync(_kit, other, 1);
        var store = _kit.Store(Limits(agent: 1, tenant: 1));
        var abandoned = (await ClaimAsync(store, silent, T0))!;

        Assert.Null(await ClaimAsync(store, other, T0.AddSeconds(1)));
        // The silent agent never polls or reports again, so nothing flips its attempt; its lease simply runs out.
        Assert.Equal(backlog.Runs[0].Id, (await ClaimAsync(store, other, T0 + Lease))!.Run.Id);

        await store.ExpireStaleAsync(T0 + Lease, default);
        var lost = (await store.GetRunAsync(tenant, abandoned.Run.Id, default))!;
        Assert.Equal(BackupStatus.AgentLost, lost.Attempts.Single().Status);
    }

    [Fact]
    public async Task RestartedBackendSeesSlotsHeldByTheOldOneAndCanReleaseThem()
    {
        var tenant = await _kit.AddTenantAsync();
        var holder = new StoreAgent(tenant, await _kit.AddAgentToAsync(tenant));
        var waiting = new StoreAgent(tenant, await _kit.AddAgentToAsync(tenant));
        await BacklogAsync(_kit, holder, 1);
        var backlog = await BacklogAsync(_kit, waiting, 1);
        var limits = Limits(agent: 1, tenant: 1);
        var claim = (await ClaimAsync(_kit.Store(limits), holder, T0))!;

        // The first backend instance is gone; the second starts with no memory and fresh connection options.
        var restarted = _kit.Store(limits);
        Assert.Null(await ClaimAsync(restarted, waiting, T0.AddSeconds(30)));
        await CompleteAsync(restarted, claim, Failed("PreCheckFailed"), T0.AddSeconds(31));

        Assert.Equal(backlog.Runs[0].Id, (await ClaimAsync(restarted, waiting, T0.AddSeconds(32)))!.Run.Id);
    }

    [Fact]
    public async Task SlotOfACrashedBackendIsNotLeakedBeyondItsLease()
    {
        var agent = await _kit.AddAgentAsync();
        var backlog = await BacklogAsync(_kit, agent, 2);
        var limits = Limits(agent: 1);
        await ClaimAsync(_kit.Store(limits), agent, T0); // claimed, then the process died: nobody will ever complete or expire it

        var restarted = _kit.Store(limits);
        Assert.Null(await ClaimAsync(restarted, agent, T0.AddSeconds(60)));
        Assert.Equal(backlog.Runs[1].Id, (await ClaimAsync(restarted, agent, T0 + Lease))!.Run.Id);
    }

    [Theory]
    [InlineData(60, true)]  // the newer run appeared after the retry became eligible (T0+35s): the retry keeps its place in line
    [InlineData(10, false)] // the newer run appeared before it: eligibility, not the older run's age, orders the queue
    public async Task RetryAttemptIsOrderedByWhenItBecameEligible(int newerRunCreatedSecond, bool retryFirst)
    {
        var agent = await _kit.AddAgentAsync();
        var job = await _kit.AddJobAsync(agent);
        var store = _kit.Store(Limits(agent: 10));
        var older = await _kit.StartRunAsync(store, job, "older", T0);
        var attempt = (await ClaimAsync(store, agent, T0))!;
        await StartAttemptAsync(store, attempt, T0);
        await CompleteAsync(store, attempt, Failed("OperationTimedOut"), T0.AddSeconds(5)); // retried 30 s later: eligible at T0+35s
        var newer = await _kit.StartRunAsync(store, job, "newer", T0.AddSeconds(newerRunCreatedSecond));
        // Database-wide scan on a shared database: only this test's run is asserted on.
        Assert.Contains(await store.StartDueRetriesAsync(T0.AddMinutes(10), 500, default), started => started.RunId == older.Id);

        var order = new[] { (await ClaimAsync(store, agent, T0.AddMinutes(10)))!, (await ClaimAsync(store, agent, T0.AddMinutes(10)))! };

        Assert.Equal(retryFirst ? new[] { older.Id, newer.Id } : new[] { newer.Id, older.Id }, order.Select(claim => claim.Run.Id));
        Assert.Equal(2, order.Single(claim => claim.Run.Id == older.Id).Attempt.Number);
    }

    [Fact]
    public async Task EqualEligibilityIsOrderedByRunCreationThenById()
    {
        var agent = await _kit.AddAgentAsync();
        var job = await _kit.AddJobAsync(agent);
        var store = _kit.Store(Limits(agent: 20));

        // Six runs created one second apart fail together, so their retry attempts are eligible at exactly the same instant.
        var runs = new List<BackupRun>();
        for (var i = 0; i < 6; i++) runs.Add(await _kit.StartRunAsync(store, job, $"retry-{i}", T0.AddSeconds(i)));
        foreach (var run in runs)
        {
            var claim = (await ClaimAsync(store, agent, T0.AddSeconds(60)))!;
            Assert.Equal(run.Id, claim.Run.Id);
            await StartAttemptAsync(store, claim, T0.AddSeconds(61));
            await CompleteAsync(store, claim, Failed("OperationTimedOut"), T0.AddSeconds(100));
        }
        var started = await store.StartDueRetriesAsync(T0.AddSeconds(200), 500, default); // database-wide scan: assert on this test's runs only
        Assert.All(runs, run => Assert.Contains(started, retry => retry.RunId == run.Id));

        var order = new List<Guid>();
        for (var i = 0; i < 6; i++) order.Add((await ClaimAsync(store, agent, T0.AddSeconds(200)))!.Run.Id);
        Assert.Equal(runs.Select(run => run.Id), order);

        // Same run creation instant AND same eligibility: the only thing left to order by is the id.
        var peer = await _kit.AddAgentAsync();
        var peerJob = await _kit.AddJobAsync(peer);
        var peerStore = _kit.Store(Limits(agent: 20));
        var simultaneous = new List<Guid>();
        for (var i = 0; i < 6; i++) simultaneous.Add((await _kit.StartRunAsync(peerStore, peerJob, $"same-instant-{i}", T0)).Id);
        var claimed = new List<Guid>();
        for (var i = 0; i < 6; i++) claimed.Add((await ClaimAsync(peerStore, peer, T0))!.Run.Id);
        // PostgreSQL compares uuids bytewise in RFC 4122 order, which is what ToByteArray(bigEndian: true) yields.
        Assert.Equal(simultaneous.OrderBy(id => Convert.ToHexString(id.ToByteArray(bigEndian: true)), StringComparer.Ordinal), claimed);
    }

    [Fact]
    public Task LargeBacklogOfOneTenantDoesNotStarveAnotherTenantsAgent() => WithIsolatedDatabaseAsync(async kit =>
    {
        // Global capacity 3; a tenant may hold at most 2 of it. Tenant A has a huge queue on every agent and polls first.
        var store = kit.Store(Limits(agent: 1, tenant: 2, global: 3));
        var tenantA = await kit.AddTenantAsync();
        var busy = new List<StoreAgent>();
        for (var i = 0; i < 3; i++)
        {
            busy.Add(new StoreAgent(tenantA, await kit.AddAgentToAsync(tenantA)));
            await BacklogAsync(kit, busy[i], 10);
        }
        var victim = await kit.AddAgentAsync();
        await BacklogAsync(kit, victim, 1);

        var grabbed = 0;
        for (var poll = 0; poll < 5; poll++)
            foreach (var agent in busy)
                if (await ClaimAsync(store, agent, T0) is not null) grabbed++;

        Assert.Equal(2, grabbed);
        Assert.NotNull(await ClaimAsync(store, victim, T0));
        Assert.Null(await ClaimAsync(store, busy[2], T0.AddSeconds(1)));
    });

    private static async Task<Backlog> BacklogAsync(BackupStoreHarness kit, StoreAgent agent, int runs)
    {
        var store = kit.Store();
        var job = await kit.AddJobAsync(agent);
        var created = new List<BackupRun>();
        for (var i = 0; i < runs; i++) created.Add(await kit.StartRunAsync(store, job, $"run-{i}", T0.AddSeconds(i)));
        return new Backlog(job, created);
    }

    private static async Task<Peaks> MonitorPeaksAsync(string connectionString, CancellationToken stop)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        Peaks peak = new(0, 0, 0);
        while (!stop.IsCancellationRequested)
        {
            var slots = await LiveSlotsAsync(connection, T0);
            peak = new Peaks(
                Math.Max(peak.Global, slots.Sum(slot => slot.Count)),
                Math.Max(peak.Tenant, slots.GroupBy(slot => slot.Tenant).Select(tenant => tenant.Sum(slot => slot.Count)).DefaultIfEmpty().Max()),
                Math.Max(peak.Agent, slots.Select(slot => slot.Count).DefaultIfEmpty().Max()));
            await Task.Delay(1);
        }
        return peak;
    }

    private sealed record Backlog(BackupJob Job, IReadOnlyList<BackupRun> Runs);
    private sealed record Peaks(long Global, long Tenant, long Agent);
}
