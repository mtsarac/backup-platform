using BackupPlatform.Application.Backups;
using BackupPlatform.Domain.Backups;
using BackupPlatform.Domain.Identity;
using static BackupPlatform.UnitTests.Retention.RetentionFixtures;

namespace BackupPlatform.UnitTests.Retention;

/// <summary>
/// What an operator can do with backups and a job's retention: the policy merge over the job's CURRENT values and the platform minimums, tenant
/// scoping, and that every state decision is handed to the store with the backend's own clock.
/// </summary>
public sealed class BackupArtifactServiceTests
{
    private static readonly string Hash = new('a', 64);

    private sealed class FakeJobs(BackupJob? job) : IBackupScheduleStore
    {
        public UserId? Actor { get; private set; }
        public DateTimeOffset? ChangedAt { get; private set; }

        public Task<BackupJob?> ModifyJobAsync(TenantId tenantId, Guid jobId, Action<BackupJob> change, CancellationToken cancellationToken, UserId? actor = null, DateTimeOffset? now = null)
        {
            if (job is null || job.TenantId != tenantId || job.Id != jobId) return Task.FromResult<BackupJob?>(null);
            change(job);
            Actor = actor;
            ChangedAt = now;
            return Task.FromResult<BackupJob?>(job);
        }

        public Task<BackupScheduleEvaluation> EvaluateAsync(TenantId tenantId, Guid jobId, DateTimeOffset now, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<IReadOnlyList<ScheduledJobReference>> FindDueJobsAsync(DateTimeOffset now, int limit, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<IReadOnlyList<BackupJob>> ListScheduledJobsAsync(CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<int> ExpireUnclaimedRunsAsync(DateTimeOffset now, int limit, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<IReadOnlyList<BackupScheduleOccurrence>> ListOccurrencesAsync(TenantId tenantId, Guid jobId, int limit, CancellationToken cancellationToken) => throw new NotSupportedException();
    }

    private static BackupJob Job(TenantId tenant, BackupRetentionPolicy policy) =>
        new(Guid.NewGuid(), tenant, "job", AgentId.New(),
            BackupRunTestSupport.Configuration(), Now, retentionPolicy: policy);

    private static BackupRetentionPolicy Policy(bool enabled = true, double keepDays = 10, double graceHours = 2) =>
        new() { Enabled = enabled, KeepFor = TimeSpan.FromDays(keepDays), DeleteGracePeriod = TimeSpan.FromHours(graceHours) };

    private static BackupArtifactService Service(BackupJob? job, FakeRetentionStore? store = null, BackupRetentionOptions? options = null, TimeProvider? time = null) =>
        new(store ?? new FakeRetentionStore(), new FakeJobs(job), options ?? new BackupRetentionOptions(), time ?? new SettableClock(Now));

    private static BackupArtifactRecord Record(TenantId tenant, Guid? runId = null, Guid? jobId = null)
    {
        var artifact = new BackupArtifact(Guid.NewGuid(), tenant, Guid.NewGuid(), 100, Hash, Hash, "sha256", Now, "{}", "{}");
        artifact.AddReplica(new BackupReplica(Guid.NewGuid(), tenant, artifact.Id, "local", "default", "k", 100, Hash, "sha256", "Verified", "ReadBack", Now, Now,
            executionLocation: BackupStorageExecutionLocation.Backend));
        return new BackupArtifactRecord(artifact, runId ?? Guid.NewGuid(), jobId ?? Guid.NewGuid());
    }

    // ---- job retention ----

    [Fact]
    public async Task OmittedMembersKeepTheJobsCurrentValuesNotThePlatformDefaults()
    {
        var tenant = TenantId.New();
        var job = Job(tenant, Policy(keepDays: 10, graceHours: 2));
        var service = Service(job);

        var view = await service.ChangeJobRetentionAsync(tenant, job.Id, new BackupRetentionPolicyRequest(KeepForSeconds: TimeSpan.FromDays(3).TotalSeconds), CancellationToken.None);

        Assert.Equal(new BackupRetentionPolicyView(true, TimeSpan.FromDays(3).TotalSeconds, TimeSpan.FromHours(2).TotalSeconds), view!.Retention);
        Assert.Equal(TimeSpan.FromDays(3), job.RetentionPolicy.KeepFor);
        Assert.Equal(TimeSpan.FromHours(2), job.RetentionPolicy.DeleteGracePeriod);
    }

    [Fact]
    public async Task AnEmptyRequestChangesNothing()
    {
        var tenant = TenantId.New();
        var job = Job(tenant, Policy(keepDays: 10, graceHours: 2));

        var view = await Service(job).ChangeJobRetentionAsync(tenant, job.Id, new BackupRetentionPolicyRequest(), CancellationToken.None);

        Assert.Equal(new BackupRetentionPolicyView(true, TimeSpan.FromDays(10).TotalSeconds, TimeSpan.FromHours(2).TotalSeconds), view!.Retention);
    }

    [Fact]
    public async Task DisablingKeepsTheDurationsSoItCanBeEnabledAgainUnchanged()
    {
        var tenant = TenantId.New();
        var job = Job(tenant, Policy(keepDays: 10, graceHours: 2));
        var service = Service(job);

        await service.ChangeJobRetentionAsync(tenant, job.Id, new BackupRetentionPolicyRequest(Enabled: false), CancellationToken.None);
        Assert.False(job.RetentionPolicy.Enabled);
        var view = await service.ChangeJobRetentionAsync(tenant, job.Id, new BackupRetentionPolicyRequest(Enabled: true), CancellationToken.None);

        Assert.Equal(new BackupRetentionPolicyView(true, TimeSpan.FromDays(10).TotalSeconds, TimeSpan.FromHours(2).TotalSeconds), view!.Retention);
    }

    [Theory]
    [InlineData(59.0, null)]
    [InlineData(null, 59.0)]
    [InlineData(0.0, null)]
    public async Task ADurationBelowThePlatformMinimumIsRejectedAndTheJobKeepsItsPolicy(double? keepSeconds, double? graceSeconds)
    {
        var tenant = TenantId.New();
        var job = Job(tenant, Policy());
        var before = job.RetentionPolicy;

        await Assert.ThrowsAsync<ArgumentException>(() => Service(job).ChangeJobRetentionAsync(
            tenant, job.Id, new BackupRetentionPolicyRequest(KeepForSeconds: keepSeconds, DeleteGracePeriodSeconds: graceSeconds), CancellationToken.None));

        Assert.Equal(before, job.RetentionPolicy);
    }

    [Fact]
    public async Task TheMinimumsAreTheConfiguredOnesNotAFixedConstant()
    {
        var tenant = TenantId.New();
        var job = Job(tenant, Policy());
        var options = new BackupRetentionOptions { MinimumKeepFor = TimeSpan.FromDays(1), DefaultKeepFor = TimeSpan.FromDays(2) };

        await Assert.ThrowsAsync<ArgumentException>(() => Service(job, options: options).ChangeJobRetentionAsync(
            tenant, job.Id, new BackupRetentionPolicyRequest(KeepForSeconds: TimeSpan.FromHours(2).TotalSeconds), CancellationToken.None));
        var accepted = await Service(job, options: options).ChangeJobRetentionAsync(
            tenant, job.Id, new BackupRetentionPolicyRequest(KeepForSeconds: TimeSpan.FromDays(1).TotalSeconds), CancellationToken.None);

        Assert.Equal(TimeSpan.FromDays(1).TotalSeconds, accepted!.Retention!.KeepForSeconds);
    }

    [Fact]
    public async Task ADisabledPolicyNeedsNoMinimumKeep()
    {
        var tenant = TenantId.New();
        var job = Job(tenant, Policy());

        var view = await Service(job).ChangeJobRetentionAsync(tenant, job.Id, new BackupRetentionPolicyRequest(Enabled: false, KeepForSeconds: 30), CancellationToken.None);

        Assert.False(view!.Retention!.Enabled);
    }

    [Theory]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    [InlineData(-3600.0)]
    [InlineData(1e12)]
    public async Task UnusableNumbersAreRejectedNotStored(double seconds)
    {
        var tenant = TenantId.New();
        var job = Job(tenant, Policy());
        var before = job.RetentionPolicy;

        await Assert.ThrowsAsync<ArgumentException>(() => Service(job).ChangeJobRetentionAsync(
            tenant, job.Id, new BackupRetentionPolicyRequest(KeepForSeconds: seconds), CancellationToken.None));
        await Assert.ThrowsAsync<ArgumentException>(() => Service(job).ChangeJobRetentionAsync(
            tenant, job.Id, new BackupRetentionPolicyRequest(DeleteGracePeriodSeconds: seconds), CancellationToken.None));

        Assert.Equal(before, job.RetentionPolicy);
    }

    [Fact]
    public async Task AnUnknownJobAndAnotherTenantsJobAreBothNotFound()
    {
        var tenant = TenantId.New();
        var job = Job(tenant, Policy());
        var service = Service(job);
        var request = new BackupRetentionPolicyRequest(Enabled: false);

        Assert.Null(await service.ChangeJobRetentionAsync(tenant, Guid.NewGuid(), request, CancellationToken.None));
        Assert.Null(await service.ChangeJobRetentionAsync(TenantId.New(), job.Id, request, CancellationToken.None));
        Assert.True(job.RetentionPolicy.Enabled);
    }

    // ---- artifacts ----

    [Fact]
    public async Task AnArtifactIsShownWithTheRunAndJobItBelongsTo()
    {
        var tenant = TenantId.New();
        var record = Record(tenant);
        var store = new FakeRetentionStore { OnGetArtifact = (t, id) => t == tenant && id == record.Artifact.Id ? record : null };

        var view = await Service(null, store).GetAsync(tenant, record.Artifact.Id, CancellationToken.None);

        Assert.Equal(record.Artifact.Id, view!.Id);
        Assert.Equal(record.RunId, view.RunId);
        Assert.Equal(record.JobId, view.JobId);
        Assert.Single(view.Replicas);
        Assert.Null(await Service(null, store).GetAsync(TenantId.New(), record.Artifact.Id, CancellationToken.None));
    }

    [Theory]
    [InlineData(-5, 1)]
    [InlineData(0, 1)]
    [InlineData(50, 50)]
    [InlineData(100_000, 200)]
    public async Task TheListLimitIsBoundedBeforeItReachesTheStore(int asked, int passed)
    {
        var tenant = TenantId.New();
        var job = Guid.NewGuid();
        var records = new[] { Record(tenant), Record(tenant) };
        var store = new FakeRetentionStore { OnListArtifacts = (_, _, _) => records };

        var views = await Service(null, store).ListAsync(tenant, job, asked, CancellationToken.None);

        Assert.Equal((tenant, (Guid?)job, passed), Assert.Single(store.ListCalls));
        Assert.Equal(records.Select(record => record.Artifact.Id), views.Select(view => view.Id));
    }

    [Fact]
    public async Task PagedViewsCarrySafeLabelsAndCannotCancelAfterPhysicalDeletionStartsOrFails()
    {
        var tenant = TenantId.New();
        var record = Record(tenant) with { JobName = "Nightly", DatabaseName = "Accounting", AgentName = "db-host", AgentId = Guid.NewGuid() };
        var query = new BackupArtifactQuery(Page: 12, PageSize: 25);
        var store = new FakeRetentionStore
        {
            OnQueryArtifacts = (actualTenant, actualQuery) =>
            {
                Assert.Equal(tenant, actualTenant);
                Assert.Equal(query, actualQuery);
                return new([record], 301, query.Page, query.PageSize);
            }
        };
        var service = Service(null, store);
        record.Artifact.RequestDelete(Now, null);
        var pending = Assert.Single((await service.QueryAsync(tenant, query, default)).Items);
        Assert.True(pending.CancelDeleteAllowed);
        Assert.Equal("Nightly", pending.JobName);
        Assert.Equal("Accounting", pending.DatabaseName);
        Assert.Equal("db-host", pending.AgentName);
        Assert.Equal(record.AgentId, pending.AgentId);
        var replica = Assert.Single(record.Artifact.Replicas);
        var token = Guid.NewGuid();
        var due = replica.DeleteAfter!.Value;
        Assert.True(replica.BeginDelete(token, "worker", due, TimeSpan.FromMinutes(5)));
        var deleting = Assert.Single((await service.QueryAsync(tenant, query, default)).Items);
        Assert.Equal("Deleting", deleting.State);
        Assert.False(deleting.CancelDeleteAllowed);
        Assert.True(replica.FailDelete(token, due, BackupDeleteFailureCodes.IoError, due.AddMinutes(1)));
        var result = await service.QueryAsync(tenant, query, default);
        var failed = Assert.Single(result.Items);
        Assert.Equal(301, result.TotalCount);
        Assert.Equal(12, result.Page);
        Assert.Equal("DeleteFailed", failed.State);
        Assert.False(failed.CancelDeleteAllowed);
    }

    [Fact]
    public async Task ADeleteRequestCarriesTheBackendClockAndTheRequestingUser()
    {
        var tenant = TenantId.New();
        var record = Record(tenant);
        var clock = new SettableClock(Now.AddHours(3));
        var store = new FakeRetentionStore { OnRequestDelete = (_, _, _) => new BackupArtifactDeleteResult(BackupArtifactDeleteKind.Requested, record) };
        var user = Guid.NewGuid();

        var response = await Service(null, store, time: clock).RequestDeleteAsync(tenant, record.Artifact.Id, user, CancellationToken.None);

        Assert.Equal(BackupArtifactDeleteKind.Requested, response.Kind);
        Assert.Equal(record.JobId, response.Artifact!.JobId);
        Assert.Equal((tenant, record.Artifact.Id, (Guid?)user, Now.AddHours(3)), Assert.Single(store.DeleteRequests));
    }

    [Fact]
    public async Task RetentionChangesCarryTheRequestingUserAndTheBackendClock()
    {
        var tenant = TenantId.New();
        var job = Job(tenant, Policy());
        var jobs = new FakeJobs(job);
        var actor = UserId.New();
        var service = new BackupArtifactService(new FakeRetentionStore(), jobs, new(), new SettableClock(Now));

        await service.ChangeJobRetentionAsync(tenant, job.Id, new(Enabled: false), default, actor);

        Assert.Equal(actor, jobs.Actor);
        Assert.Equal(Now, jobs.ChangedAt);
    }

    [Fact]
    public async Task CancellingADeleteCarriesTheActualRequestingUser()
    {
        var tenant = TenantId.New();
        var record = Record(tenant);
        var actor = Guid.NewGuid();
        var store = new FakeRetentionStore { OnCancelDelete = (_, _, _) => new(BackupArtifactCancelKind.Restored, record) };

        await Service(null, store).CancelDeleteAsync(tenant, record.Artifact.Id, default, actor);

        Assert.Equal((tenant, record.Artifact.Id, (Guid?)actor, Now), Assert.Single(store.CancelRequests));
    }

    [Fact]
    public async Task ADeleteOfAnArtifactThatIsNotTheTenantsIsNotFoundWithoutAnArtifact()
    {
        var store = new FakeRetentionStore { OnRequestDelete = (_, _, _) => new BackupArtifactDeleteResult(BackupArtifactDeleteKind.NotFound, null) };

        var response = await Service(null, store).RequestDeleteAsync(TenantId.New(), Guid.NewGuid(), null, CancellationToken.None);

        Assert.Equal(BackupArtifactDeleteKind.NotFound, response.Kind);
        Assert.Null(response.Artifact);
    }

    [Theory]
    [InlineData(BackupArtifactCancelKind.Restored, true)]
    [InlineData(BackupArtifactCancelKind.NothingToCancel, true)]
    [InlineData(BackupArtifactCancelKind.NotFound, false)]
    public async Task CancellingADeleteReportsTheStoresDecisionWithTheBackendClock(BackupArtifactCancelKind kind, bool hasArtifact)
    {
        var tenant = TenantId.New();
        var record = Record(tenant);
        DateTimeOffset? seenNow = null;
        var store = new FakeRetentionStore
        {
            OnCancelDelete = (_, _, now) =>
            {
                seenNow = now;
                return new BackupArtifactCancelResult(kind, hasArtifact ? record : null);
            }
        };

        var response = await Service(null, store).CancelDeleteAsync(tenant, record.Artifact.Id, CancellationToken.None);

        Assert.Equal(kind, response.Kind);
        Assert.Equal(hasArtifact, response.Artifact is not null);
        Assert.Equal(Now, seenNow);
    }
}
