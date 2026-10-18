using BackupPlatform.Application.Agents;
using BackupPlatform.Application.Backups;
using BackupPlatform.Contracts.Backups;
using BackupPlatform.Domain.Backups;
using BackupCompletion = BackupPlatform.Application.Backups.BackupCompletion;
using BackupPlatform.Domain.Identity;
using static BackupPlatform.UnitTests.BackupRunTestSupport;

namespace BackupPlatform.UnitTests.Backup;

public sealed class ArtifactEncryptionPolicyTests
{
    private const string KeyId = "0123456789abcdef0123456789abcdef";
    private static readonly string Wrapped = Convert.ToBase64String(new byte[512]);
    private static readonly byte[] Spki = [1, 2, 3, 4];

    [Fact]
    public async Task OffSendsNoInstructionAndNeverTouchesTheKeyStore()
    {
        var (orchestrator, keys, identity) = Setup("Off");
        Assert.Null((await orchestrator.ClaimAsync(identity, default))!.Encryption);
        Assert.Equal(0, keys.Ensured);
    }

    [Theory]
    [InlineData("Optional", false)]
    [InlineData("Required", true)]
    public async Task OptionalAndRequiredSendThePublicKeyWithTheRequiredFlag(string policy, bool required)
    {
        var (orchestrator, keys, identity) = Setup(policy);
        var instruction = (await orchestrator.ClaimAsync(identity, default))!.Encryption!;
        Assert.Equal((required, 7, Convert.ToBase64String(Spki)), (instruction.Required, instruction.KeyVersion, instruction.PublicKeySpkiBase64));
        Assert.Equal(1, keys.Ensured);
    }

    [Fact]
    public async Task AnEncryptingPolicyWithoutAKeyStoreRefusesToHandOutWork()
    {
        var run = RunWith("Required");
        var orchestrator = new BackupOrchestrator(new Store(run), TimeProvider.System);
        await Assert.ThrowsAsync<InvalidOperationException>(() => orchestrator.ClaimAsync(Identity(run), default));
    }

    [Fact]
    public async Task RequiredJobWithAPlainArtifactFailsTheAttemptAndReportsTheCopiesAsOrphans()
    {
        var (orchestrator, _, identity, store) = SetupWithStore("Required");
        await orchestrator.CompleteAsync(identity, store.Run.CurrentAttempt.Id, Success(ArtifactFormats.Plain), default);
        var completion = Assert.IsType<BackupCompletion>(store.Completed);
        Assert.Equal(("Failed", BackupFailureCodes.EncryptionRequired), (completion.Outcome, completion.ReasonCode));
        Assert.Empty(completion.Artifact!.Replicas);
        var destination = Assert.Single(completion.Destinations!);
        Assert.True(destination.ObjectMayRemain);
        Assert.Equal("Failed", destination.State);
    }

    [Theory]
    [InlineData("Off")]
    [InlineData("Optional")]
    public async Task PlainArtifactsOfNonRequiredJobsAreUntouched(string policy)
    {
        var (orchestrator, _, identity, store) = SetupWithStore(policy);
        await orchestrator.CompleteAsync(identity, store.Run.CurrentAttempt.Id, Success(ArtifactFormats.Plain), default);
        var completion = store.Completed!;
        Assert.Equal("Succeeded", completion.Outcome);
        Assert.Equal((BackupArtifact.PlainFormat, null), (completion.Artifact!.Format, completion.Artifact.EncryptionKeyId));
        Assert.Single(completion.Artifact.Replicas);
    }

    [Fact]
    public async Task EncryptedArtifactCarriesKeyIdVersionAndWrappedDekIntoTheStore()
    {
        var (orchestrator, _, identity, store) = SetupWithStore("Required");
        await orchestrator.CompleteAsync(identity, store.Run.CurrentAttempt.Id, Success(ArtifactFormats.Bpe2), default);
        var artifact = store.Completed!.Artifact!;
        Assert.Equal(("Succeeded", ArtifactFormats.Bpe2, KeyId, 7, Wrapped), (store.Completed.Outcome, artifact.Format, artifact.EncryptionKeyId, artifact.EncryptionKeyVersion, artifact.EncryptionWrappedDekBase64));
    }

    [Fact]
    public async Task EncryptedArtifactNamingAnotherTenantsKeyIsRejected()
    {
        var (orchestrator, keys, identity, store) = SetupWithStore("Required");
        keys.Owns = false;
        await Assert.ThrowsAsync<ArgumentException>(() => orchestrator.CompleteAsync(identity, store.Run.CurrentAttempt.Id, Success(ArtifactFormats.Bpe2), default));
        Assert.Null(store.Completed);
    }

    [Theory]
    [InlineData(null, 7, "AAAA")]
    [InlineData(KeyId, null, "AAAA")]
    [InlineData(KeyId, 7, null)]
    [InlineData("XYZ", 7, "AAAA")]
    public async Task EncryptedArtifactWithoutACompleteKeyReferenceIsRejected(string? keyId, int? version, string? wrapped)
    {
        var (orchestrator, _, identity, store) = SetupWithStore("Optional");
        var request = Success(ArtifactFormats.Bpe2);
        request = request with { Artifact = request.Artifact! with { EncryptionKeyId = keyId, EncryptionKeyVersion = version, EncryptionWrappedDekBase64 = wrapped } };
        await Assert.ThrowsAsync<ArgumentException>(() => orchestrator.CompleteAsync(identity, store.Run.CurrentAttempt.Id, request, default));
    }

    [Fact]
    public async Task PlainArtifactMustNotCarryKeyMaterial()
    {
        var (orchestrator, _, identity, store) = SetupWithStore("Off");
        var request = Success(ArtifactFormats.Plain);
        request = request with { Artifact = request.Artifact! with { EncryptionKeyId = KeyId } };
        await Assert.ThrowsAsync<ArgumentException>(() => orchestrator.CompleteAsync(identity, store.Run.CurrentAttempt.Id, request, default));
    }

    [Fact]
    public void PolicyDefaultsToOffAndIsValidatedAndPartOfEquality()
    {
        var off = Configuration();
        Assert.Equal("Off", off.EncryptionPolicy);
        Assert.NotEqual(off, off with { EncryptionPolicy = "Required" });
        Assert.Throws<ArgumentException>(() => (off with { EncryptionPolicy = "Always" }).Validate());
        Assert.Equal("Optional", BackupOrchestrator.ToWire(off with { EncryptionPolicy = "Optional" }).EncryptionPolicy);
    }

    [Fact]
    public void ConfigurationSavedBeforeEncryptionExistedDeserializesAsOff()
    {
        var json = System.Text.Json.JsonSerializer.Serialize(Configuration());
        var legacy = System.Text.Json.Nodes.JsonNode.Parse(json)!.AsObject();
        legacy.Remove("EncryptionPolicy");
        Assert.Equal("Off", System.Text.Json.JsonSerializer.Deserialize<BackupConfiguration>(legacy.ToJsonString())!.EncryptionPolicy);
    }

    [Fact]
    public void ArtifactsDefaultToPlainAndEncryptedOnesNeedTheirKeyReference()
    {
        var tenant = TenantId.New();
        var plain = new BackupArtifact(Guid.NewGuid(), tenant, Guid.NewGuid(), 1, new('a', 64), new('b', 64), "sha256", Now, "{}", "{}");
        Assert.Equal(BackupArtifact.PlainFormat, plain.Format);
        Assert.Throws<ArgumentException>(() => new BackupArtifact(Guid.NewGuid(), tenant, Guid.NewGuid(), 1, new('a', 64), new('b', 64), "sha256", Now, "{}", "{}", BackupArtifact.BpeFormat));
        Assert.Throws<ArgumentException>(() => new BackupArtifact(Guid.NewGuid(), tenant, Guid.NewGuid(), 1, new('a', 64), new('b', 64), "sha256", Now, "{}", "{}", "v3"));
        var sealedKey = new BackupArtifact(Guid.NewGuid(), tenant, Guid.NewGuid(), 1, new('a', 64), new('b', 64), "sha256", Now, "{}", "{}", BackupArtifact.BpeFormat, KeyId, 1, Wrapped);
        Assert.Equal((KeyId, 1), (sealedKey.EncryptionKeyId, sealedKey.EncryptionKeyVersion));
    }

    private static (BackupOrchestrator Orchestrator, Keys Keys, AgentIdentity Identity) Setup(string policy)
    {
        var (orchestrator, keys, identity, _) = SetupWithStore(policy);
        return (orchestrator, keys, identity);
    }

    private static (BackupOrchestrator Orchestrator, Keys Keys, AgentIdentity Identity, Store Store) SetupWithStore(string policy)
    {
        var run = RunWith(policy);
        var keys = new Keys();
        var store = new Store(run);
        return (new BackupOrchestrator(store, TimeProvider.System, wrapKeys: keys), keys, Identity(run), store);
    }

    private static AgentIdentity Identity(BackupRun run) => new(run.AgentId, run.TenantId, Guid.NewGuid(), 1);

    private static BackupRun RunWith(string policy)
    {
        var run = new BackupRun(Guid.NewGuid(), TenantId.New(), Guid.NewGuid(), AgentId.New(), Configuration() with { EncryptionPolicy = policy }, "key", Now, Policy(), BackupOverlapPolicy.Skip);
        run.QueueFirstAttempt(Guid.NewGuid(), Now);
        Begin(run, Now);
        return run;
    }

    private static BackupTaskCompletionRequest Success(string format) => new()
    {
        LeaseToken = Guid.NewGuid(),
        Status = "Succeeded",
        Artifact = new BackupTaskArtifact
        {
            SizeBytes = 100, SourceChecksum = new('a', 64), ArtifactChecksum = new('b', 64), CreatedAt = Now,
            SourceMetadataJson = $$$"""{"engine":"mssql","databaseName":"Database","backupType":"Full","backupStartedAt":"{{{Now:O}}}","backupFinishedAt":"{{{Now:O}}}","backupVerification":{"state":"Passed","method":"verifyonly"}}""",
            AppliedOptionsJson = """{"copyOnly":true,"checksum":true,"compression":true}""",
            Format = format,
            EncryptionKeyId = format == ArtifactFormats.Bpe2 ? KeyId : null,
            EncryptionKeyVersion = format == ArtifactFormats.Bpe2 ? 7 : null,
            EncryptionWrappedDekBase64 = format == ArtifactFormats.Bpe2 ? Wrapped : null,
            Replicas = [new BackupTaskReplica
            {
                StorageProvider = "local", StorageTarget = "default", Key = "tenant/job/run/attempt/backup.bak", SizeBytes = 100,
                Checksum = new('b', 64), ChecksumAlgorithm = "sha256", VerificationState = "Verified", VerificationMethod = "ReadBack", CreatedAt = Now, VerifiedAt = Now
            }]
        },
        Destinations = [new BackupTaskDestinationResult
        {
            StorageProvider = "local", State = "Verified", Upload = "Succeeded", Verification = "Verified", Key = "tenant/job/run/attempt/backup.bak",
            StartedAt = Now, CompletedAt = Now
        }]
    };

    private sealed class Keys : ITenantWrapKeyStore
    {
        public int Ensured;
        public bool Owns = true;
        public Task<TenantWrapPublicKey> EnsureActiveAsync(TenantId tenantId, CancellationToken cancellationToken)
        {
            Ensured++;
            return Task.FromResult(new TenantWrapPublicKey(7, KeyId, Spki));
        }
        public Task<bool> OwnsKeyAsync(TenantId tenantId, string keyId, int version, CancellationToken cancellationToken) => Task.FromResult(Owns);
        public Task<byte[]> UnwrapDekAsync(TenantId tenantId, Guid restoreAttemptId, string keyId, byte[] wrappedDek, CancellationToken cancellationToken) => throw new NotSupportedException();
    }

    private sealed class Store(BackupRun run) : IBackupRunStore
    {
        public BackupRun Run => run;
        public BackupCompletion? Completed;
        public Task<BackupClaim?> ClaimNextAsync(TenantId tenantId, AgentId agentId, DateTimeOffset now, TimeSpan leaseDuration, CancellationToken cancellationToken) =>
            Task.FromResult<BackupClaim?>(new BackupClaim(run, run.CurrentAttempt));
        public Task<BackupClaim?> GetAttemptAsync(TenantId tenantId, AgentId agentId, Guid attemptId, CancellationToken cancellationToken) =>
            Task.FromResult<BackupClaim?>(new BackupClaim(run, run.CurrentAttempt));
        public Task<BackupMutationResult> CompleteAttemptAsync(BackupLeaseCommand command, BackupCompletion completion, CancellationToken cancellationToken)
        {
            Completed = completion;
            return Task.FromResult(BackupMutationResult.Accepted);
        }
        public Task<BackupJob?> CreateJobAsync(BackupJob job, CancellationToken cancellationToken, UserId? actor = null) => throw new NotSupportedException();
        public Task<BackupJob?> GetJobAsync(TenantId tenantId, Guid jobId, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<BackupJob?> UpdateJobAsync(TenantId tenantId, Guid jobId, BackupJobUpdate update, CancellationToken cancellationToken, UserId? actor = null, DateTimeOffset? now = null) => throw new NotSupportedException();
        public Task<BackupStartResult> StartRunAsync(TenantId tenantId, Guid jobId, string idempotencyKey, DateTimeOffset now, CancellationToken cancellationToken, UserId? actor = null) => throw new NotSupportedException();
        public Task<IReadOnlyList<BackupRun>> ListRunsAsync(TenantId tenantId, Guid? jobId, int limit, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<BackupRun?> GetRunAsync(TenantId tenantId, Guid runId, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<BackupRun?> RequestCancellationAsync(TenantId tenantId, Guid runId, DateTimeOffset now, CancellationToken cancellationToken, UserId? actor = null) => throw new NotSupportedException();
        public Task<BackupRetryResult> RetryRunAsync(TenantId tenantId, Guid runId, DateTimeOffset now, CancellationToken cancellationToken, UserId? actor = null) => throw new NotSupportedException();
        public Task<BackupMutationResult> StartAttemptAsync(BackupLeaseCommand command, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<BackupLeaseResult?> RenewLeaseAsync(BackupLeaseCommand command, TimeSpan leaseDuration, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<BackupMutationResult> RecordTransferAsync(BackupLeaseCommand command, BackupTransferIntent transfer, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<BackupMutationResult> ReportProgressAsync(BackupLeaseCommand command, BackupProgress progress, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<int> ExpireStaleAsync(DateTimeOffset now, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<IReadOnlyList<BackupRetryStarted>> StartDueRetriesAsync(DateTimeOffset now, int limit, CancellationToken cancellationToken) => throw new NotSupportedException();
    }
}
