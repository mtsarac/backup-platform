using BackupPlatform.Application.Agents;
using BackupPlatform.Application.Backups;
using BackupPlatform.Contracts.Backups;
using BackupPlatform.Domain.Backups;
using BackupPlatform.Domain.Identity;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace BackupPlatform.UnitTests.Backup;

/// <summary>Backend half of an agent-executed SFTP/Google Drive repair: what is handed to the agent, and how its report is applied.</summary>
public sealed class AgentReplicaRepairUnitTests
{
    private static readonly DateTimeOffset T0 = new(2026, 10, 24, 10, 0, 0, TimeSpan.Zero);
    private static readonly TenantId Tenant = TenantId.New();
    private static readonly AgentIdentity Agent = new(AgentId.New(), Tenant, Guid.NewGuid(), 1);
    private static readonly string Checksum = new('b', 64);
    private const long Size = 4096;

    private static BackupArtifact NewArtifact() => new(Guid.NewGuid(), Tenant, Guid.NewGuid(), Size, new('a', 64), Checksum, "sha256", T0, "{}", "{}");

    private static BackupReplica NewReplica(BackupArtifact artifact, string provider, string name = "source.bak",
        BackupStorageExecutionLocation location = BackupStorageExecutionLocation.Backend)
    {
        var connection = Guid.NewGuid();
        return new BackupReplica(Guid.NewGuid(), Tenant, artifact.Id, provider, provider == "local" ? "local" : connection.ToString("D"),
            $"{Tenant.Value:N}/job/run/attempt/{name}", Size, Checksum, "sha256", "Verified", "ReadBack", T0, T0, location,
            owningAgentId: location == BackupStorageExecutionLocation.Agent ? Guid.NewGuid() : null, storageConnectionId: connection);
    }

    private sealed class Harness
    {
        public readonly FakeStore Store = new();
        public readonly BackupArtifact Artifact = NewArtifact();
        public readonly Guid TargetConnection = Guid.NewGuid();
        public readonly Guid LeaseToken = Guid.NewGuid();
        public readonly FakeTimeProvider Time = new(T0);
        public ReplicaRepairOperation Operation = null!;
        public BackupReplica Source = null!;

        public Harness(string targetProvider = "sftp", string sourceProvider = "sftp", bool pinned = true, string? owner = null)
        {
            Source = NewReplica(Artifact, sourceProvider);
            Artifact.AddReplica(Source);
            Operation = new ReplicaRepairOperation(Guid.NewGuid(), Tenant, Artifact.Id, TargetConnection, targetProvider, Source.Id, T0, totalBytes: Size);
            Operation.Claim(LeaseToken, owner ?? ReplicaRepairOperation.AgentLeaseOwner(Agent.AgentId.Value), T0, TimeSpan.FromMinutes(10));
            if (pinned) Operation.SetTargetIntent(Source.Id, TargetKeyFor(Source));
            Store.Operation = Operation;
            Store.Artifact = Artifact;
        }

        public string TargetKeyFor(BackupReplica source) =>
            $"{source.Key[..source.Key.LastIndexOf('/')]}/{ManagedBackupLayout.RepairFileName(Operation.Id, LeaseToken)}";

        public ReplicaRepairService Service => new(Store, null!, Time, NullLogger<ReplicaRepairService>.Instance);

        public Task<BackupMutationResult> CompleteAsync(ReplicaRepairCompletionRequest request, Func<Guid, string, TaskStorageCatalogRecord, CancellationToken, Task>? save = null) =>
            Service.CompleteForAgentAsync(Agent, Operation.Id, request with { LeaseToken = request.LeaseToken == Guid.Empty ? LeaseToken : request.LeaseToken },
                save ?? ((_, _, _, _) => Task.CompletedTask), default);
    }

    [Fact]
    public async Task Claim_WhenAdmissionDenied_FailsOperationAndHandsNothingToTheAgent()
    {
        var h = new Harness();
        h.Store.Admission = new ReplicaRepairAdmissionResult(false, ReplicaRepairFailureCodes.AlreadyVerified, "already");
        h.Store.ClaimResult = h.Operation;

        var snapshot = await h.Service.ClaimForAgentAsync(Agent, default);

        Assert.Null(snapshot);
        Assert.Equal(("fail", ReplicaRepairFailureCodes.AlreadyVerified), (h.Store.Terminal!.Kind, h.Store.Terminal.Code));
        Assert.Equal(h.Operation.Id, h.Store.AdmittedExcluding);
    }

    [Fact]
    public async Task Claim_KeepsOnlyExternalBackendSourcesInOrder_AndDescribesTheTarget()
    {
        var h = new Harness();
        var central = NewReplica(h.Artifact, "central", "central.bak");
        var local = NewReplica(h.Artifact, "local", "local.bak", BackupStorageExecutionLocation.Agent);
        var drive = NewReplica(h.Artifact, "google-drive", "drive.bak");
        h.Store.ClaimResult = h.Operation;
        h.Store.Admission = Admit(h, [central, local, h.Source, drive]);

        var snapshot = await h.Service.ClaimForAgentAsync(Agent, default);

        Assert.NotNull(snapshot);
        Assert.Equal([h.Source.Id, drive.Id], snapshot.Sources.Select(source => source.ReplicaId));
        Assert.Equal(h.Source.Key, snapshot.Sources[0].StorageKey);
        Assert.Equal(h.Source.StorageConnectionId, snapshot.Sources[0].StorageConnectionId);
        Assert.Equal(h.Operation.Id, snapshot.OperationId);
        Assert.Equal(Tenant.Value, snapshot.TenantId);
        Assert.Equal(Agent.AgentId.Value, snapshot.AgentId);
        Assert.Equal(h.LeaseToken, snapshot.LeaseToken);
        Assert.Equal(h.Operation.LeaseExpiresAt, snapshot.LeaseExpiresAt);
        Assert.Equal(h.Artifact.SizeBytes, snapshot.SizeBytes);
        Assert.Equal(h.Artifact.ArtifactChecksum, snapshot.Checksum);
        Assert.Equal("sftp", snapshot.Target.StorageProvider);
        Assert.Equal(h.TargetConnection, snapshot.Target.StorageConnectionId);
        Assert.Null(h.Store.Terminal);
    }

    [Fact]
    public async Task Claim_WhenOnlyCentralOrAgentLocalSourcesExist_FailsAsUnsupportedSource()
    {
        var h = new Harness();
        h.Store.ClaimResult = h.Operation;
        h.Store.Admission = Admit(h, [NewReplica(h.Artifact, "central", "c.bak"), NewReplica(h.Artifact, "local", "l.bak", BackupStorageExecutionLocation.Agent)]);

        Assert.Null(await h.Service.ClaimForAgentAsync(Agent, default));
        Assert.Equal(("fail", ReplicaRepairFailureCodes.UnsupportedSource), (h.Store.Terminal!.Kind, h.Store.Terminal.Code));
    }

    private static BackupReplica OwnLocal(BackupArtifact artifact, Guid owner, string name = "own.bak") =>
        new(Guid.NewGuid(), Tenant, artifact.Id, "local", "local", $"{Tenant.Value:N}/job/run/attempt/{name}", Size, Checksum, "sha256", "Verified", "ReadBack", T0, T0,
            BackupStorageExecutionLocation.Agent, owningAgentId: owner, storageConnectionId: Guid.NewGuid());

    [Theory]
    [InlineData("sftp")]
    [InlineData("s3")]
    public async Task Claim_HandsTheClaimingAgentItsOwnLocalReplicaAsASource(string targetProvider)
    {
        var h = new Harness(targetProvider);
        var own = OwnLocal(h.Artifact, Agent.AgentId.Value);
        var foreign = NewReplica(h.Artifact, "local", "foreign.bak", BackupStorageExecutionLocation.Agent);
        h.Store.ClaimResult = h.Operation;
        h.Store.Admission = Admit(h, [foreign, own]);

        var snapshot = await h.Service.ClaimForAgentAsync(Agent, default);

        var source = Assert.Single(snapshot!.Sources);
        Assert.Equal((own.Id, "local", "local", Guid.Empty, own.Key), (source.ReplicaId, source.StorageProvider, source.StorageTarget, source.StorageConnectionId, source.StorageKey));
        Assert.Null(h.Store.Terminal);
    }

    [Fact]
    public async Task Pin_AcceptsTheAgentsOwnLocalReplicaButNotAnotherAgents()
    {
        var h = new Harness("s3", pinned: false);
        var own = OwnLocal(h.Artifact, Agent.AgentId.Value);
        var foreign = OwnLocal(h.Artifact, Guid.NewGuid(), "foreign.bak");
        h.Artifact.AddReplica(own);
        h.Artifact.AddReplica(foreign);

        Assert.Equal(BackupMutationResult.Conflict, await h.Service.PinForAgentAsync(Agent, h.Operation.Id,
            new() { LeaseToken = h.LeaseToken, SourceReplicaId = foreign.Id, TargetKey = h.TargetKeyFor(foreign) }, default));
        Assert.Equal(BackupMutationResult.Accepted, await h.Service.PinForAgentAsync(Agent, h.Operation.Id,
            new() { LeaseToken = h.LeaseToken, SourceReplicaId = own.Id, TargetKey = h.TargetKeyFor(own) }, default));
        Assert.Equal(h.TargetKeyFor(own), h.Store.PinnedKey);
    }

    [Fact]
    public async Task Access_ForAnS3TargetIsTheOperationConnection_AndALocalSourceNeedsNone()
    {
        var h = new Harness("s3", pinned: false);
        var own = OwnLocal(h.Artifact, Agent.AgentId.Value);
        h.Artifact.AddReplica(own);
        h.Operation.SetTargetIntent(own.Id, h.TargetKeyFor(own));

        Assert.Equal(new ReplicaRepairAccessTarget(h.TargetConnection, "s3"), await h.Service.ResolveAccessAsync(Agent, h.Operation.Id, h.LeaseToken, source: false, default));
        Assert.Null(await h.Service.ResolveAccessAsync(Agent, h.Operation.Id, h.LeaseToken, source: true, default));
    }

    [Fact]
    public async Task Claim_WhenNoCandidateSourceExists_FailsAsNoVerifiedSource()
    {
        var h = new Harness();
        h.Store.ClaimResult = h.Operation;
        h.Store.Admission = Admit(h, []);

        Assert.Null(await h.Service.ClaimForAgentAsync(Agent, default));
        Assert.Equal(("fail", ReplicaRepairFailureCodes.NoVerifiedSourceAvailable), (h.Store.Terminal!.Kind, h.Store.Terminal.Code));
    }

    [Fact]
    public async Task Claim_WhenStoreHasNothingForTheAgent_ReturnsNullWithoutAdmission()
    {
        var h = new Harness();
        h.Store.ClaimResult = null;

        Assert.Null(await h.Service.ClaimForAgentAsync(Agent, default));
        Assert.Null(h.Store.AdmittedExcluding);
    }

    [Fact]
    public async Task Complete_Success_PublishesTheVerifiedReplicaOfTheOperationTarget()
    {
        var h = new Harness();

        var result = await h.CompleteAsync(new() { Outcome = ReplicaRepairOutcomes.Succeeded, VerificationMethod = "ServerSideChecksum" });

        Assert.Equal(BackupMutationResult.Accepted, result);
        var replica = Assert.IsType<BackupReplica>(h.Store.CompletedReplica);
        Assert.Equal("sftp", replica.StorageProvider);
        Assert.Equal(h.TargetConnection.ToString("D"), replica.StorageTarget);
        Assert.Equal(h.TargetConnection, replica.StorageConnectionId);
        Assert.Equal(h.Operation.TargetKey, replica.Key);
        Assert.Equal(h.Artifact.SizeBytes, replica.SizeBytes);
        Assert.Equal(h.Artifact.ArtifactChecksum, replica.Checksum);
        Assert.Equal(h.Artifact.ChecksumAlgorithm, replica.ChecksumAlgorithm);
        Assert.Equal("Verified", replica.VerificationState);
        Assert.Equal("ServerSideChecksum", replica.VerificationMethod);
        Assert.Equal(BackupStorageExecutionLocation.Backend, replica.ExecutionLocation);
        Assert.Null(replica.OwningAgentId);
    }

    [Fact]
    public async Task Claim_ForALocalTarget_NamesTheVolume_AndHandsOverOnlyExternalSources()
    {
        var h = new Harness("local");
        var own = OwnLocal(h.Artifact, Agent.AgentId.Value);
        h.Store.ClaimResult = h.Operation;
        h.Store.Admission = new ReplicaRepairAdmissionResult(true, null, null, h.Artifact,
            new BackupDestination("local", h.TargetConnection) { StorageTarget = "usb-disk" }, h.Source, [own, NewReplica(h.Artifact, "central", "c.bak"), h.Source]);

        var snapshot = await h.Service.ClaimForAgentAsync(Agent, default);

        Assert.Equal(("local", h.TargetConnection, "usb-disk"), (snapshot!.Target.StorageProvider, snapshot.Target.StorageConnectionId, snapshot.Target.StorageTarget));
        Assert.Equal(h.Source.Id, Assert.Single(snapshot.Sources).ReplicaId);
    }

    [Fact]
    public async Task Complete_Success_OnALocalTarget_PublishesAnAgentReplicaOnTheVolumeOfTheRunSnapshot()
    {
        var h = new Harness("local");
        h.Store.RunDestination = new BackupDestination("local", h.TargetConnection) { StorageTarget = "usb-disk" };

        var result = await h.CompleteAsync(new() { Outcome = ReplicaRepairOutcomes.Succeeded, VerificationMethod = "ReadBack" });

        Assert.Equal(BackupMutationResult.Accepted, result);
        var replica = Assert.IsType<BackupReplica>(h.Store.CompletedReplica);
        Assert.Equal(("local", "usb-disk", h.TargetConnection), (replica.StorageProvider, replica.StorageTarget, replica.StorageConnectionId));
        Assert.Equal(BackupStorageExecutionLocation.Agent, replica.ExecutionLocation);
        Assert.Equal(Agent.AgentId.Value, replica.OwningAgentId);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("Bogus")]
    [InlineData("99")]
    public async Task Complete_Success_RejectsAnUnknownVerificationMethod(string? method)
    {
        var h = new Harness();

        await Assert.ThrowsAsync<ArgumentException>(() => h.CompleteAsync(new() { Outcome = ReplicaRepairOutcomes.Succeeded, VerificationMethod = method }));
        Assert.Null(h.Store.CompletedReplica);
    }

    [Fact]
    public async Task Complete_Success_WithoutPinnedTarget_IsRefused()
    {
        var h = new Harness(pinned: false);

        var result = await h.CompleteAsync(new() { Outcome = ReplicaRepairOutcomes.Succeeded, VerificationMethod = "ReadBack" });

        Assert.Equal(BackupMutationResult.Conflict, result);
        Assert.Null(h.Store.CompletedReplica);
    }

    [Fact]
    public async Task Complete_Success_OnDrive_RequiresItsRecord_AndSavesItBeforePublishing()
    {
        var h = new Harness(targetProvider: "google-drive");
        await Assert.ThrowsAsync<ArgumentException>(() => h.CompleteAsync(new() { Outcome = ReplicaRepairOutcomes.Succeeded, VerificationMethod = "ReadBack" }));
        Assert.Null(h.Store.CompletedReplica);

        var order = new List<string>();
        h.Store.OnComplete = () => order.Add("complete");
        var record = new TaskStorageCatalogRecord("file-1", "folder-1", "x.bak", Size, Checksum, null);
        var result = await h.CompleteAsync(new() { Outcome = ReplicaRepairOutcomes.Succeeded, VerificationMethod = "ReadBack", DriveRecord = record },
            (connection, key, saved, _) =>
            {
                Assert.Equal(h.TargetConnection, connection);
                Assert.Equal(h.Operation.TargetKey, key);
                Assert.Same(record, saved);
                order.Add("save");
                return Task.CompletedTask;
            });

        Assert.Equal(BackupMutationResult.Accepted, result);
        Assert.Equal(["save", "complete"], order);
    }

    [Fact]
    public async Task Complete_Success_OnSftp_DoesNotTouchTheDriveCatalog()
    {
        var h = new Harness();
        var saved = false;

        await h.CompleteAsync(new() { Outcome = ReplicaRepairOutcomes.Succeeded, VerificationMethod = "ReadBack" }, (_, _, _, _) => { saved = true; return Task.CompletedTask; });

        Assert.False(saved);
    }

    [Fact]
    public async Task Complete_Failed_WithUnknownCode_IsRecordedAsInternalErrorAndTruncated()
    {
        var h = new Harness();

        var result = await h.CompleteAsync(new() { Outcome = ReplicaRepairOutcomes.Failed, FailureCode = "rm -rf", Reason = new string('x', 5000) });

        Assert.Equal(BackupMutationResult.Accepted, result);
        Assert.Equal(ReplicaRepairFailureCodes.InternalError, h.Store.Terminal!.Code);
        Assert.True(h.Store.Terminal.Reason!.Length <= 512);
    }

    [Theory]
    [InlineData(ReplicaRepairFailureCodes.StorageUnavailable, "retry")]
    [InlineData(ReplicaRepairFailureCodes.SourceChecksumMismatch, "fail")]
    public async Task Complete_Failed_RetriesOnlyTransientCodes(string code, string expected)
    {
        var h = new Harness();

        await h.CompleteAsync(new() { Outcome = ReplicaRepairOutcomes.Failed, FailureCode = code });

        Assert.Equal((expected, code), (h.Store.Terminal!.Kind, h.Store.Terminal.Code));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Complete_Failed_RecordsTheOrphanBeforeFailing(bool quarantine)
    {
        var h = new Harness();

        await h.CompleteAsync(new()
        {
            Outcome = ReplicaRepairOutcomes.Failed, FailureCode = ReplicaRepairFailureCodes.StorageWriteFailed,
            OrphanKey = h.Operation.TargetKey, OrphanQuarantine = quarantine
        });

        var orphan = Assert.Single(h.Store.Orphans);
        Assert.Equal((h.Operation.TargetKey, quarantine, "sftp", h.TargetConnection), (orphan.Key, orphan.Quarantine, orphan.Provider, orphan.Connection));
        Assert.Equal(h.Artifact.SizeBytes, orphan.Size);
        Assert.Equal(h.Artifact.ArtifactChecksum, orphan.Checksum);
        Assert.NotNull(h.Store.Terminal);
        Assert.True(h.Store.OrphanRecordedBeforeTerminal);
    }

    [Fact]
    public async Task Complete_Failed_RejectsAnOrphanKeyThatIsNotTheOperationTarget()
    {
        var h = new Harness();

        await Assert.ThrowsAsync<ArgumentException>(() => h.CompleteAsync(new()
        {
            Outcome = ReplicaRepairOutcomes.Failed, FailureCode = ReplicaRepairFailureCodes.StorageWriteFailed, OrphanKey = h.Source.Key
        }));
        Assert.Empty(h.Store.Orphans);
        Assert.Null(h.Store.Terminal);
    }

    [Fact]
    public async Task Complete_Failed_AcceptsAPartialSiblingOfTheTargetAsOrphan()
    {
        var h = new Harness();
        var slash = h.Operation.TargetKey!.LastIndexOf('/');
        var partial = $"{h.Operation.TargetKey[..slash]}/.{h.Operation.TargetKey[(slash + 1)..]}.{Guid.NewGuid():N}.partial";

        await h.CompleteAsync(new() { Outcome = ReplicaRepairOutcomes.Failed, FailureCode = ReplicaRepairFailureCodes.StorageWriteFailed, OrphanKey = partial });

        Assert.Equal(partial, Assert.Single(h.Store.Orphans).Key);
    }

    [Fact]
    public async Task Complete_Cancelled_RecordsOrphanThenCancels()
    {
        var h = new Harness();

        var result = await h.CompleteAsync(new() { Outcome = ReplicaRepairOutcomes.Cancelled, OrphanKey = h.Operation.TargetKey });

        Assert.Equal(BackupMutationResult.Accepted, result);
        Assert.Single(h.Store.Orphans);
        Assert.Equal("cancel", h.Store.Terminal!.Kind);
        Assert.True(h.Store.OrphanRecordedBeforeTerminal);
    }

    [Fact]
    public async Task Complete_TargetConflict_DelegatesToTheQuarantiningStoreOperation()
    {
        var h = new Harness();

        var result = await h.CompleteAsync(new() { Outcome = ReplicaRepairOutcomes.TargetConflict });

        Assert.Equal(BackupMutationResult.Accepted, result);
        Assert.Equal(("target_conflict", ReplicaRepairFailureCodes.TargetConflict), (h.Store.Terminal!.Kind, h.Store.Terminal.Code));
    }

    [Fact]
    public async Task Complete_UnknownOutcome_IsInvalid()
    {
        var h = new Harness();

        await Assert.ThrowsAsync<ArgumentException>(() => h.CompleteAsync(new() { Outcome = "succeeded-ish" }));
    }

    [Fact]
    public async Task Complete_FromAnotherAgentOrWithAnotherLease_IsConflictAndChangesNothing()
    {
        var h = new Harness();
        var otherAgent = new AgentIdentity(AgentId.New(), Tenant, Guid.NewGuid(), 1);
        var request = new ReplicaRepairCompletionRequest { LeaseToken = h.LeaseToken, Outcome = ReplicaRepairOutcomes.Succeeded, VerificationMethod = "ReadBack" };

        Assert.Equal(BackupMutationResult.Conflict, await h.Service.CompleteForAgentAsync(otherAgent, h.Operation.Id, request, (_, _, _, _) => Task.CompletedTask, default));
        Assert.Equal(BackupMutationResult.Conflict, await h.Service.CompleteForAgentAsync(Agent, h.Operation.Id, request with { LeaseToken = Guid.NewGuid() }, (_, _, _, _) => Task.CompletedTask, default));
        Assert.Null(h.Store.CompletedReplica);
        Assert.Null(h.Store.Terminal);
    }

    [Fact]
    public async Task Complete_AfterTheLeaseExpired_IsConflict()
    {
        var h = new Harness();
        h.Time.Advance(TimeSpan.FromMinutes(11));

        Assert.Equal(BackupMutationResult.Conflict, await h.CompleteAsync(new() { Outcome = ReplicaRepairOutcomes.Succeeded, VerificationMethod = "ReadBack" }));
    }

    [Fact]
    public async Task Pin_AcceptsOnlyTheLeaseSpecificKeyNextToTheSource()
    {
        var h = new Harness(pinned: false);
        var good = h.TargetKeyFor(h.Source);

        await Assert.ThrowsAsync<ArgumentException>(() => h.Service.PinForAgentAsync(Agent, h.Operation.Id,
            new() { LeaseToken = h.LeaseToken, SourceReplicaId = h.Source.Id, TargetKey = "somewhere/else.bak" }, default));
        await Assert.ThrowsAsync<ArgumentException>(() => h.Service.PinForAgentAsync(Agent, h.Operation.Id,
            new() { LeaseToken = h.LeaseToken, SourceReplicaId = h.Source.Id, TargetKey = good.Replace(ManagedBackupLayout.RepairFileName(h.Operation.Id, h.LeaseToken), "other.bak") }, default));
        Assert.Null(h.Store.PinnedKey);

        var result = await h.Service.PinForAgentAsync(Agent, h.Operation.Id,
            new() { LeaseToken = h.LeaseToken, SourceReplicaId = h.Source.Id, TargetKey = good }, default);

        Assert.Equal(BackupMutationResult.Accepted, result);
        Assert.Equal(good, h.Store.PinnedKey);
    }

    [Fact]
    public async Task Pin_RefusesACentralSourceAndAReplicaOfAnotherArtifact()
    {
        var h = new Harness(pinned: false);
        var central = NewReplica(h.Artifact, "central", "c.bak");
        h.Artifact.AddReplica(central);
        var foreign = NewReplica(NewArtifact(), "sftp", "f.bak");

        foreach (var source in new[] { central, foreign })
            Assert.Equal(BackupMutationResult.Conflict, await h.Service.PinForAgentAsync(Agent, h.Operation.Id,
                new() { LeaseToken = h.LeaseToken, SourceReplicaId = source.Id, TargetKey = h.TargetKeyFor(source) }, default));
        Assert.Null(h.Store.PinnedKey);
    }

    [Fact]
    public async Task Access_TargetIsTheOperationConnection_SourceNeedsAPin()
    {
        var unpinned = new Harness(pinned: false);
        var pinned = new Harness();

        Assert.Equal(new ReplicaRepairAccessTarget(unpinned.TargetConnection, "sftp"),
            await unpinned.Service.ResolveAccessAsync(Agent, unpinned.Operation.Id, unpinned.LeaseToken, source: false, default));
        Assert.Null(await unpinned.Service.ResolveAccessAsync(Agent, unpinned.Operation.Id, unpinned.LeaseToken, source: true, default));
        Assert.Equal(new ReplicaRepairAccessTarget(pinned.Source.StorageConnectionId, "sftp"),
            await pinned.Service.ResolveAccessAsync(Agent, pinned.Operation.Id, pinned.LeaseToken, source: true, default));
    }

    [Fact]
    public async Task Access_IsRefusedForAnotherAgentWrongLeaseOrExpiredLease()
    {
        var h = new Harness();
        var otherAgent = new AgentIdentity(AgentId.New(), Tenant, Guid.NewGuid(), 1);

        Assert.Null(await h.Service.ResolveAccessAsync(otherAgent, h.Operation.Id, h.LeaseToken, false, default));
        Assert.Null(await h.Service.ResolveAccessAsync(Agent, h.Operation.Id, Guid.NewGuid(), false, default));
        h.Time.Advance(TimeSpan.FromMinutes(11));
        Assert.Null(await h.Service.ResolveAccessAsync(Agent, h.Operation.Id, h.LeaseToken, false, default));
    }

    [Fact]
    public async Task OldLeaseTargetCannotBeAdoptedByAReplacementLease()
    {
        var h = new Harness();
        h.Operation.SetTargetIntent(h.Source.Id, h.TargetKeyFor(h.Source).Replace(h.LeaseToken.ToString("N"), Guid.NewGuid().ToString("N")));

        Assert.Equal(BackupMutationResult.Conflict, await h.CompleteAsync(new() { Outcome = ReplicaRepairOutcomes.Succeeded, VerificationMethod = "ReadBack" }));
        Assert.Null(await h.Service.ResolveAccessAsync(Agent, h.Operation.Id, h.LeaseToken, true, default));
        Assert.Null(h.Store.CompletedReplica);
    }

    private static ReplicaRepairAdmissionResult Admit(Harness h, IReadOnlyList<BackupReplica> candidates) =>
        new(true, null, null, h.Artifact, new BackupDestination(h.Operation.StorageProvider, h.TargetConnection), candidates.FirstOrDefault(), candidates);

    private sealed record Terminal(string Kind, string? Code, string? Reason);

    private sealed record Orphan(string Provider, Guid Connection, string Key, long Size, string Checksum, bool Quarantine);

    private sealed class FakeStore : IReplicaRepairStore
    {
        public ReplicaRepairOperation? Operation;
        public BackupArtifact? Artifact;
        public ReplicaRepairOperation? ClaimResult;
        public ReplicaRepairAdmissionResult? Admission;
        public Guid? AdmittedExcluding;
        public string? PinnedKey;
        public BackupReplica? CompletedReplica;
        public Action? OnComplete;
        public Terminal? Terminal;
        public readonly List<Orphan> Orphans = [];
        public bool OrphanRecordedBeforeTerminal;

        public Task<ReplicaRepairOperation?> ClaimAgentRepairAsync(AgentIdentity identity, DateTimeOffset now, TimeSpan leaseDuration, CancellationToken cancellationToken) =>
            Task.FromResult(ClaimResult);

        public Task<ReplicaRepairAdmissionResult> AdmitRepairAsync(TenantId tenantId, Guid artifactId, Guid? storageConnectionId, string? storageProvider, DateTimeOffset now, CancellationToken cancellationToken, Guid? excludeOperationId = null)
        {
            AdmittedExcluding = excludeOperationId;
            return Task.FromResult(Admission!);
        }

        public Task<ReplicaRepairOperation?> FindOperationAsync(TenantId tenantId, Guid operationId, CancellationToken cancellationToken) =>
            Task.FromResult(Operation is not null && Operation.TenantId == tenantId && Operation.Id == operationId ? Operation : null);

        public Task<BackupArtifact?> FindArtifactAsync(TenantId tenantId, Guid artifactId, CancellationToken cancellationToken) =>
            Task.FromResult(Artifact is not null && Artifact.TenantId == tenantId && Artifact.Id == artifactId ? Artifact : null);

        public BackupDestination? RunDestination;

        public Task<BackupDestination?> FindRunDestinationAsync(TenantId tenantId, Guid artifactId, Guid storageConnectionId, CancellationToken cancellationToken) =>
            Task.FromResult(RunDestination);

        public Task<bool> PinSourceAsync(TenantId tenantId, Guid operationId, Guid leaseToken, Guid sourceReplicaId, string targetKey, DateTimeOffset now, CancellationToken cancellationToken)
        {
            PinnedKey = targetKey;
            return Task.FromResult(true);
        }

        public Task<BackupReplica> CompleteOperationAsync(TenantId tenantId, Guid operationId, Guid leaseToken, BackupReplica newReplica, DateTimeOffset now, CancellationToken cancellationToken)
        {
            OnComplete?.Invoke();
            CompletedReplica = newReplica;
            return Task.FromResult(newReplica);
        }

        public Task RecordRepairOrphanAsync(TenantId tenantId, Guid artifactId, Guid storageConnectionId, string storageProvider, string storageTarget, string key, long sizeBytes, string checksum, DateTimeOffset now, CancellationToken cancellationToken, bool quarantine = false)
        {
            Orphans.Add(new(storageProvider, storageConnectionId, key, sizeBytes, checksum, quarantine));
            return Task.CompletedTask;
        }

        public Task<bool> FailOperationAsync(TenantId tenantId, Guid operationId, Guid leaseToken, string failureCode, string? reason, DateTimeOffset now, CancellationToken cancellationToken) =>
            End("fail", failureCode, reason);

        public Task<bool> FailTargetConflictAsync(TenantId tenantId, Guid operationId, Guid leaseToken, DateTimeOffset now, CancellationToken cancellationToken) =>
            End("target_conflict", ReplicaRepairFailureCodes.TargetConflict, null);

        public Task<bool> ScheduleRetryAsync(TenantId tenantId, Guid operationId, Guid leaseToken, string failureCode, string? reason, DateTimeOffset nextRetryAt, DateTimeOffset now, CancellationToken cancellationToken) =>
            End("retry", failureCode, reason);

        public Task<bool> CancelOperationAsync(TenantId tenantId, Guid operationId, Guid leaseToken, DateTimeOffset now, CancellationToken cancellationToken) =>
            End("cancel", null, null);

        private Task<bool> End(string kind, string? code, string? reason)
        {
            OrphanRecordedBeforeTerminal = Orphans.Count > 0;
            Terminal = new(kind, code, reason);
            return Task.FromResult(true);
        }

        public Task<ReplicaRepairAdmissionResult> CreateOperationAsync(ReplicaRepairOperation operation, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<ReplicaRepairOperation?> ClaimOperationAsync(TenantId tenantId, Guid operationId, Guid leaseToken, string leaseOwner, DateTimeOffset now, TimeSpan leaseDuration, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<bool> CanCleanupTargetAsync(TenantId tenantId, Guid operationId, Guid leaseToken, string targetKey, DateTimeOffset now, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<ReplicaRepairLeaseRenewal> RenewLeaseAsync(TenantId tenantId, Guid operationId, Guid leaseToken, DateTimeOffset now, TimeSpan leaseDuration, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<bool> UpdateProgressAsync(TenantId tenantId, Guid operationId, Guid leaseToken, long transferredBytes, long totalBytes, DateTimeOffset now, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<ReplicaRepairOperation?> ClaimDueRepairAsync(string leaseOwner, DateTimeOffset now, TimeSpan leaseDuration, CancellationToken cancellationToken, TenantId? tenantId = null, bool backendExecutedOnly = false) => throw new NotSupportedException();
        public Task<int> RecoverStaleRepairsAsync(DateTimeOffset now, TimeSpan initialRetryDelay, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<IReadOnlyList<ReplicaRepairOperation>> DiscoverEligibleRepairsAsync(DateTimeOffset now, int maxBatch, CancellationToken cancellationToken, TenantId? tenantId = null) => throw new NotSupportedException();
        public Task<bool> RequestCancellationAsync(TenantId tenantId, Guid artifactId, Guid operationId, DateTimeOffset now, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<ReplicaRepairOperationView?> GetOperationAsync(TenantId tenantId, Guid artifactId, Guid operationId, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<IReadOnlyList<ReplicaRepairOperationView>> ListOperationsAsync(TenantId tenantId, Guid artifactId, CancellationToken cancellationToken) => throw new NotSupportedException();
    }
}
