using BackupPlatform.Application.Agents;
using BackupPlatform.Application.Backups;
using BackupPlatform.Application.Storage;
using BackupPlatform.Contracts.Backups;
using BackupPlatform.Domain.Backups;
using BackupCompletion = BackupPlatform.Application.Backups.BackupCompletion;
using BackupPlatform.Domain.Identity;
using static BackupPlatform.UnitTests.BackupRunTestSupport;

namespace BackupPlatform.UnitTests.Backup;

public sealed class ArtifactEncryptionPolicyTests
{
    private const string KeyId = "0123456789abcdef0123456789abcdef";
    private const string PlatformKeyId = "aaaaaaaaaaaaaaaabbbbbbbbbbbbbbbb";
    private const string TenantKeyId = "ccccccccccccccccdddddddddddddddd";
    private static readonly string Wrapped = Convert.ToBase64String(new byte[512]);
    private static readonly byte[] Spki = [1, 2, 3, 4];
    private static readonly byte[] PlatformSpki = [5, 6, 7];
    private static readonly byte[] TenantSpki = [8, 9];

    private static ArtifactRecoveryOptions Platform() => new(new PlatformRecoveryKey(3, PlatformKeyId, PlatformSpki));

    private static TenantRecoveryKeyInfo TenantKey() => new(2, TenantKeyId, TenantSpki, Now);

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
    public async Task OptionalAndRequiredSendOnlyTheTenantWrapKeyWhenNoRecoveryKeyExists(string policy, bool required)
    {
        var (orchestrator, keys, identity) = Setup(policy);
        var instruction = (await orchestrator.ClaimAsync(identity, default))!.Encryption!;
        Assert.Equal(required, instruction.Required);
        var recipient = Assert.Single(instruction.Recipients);
        Assert.Equal((BackupRecipientTypes.TenantWrap, 7, KeyId, Convert.ToBase64String(Spki)), (recipient.Type, recipient.KeyVersion, recipient.KeyId, recipient.PublicKeySpkiBase64));
        Assert.Equal(1, keys.Ensured);
    }

    [Fact]
    public async Task ThePlatformAndTenantRecoveryKeysTravelAsPublicRecipientsNextToTheTenantWrapKey()
    {
        var (orchestrator, _, identity) = Setup("Required", Platform(), TenantKey());
        var instruction = (await orchestrator.ClaimAsync(identity, default))!.Encryption!;
        Assert.Equal([(BackupRecipientTypes.TenantWrap, 7, KeyId), (BackupRecipientTypes.PlatformRecovery, 3, PlatformKeyId), (BackupRecipientTypes.TenantRecovery, 2, TenantKeyId)],
            instruction.Recipients.Select(r => (r.Type, r.KeyVersion, r.KeyId)));
        Assert.Equal(Convert.ToBase64String(PlatformSpki), instruction.Recipients[1].PublicKeySpkiBase64);
        Assert.Equal(Convert.ToBase64String(TenantSpki), instruction.Recipients[2].PublicKeySpkiBase64);
    }

    [Fact]
    public async Task OffNeverAddsRecoveryRecipients()
    {
        var (orchestrator, _, identity) = Setup("Off", Platform(), TenantKey());
        Assert.Null((await orchestrator.ClaimAsync(identity, default))!.Encryption);
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
    public async Task EncryptedArtifactCarriesKeyIdVersionWrappedDekAndTheRecipientIndexIntoTheStore()
    {
        var (orchestrator, _, identity, store) = SetupWithStore("Required", Platform(), TenantKey());
        var recipients = new[] { Wrap, Recipient(BackupRecipientTypes.PlatformRecovery, PlatformKeyId, 3), Recipient(BackupRecipientTypes.TenantRecovery, TenantKeyId, 2) };
        await orchestrator.CompleteAsync(identity, store.Run.CurrentAttempt.Id, Success(ArtifactFormats.Bpe2, recipients), default);
        var artifact = store.Completed!.Artifact!;
        Assert.Equal(("Succeeded", ArtifactFormats.Bpe2, KeyId, 7, Wrapped), (store.Completed.Outcome, artifact.Format, artifact.EncryptionKeyId, artifact.EncryptionKeyVersion, artifact.EncryptionWrappedDekBase64));
        var index = System.Text.Json.JsonDocument.Parse(artifact.EncryptionRecipientsJson!).RootElement.EnumerateArray().Select(e => (e.GetProperty("type").GetString(), e.GetProperty("keyId").GetString(), e.GetProperty("keyVersion").GetInt32())).ToArray();
        Assert.Equal([("tenant-wrap", KeyId, 7), ("platform-recovery", PlatformKeyId, 3), ("tenant-recovery", TenantKeyId, 2)], index);
        Assert.DoesNotContain(Wrapped, artifact.EncryptionRecipientsJson);
    }

    [Fact]
    public async Task RecoveryRecipientsAreOnlyAcceptedAsTheKeysTheBackendIssues()
    {
        // platform recovery key not configured, a recipient claims one
        var (orchestrator, _, identity, store) = SetupWithStore("Required");
        await Assert.ThrowsAsync<ArgumentException>(() => orchestrator.CompleteAsync(identity, store.Run.CurrentAttempt.Id,
            Success(ArtifactFormats.Bpe2, Wrap, Recipient(BackupRecipientTypes.PlatformRecovery, PlatformKeyId, 1)), default));
        // configured, but the agent reports another key id or version
        (orchestrator, _, identity, store) = SetupWithStore("Required", Platform());
        await Assert.ThrowsAsync<ArgumentException>(() => orchestrator.CompleteAsync(identity, store.Run.CurrentAttempt.Id,
            Success(ArtifactFormats.Bpe2, Wrap, Recipient(BackupRecipientTypes.PlatformRecovery, TenantKeyId, 1)), default));
        await Assert.ThrowsAsync<ArgumentException>(() => orchestrator.CompleteAsync(identity, store.Run.CurrentAttempt.Id,
            Success(ArtifactFormats.Bpe2, Wrap, Recipient(BackupRecipientTypes.PlatformRecovery, PlatformKeyId, 9)), default));
        // the tenant has no recovery key, or another one
        await Assert.ThrowsAsync<ArgumentException>(() => orchestrator.CompleteAsync(identity, store.Run.CurrentAttempt.Id,
            Success(ArtifactFormats.Bpe2, Wrap, Recipient(BackupRecipientTypes.TenantRecovery, TenantKeyId, 2)), default));
        (orchestrator, _, identity, store) = SetupWithStore("Required", Platform(), TenantKey());
        await Assert.ThrowsAsync<ArgumentException>(() => orchestrator.CompleteAsync(identity, store.Run.CurrentAttempt.Id,
            Success(ArtifactFormats.Bpe2, Wrap, Recipient(BackupRecipientTypes.TenantRecovery, PlatformKeyId, 2)), default));
        Assert.Null(store.Completed);
    }
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ConfiguredRecoveryRecipientsCannotBeOmittedFromTheArtifactIndex(bool omitPlatform)
    {
        var (orchestrator, _, identity, store) = SetupWithStore("Required", Platform(), TenantKey());
        var remaining = omitPlatform ? Recipient(BackupRecipientTypes.TenantRecovery, TenantKeyId, 2) : Recipient(BackupRecipientTypes.PlatformRecovery, PlatformKeyId, 3);
        await Assert.ThrowsAsync<ArgumentException>(() => orchestrator.CompleteAsync(identity, store.Run.CurrentAttempt.Id,
            Success(ArtifactFormats.Bpe2, Wrap, remaining), default));
        Assert.Null(store.Completed);
    }


    [Theory]
    [InlineData("duplicate-type")]
    [InlineData("no-tenant-wrap")]
    [InlineData("wrap-differs-from-artifact-key")]
    [InlineData("unknown-type")]
    [InlineData("bad-key-id")]
    [InlineData("too-many")]
    public async Task MalformedRecipientIndexesAreRejected(string kind)
    {
        var (orchestrator, _, identity, store) = SetupWithStore("Required", Platform(), TenantKey());
        var recipients = kind switch
        {
            "duplicate-type" => new[] { Wrap, Wrap },
            "no-tenant-wrap" => [Recipient(BackupRecipientTypes.PlatformRecovery, PlatformKeyId, 1)],
            "wrap-differs-from-artifact-key" => [Recipient(BackupRecipientTypes.TenantWrap, PlatformKeyId, 7)],
            "unknown-type" => [Wrap, Recipient("escrow", PlatformKeyId, 1)],
            "bad-key-id" => [Wrap, Recipient(BackupRecipientTypes.PlatformRecovery, "XYZ", 1)],
            _ => [Wrap, Recipient(BackupRecipientTypes.PlatformRecovery, PlatformKeyId, 1), Recipient(BackupRecipientTypes.TenantRecovery, TenantKeyId, 2), Recipient(BackupRecipientTypes.TenantRecovery, TenantKeyId, 3)]
        };
        await Assert.ThrowsAsync<ArgumentException>(() => orchestrator.CompleteAsync(identity, store.Run.CurrentAttempt.Id, Success(ArtifactFormats.Bpe2, recipients), default));
        Assert.Null(store.Completed);
    }

    [Fact]
    public async Task APlainArtifactMustNotCarryARecipientIndex()
    {
        var (orchestrator, _, identity, store) = SetupWithStore("Off");
        var request = Success(ArtifactFormats.Plain);
        request = request with { Artifact = request.Artifact! with { EncryptionRecipients = [Wrap] } };
        await Assert.ThrowsAsync<ArgumentException>(() => orchestrator.CompleteAsync(identity, store.Run.CurrentAttempt.Id, request, default));
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
    public async Task CompleteAsync_EnqueuesCapacityProbe_ForDestinationConnections()
    {
        var connId = Guid.NewGuid();
        var trigger = new FakeCapacityProbeTrigger();
        var destinations = new[] { new BackupDestination("local", connId, true) { StorageExecutionLocation = BackupStorageExecutionLocation.Backend } };
        var (orchestrator, _, identity, store) = SetupWithStore("Off", probeTrigger: trigger, destinations: destinations);

        var result = await orchestrator.CompleteAsync(identity, store.Run.CurrentAttempt.Id, Success(ArtifactFormats.Plain), default);
        Assert.Equal(BackupMutationResult.Accepted, result);
        Assert.Contains((store.Run.TenantId, connId), trigger.Enqueued);
    }

    private sealed class FakeCapacityProbeTrigger : IStorageCapacityProbeTrigger
    {
        public List<(TenantId TenantId, Guid ConnectionId)> Enqueued { get; } = [];
        public void EnqueueProbe(TenantId tenantId, Guid connectionId) => Enqueued.Add((tenantId, connectionId));
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

    private static (BackupOrchestrator Orchestrator, Keys Keys, AgentIdentity Identity) Setup(string policy, ArtifactRecoveryOptions? recovery = null, TenantRecoveryKeyInfo? tenantKey = null)
    {
        var (orchestrator, keys, identity, _) = SetupWithStore(policy, recovery, tenantKey);
        return (orchestrator, keys, identity);
    }

    private static (BackupOrchestrator Orchestrator, Keys Keys, AgentIdentity Identity, Store Store) SetupWithStore(string policy, ArtifactRecoveryOptions? recovery = null,
        TenantRecoveryKeyInfo? tenantKey = null, IStorageCapacityProbeTrigger? probeTrigger = null, IReadOnlyList<BackupDestination>? destinations = null)
    {
        var run = RunWith(policy, destinations);
        var keys = new Keys();
        var store = new Store(run);
        return (new BackupOrchestrator(store, TimeProvider.System, wrapKeys: keys, recovery: recovery, tenantRecoveryKeys: new TenantRecovery(tenantKey), probeTrigger: probeTrigger), keys, Identity(run), store);
    }

    private static AgentIdentity Identity(BackupRun run) => new(run.AgentId, run.TenantId, Guid.NewGuid(), 1);

    private static BackupRun RunWith(string policy, IReadOnlyList<BackupDestination>? destinations = null)
    {
        var config = Configuration() with { EncryptionPolicy = policy, Destinations = destinations ?? Configuration().Destinations };
        var run = new BackupRun(Guid.NewGuid(), TenantId.New(), Guid.NewGuid(), AgentId.New(), config, "key", Now, Policy(), BackupOverlapPolicy.Skip);
        run.QueueFirstAttempt(Guid.NewGuid(), Now);
        Begin(run, Now);
        return run;
    }

    private static BackupTaskArtifactRecipient Recipient(string type, string keyId, int version) => new() { Type = type, KeyId = keyId, KeyVersion = version };

    private static readonly BackupTaskArtifactRecipient Wrap = Recipient(BackupRecipientTypes.TenantWrap, KeyId, 7);

    private static BackupTaskCompletionRequest Success(string format, params BackupTaskArtifactRecipient[] recipients) => new()
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
            EncryptionRecipients = format == ArtifactFormats.Bpe2 ? (recipients.Length == 0 ? [Wrap] : recipients) : [],
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
        public Task<byte[]> UnwrapDekAsync(TenantId tenantId, string targetType, Guid targetId, string keyId, byte[] wrappedDek, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<IReadOnlyList<TenantWrapKeyView>> ListAsync(TenantId tenantId, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<TenantWrapKeyRotation> RotateAsync(TenantId tenantId, UserId actor, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<TenantWrapKeyRetirement> RetireAsync(TenantId tenantId, int version, UserId actor, CancellationToken cancellationToken) => throw new NotSupportedException();
    }

    private sealed class TenantRecovery(TenantRecoveryKeyInfo? key) : ITenantRecoveryKeyStore
    {
        public Task<TenantRecoveryKeyInfo?> GetAsync(TenantId tenantId, CancellationToken cancellationToken) => Task.FromResult(key);
        public Task<TenantRecoveryKeyInfo> SetAsync(TenantId tenantId, UserId actor, string publicKeyPem, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<bool> RemoveAsync(TenantId tenantId, UserId actor, CancellationToken cancellationToken) => throw new NotSupportedException();
    }

    private sealed class Store(BackupRun run) : IBackupRunStore
    {
        public BackupRun Run => run;
        public BackupCompletion? Completed;
        public Task<IReadOnlyList<TransferCheckpoint>> GetCheckpointsAsync(TenantId tenantId, Guid attemptId, CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<TransferCheckpoint>>([]);
        public Task<TenantId?> FindRunTenantAsync(Guid runId, CancellationToken cancellationToken) => Task.FromResult<TenantId?>(run.Id == runId ? run.TenantId : null);
        public Task<BackupMutationResult> SetPauseAsync(TenantId tenantId, Guid runId, bool resume, DateTimeOffset now, CancellationToken cancellationToken, UserId? actor = null) => throw new NotSupportedException();
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
