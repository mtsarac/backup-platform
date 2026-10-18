using System.Net;
using System.Security.Cryptography;
using BackupPlatform.Agent.Core.Backup;
using BackupPlatform.Agent.Core.Connectivity;
using BackupPlatform.Agent.Core.Restore;
using BackupPlatform.Agent.Core.Tasks;
using BackupPlatform.Agent.Transport;
using BackupPlatform.AgentTests.Support;
using BackupPlatform.BackupProviders.Abstractions.Database;
using BackupPlatform.Contracts.Backups;
using BackupPlatform.Encryption;

namespace BackupPlatform.AgentTests;

public sealed class ArtifactEncryptionTests
{
    private static readonly StagingArea Staging = StagingArea.SameHost(Path.GetTempPath());

    private static BackupEncryptionInstruction Instruction(RSA key, bool required = true, int version = 3) => new()
    {
        Required = required, KeyVersion = version, PublicKeySpkiBase64 = Convert.ToBase64String(key.ExportSubjectPublicKeyInfo())
    };

    private static async Task<(BackupTaskHarness Harness, BackupTaskSnapshot Task, BackupTaskRequestFactory Factory)> ArrangeAsync(BackupEncryptionInstruction? instruction)
    {
        var harness = new BackupTaskHarness();
        var task = harness.Envelope().Backup! with { Encryption = instruction };
        return (harness, task, new BackupTaskRequestFactory(harness.Credentials, [Staging]));
    }

    // ---- request factory -----------------------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task ATaskWithoutAnInstructionDoesNotEncrypt()
    {
        var (harness, task, factory) = await ArrangeAsync(null);
        await using var _ = harness;

        Assert.Null((await factory.CreateAsync(task, default)).Encryption);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task AValidInstructionBecomesOneTenantWrapRecipientWithTheRequiredFlag(bool required)
    {
        using var key = RSA.Create(3072);
        var (harness, task, factory) = await ArrangeAsync(Instruction(key, required, version: 3));
        await using var _ = harness;

        var encryption = (await factory.CreateAsync(task, default)).Encryption!;

        Assert.Equal(required, encryption.Required);
        var recipient = Assert.Single(encryption.Recipients);
        Assert.Equal(BpeRecipientType.TenantWrap, recipient.Type);
        Assert.Equal(3, recipient.KeyVersion);
        Assert.Equal(key.ExportSubjectPublicKeyInfo(), recipient.PublicKey.ExportSubjectPublicKeyInfo());
    }

    [Theory]
    [InlineData("empty")]
    [InlineData("not-base64")]
    [InlineData("not-spki")]
    [InlineData("rsa-2048")]
    [InlineData("ecdsa")]
    [InlineData("version-0")]
    public async Task AnUnusableKeyIsRefusedInsteadOfProducingAPlainArtifact(string kind)
    {
        using var rsa2048 = RSA.Create(2048);
        using var rsa3072 = RSA.Create(3072);
        using var ecdsa = ECDsa.Create();
        var instruction = kind switch
        {
            "empty" => new BackupEncryptionInstruction { Required = true, KeyVersion = 1 },
            "not-base64" => new BackupEncryptionInstruction { Required = true, KeyVersion = 1, PublicKeySpkiBase64 = "%%%" },
            "not-spki" => new BackupEncryptionInstruction { Required = true, KeyVersion = 1, PublicKeySpkiBase64 = Convert.ToBase64String(new byte[64]) },
            "rsa-2048" => Instruction(rsa2048),
            "ecdsa" => new BackupEncryptionInstruction { Required = true, KeyVersion = 1, PublicKeySpkiBase64 = Convert.ToBase64String(ecdsa.ExportSubjectPublicKeyInfo()) },
            _ => Instruction(rsa3072, version: 0)
        };
        var (harness, task, factory) = await ArrangeAsync(instruction);
        await using var _ = harness;

        await Assert.ThrowsAsync<BackupTaskEncryptionException>(() => factory.CreateAsync(task, default).AsTask());
    }

    [Fact]
    public async Task AnUnusableKeyFailsTheTaskAsInvalidConfigurationWithoutRunningTheBackup()
    {
        await using var harness = new BackupTaskHarness();
        var envelope = harness.Envelope();
        envelope = envelope with { Backup = envelope.Backup! with { Encryption = new BackupEncryptionInstruction { Required = true, KeyVersion = 1 } } };

        Assert.True(harness.Worker.TryPublish(envelope));
        harness.Start();
        await Wait.UntilAsync(() => harness.Client.Completions.Count == 1, "terminal result");

        var completion = Assert.Single(harness.Client.Completions);
        Assert.Equal("Failed", completion.Status);
        Assert.Equal(nameof(BackupTaskFailureCode.InvalidConfiguration), completion.FailureCode);
        Assert.Empty(harness.Pipeline.Calls);
    }

    // ---- result mapping -----------------------------------------------------------------------------------------------------------------------

    private static async Task<BackupTaskArtifact> MapAsync(Func<BackupArtifactInfo, BackupArtifactInfo> change)
    {
        var (harness, task, factory) = await ArrangeAsync(null);
        await using var _ = harness;
        var result = harness.Success(await factory.CreateAsync(task, default));
        return BackupTaskResultMapper.Map(task, result with { Artifact = change(result.Artifact!) }).Artifact!;
    }

    [Fact]
    public async Task AnArtifactWithoutAnEncryptionTransformIsReportedAsPlain()
    {
        var artifact = await MapAsync(info => info);

        Assert.Equal(ArtifactFormats.Plain, artifact.Format);
        Assert.Null(artifact.EncryptionKeyId);
        Assert.Null(artifact.EncryptionKeyVersion);
        Assert.Null(artifact.EncryptionWrappedDekBase64);
    }

    [Fact]
    public async Task AnEncryptedArtifactIsReportedWithItsKeyIdVersionAndWrappedDekAndKeepsBothChecksums()
    {
        byte[] wrapped = [1, 2, 3, 4];
        var artifact = await MapAsync(info => info with
        {
            Transforms = [new ArtifactTransform(ArtifactTransformKind.Encryption, EncryptingArtifactPreparer.Algorithm)],
            Seal = new ArtifactSeal("0123456789abcdef0123456789abcdef", 3, wrapped)
        });

        Assert.Equal(ArtifactFormats.Bpe2, artifact.Format);
        Assert.Equal("0123456789abcdef0123456789abcdef", artifact.EncryptionKeyId);
        Assert.Equal(3, artifact.EncryptionKeyVersion);
        Assert.Equal(wrapped, Convert.FromBase64String(artifact.EncryptionWrappedDekBase64!));
        Assert.Equal(new string('a', 64), artifact.SourceChecksum);
        Assert.Equal(new string('b', 64), artifact.ArtifactChecksum);
    }

    // ---- restore request factory --------------------------------------------------------------------------------------------------------------

    private sealed class RecordingKeys : IArtifactKeyClient
    {
        public (Guid Attempt, Guid Lease, Guid Operation)? Call { get; private set; }

        public Task<byte[]> UnwrapAsync(Guid attemptId, Guid leaseToken, Guid restoreOperationId, CancellationToken cancellationToken)
        {
            Call = (attemptId, leaseToken, restoreOperationId);
            return Task.FromResult(new byte[32]);
        }
    }

    private static Contracts.Restores.RestoreTaskSnapshot RestoreTask(string format, string sourceChecksum) => new()
    {
        OperationId = Guid.NewGuid(), AttemptId = Guid.NewGuid(), AttemptNumber = 1, TenantId = Guid.NewGuid(), LeaseToken = Guid.NewGuid(),
        LeaseExpiresAt = DateTimeOffset.UtcNow.AddMinutes(2), ArtifactId = Guid.NewGuid(), ReplicaId = Guid.NewGuid(),
        Source = new Contracts.Restores.RestoreTaskSource
        {
            Access = "AgentLocal", StorageProvider = "local", StorageExecutionLocation = "Agent", StorageTarget = "local", StorageKey = "tenant/sales.bak",
            SizeBytes = 2048, Checksum = new string('b', 64), ChecksumAlgorithm = "sha256", Format = format, SourceChecksum = sourceChecksum
        },
        Target = new Contracts.Restores.RestoreTaskTarget { Host = "sql-host", Port = 1433, IntegratedSecurity = true },
        DatabaseName = "SalesRestored", Engine = "mssql", StagingReference = "default", Purpose = "Execution", Mode = "NewDatabase",
        Options = new Contracts.Restores.RestoreTaskOptions()
    };

    [Fact]
    public async Task AnEncryptedReplicaGetsADecryptionBoundToThisAttemptsLeaseAndOperation()
    {
        var keys = new RecordingKeys();
        var task = RestoreTask(ArtifactFormats.Bpe2, new string('c', 64));
        var request = await new RestoreTaskRequestFactory(new TaskCredentials(), [Staging], artifactKeys: keys).CreateAsync(task, default);

        var decryption = request.Decryption!;
        Assert.Equal(new string('c', 64), decryption.SourceChecksum.Hex);
        Assert.Equal(32, (await decryption.UnwrapDek(default)).Length);
        Assert.Equal((task.AttemptId, task.LeaseToken, task.OperationId), keys.Call);
    }

    [Fact]
    public async Task APlainReplicaGetsNoDecryption()
    {
        var request = await new RestoreTaskRequestFactory(new TaskCredentials(), [Staging], artifactKeys: new RecordingKeys())
            .CreateAsync(RestoreTask(ArtifactFormats.Plain, ""), default);

        Assert.Null(request.Decryption);
    }

    [Theory]
    [InlineData("v2-bpe", "", true)] // no recorded plaintext checksum to verify against
    [InlineData("v2-bpe", "abc", true)]
    [InlineData("v2-bpe", "cccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccc", false)] // agent without a key client
    [InlineData("v3-unknown", "", true)]
    public async Task AnEncryptedReplicaTheAgentCannotVerifyIsRefusedBeforeAnythingIsRead(string format, string sourceChecksum, bool withKeys)
    {
        var factory = new RestoreTaskRequestFactory(new TaskCredentials(), [Staging], artifactKeys: withKeys ? new RecordingKeys() : null);

        var failure = await Assert.ThrowsAsync<RestoreTaskRequestException>(() => factory.CreateAsync(RestoreTask(format, sourceChecksum), default).AsTask());

        Assert.Equal(RestoreAgentFailureCodes.InvalidConfiguration, failure.FailureCode);
    }

    // ---- key client ---------------------------------------------------------------------------------------------------------------------------

    private static HttpArtifactKeyClient KeyClient(FakeHttpHandler handler) => new(
        new HttpClient(handler), new AgentBackendEndpoint(new Uri("https://backup.example.test/")), new FakeCredentialStore(TestIdentity.Create()));

    [Fact]
    public async Task TheKeyClientPostsAnEmptyFencedRequestAndReturnsTheDek()
    {
        var dek = RandomNumberGenerator.GetBytes(32);
        using var handler = new FakeHttpHandler
        {
            Respond = (_, _) => Task.FromResult(FakeHttpHandler.Json(HttpStatusCode.OK, $$"""{"dekBase64":"{{Convert.ToBase64String(dek)}}"}"""))
        };
        Guid attempt = Guid.NewGuid(), lease = Guid.NewGuid(), operation = Guid.NewGuid();

        var result = await KeyClient(handler).UnwrapAsync(attempt, lease, operation, default);

        Assert.Equal(dek, result);
        var request = Assert.Single(handler.Requests);
        Assert.Equal(HttpMethod.Post, request.Method);
        Assert.Equal($"/api/agent/restore-tasks/{attempt:D}/artifact-key", request.Uri.AbsolutePath);
        Assert.Equal(lease.ToString("D"), request.Headers[TaskStorageProtocol.LeaseHeader]);
        Assert.Equal(operation.ToString("D"), request.Headers[TaskStorageProtocol.RestoreHeader]);
        Assert.Equal("", request.Body ?? "");
    }

    [Theory]
    [InlineData(HttpStatusCode.Forbidden)]
    [InlineData(HttpStatusCode.Conflict)]
    [InlineData(HttpStatusCode.InternalServerError)]
    public async Task ARefusedKeyRequestIsAKeyUnavailableFailure(HttpStatusCode status)
    {
        using var handler = new FakeHttpHandler { Respond = (_, _) => Task.FromResult(FakeHttpHandler.Json(status, """{"error":"nope"}""")) };

        await Assert.ThrowsAsync<ArtifactKeyUnavailableException>(() => KeyClient(handler).UnwrapAsync(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), default));
    }

    [Theory]
    [InlineData("""{"dekBase64":"AAAA"}""")] // wrong length
    [InlineData("""{"dekBase64":"%%%"}""")]
    [InlineData("""{"dekBase64":""}""")]
    [InlineData("not json")]
    public async Task AnUnusableKeyAnswerIsAKeyUnavailableFailureThatDoesNotEchoTheBody(string body)
    {
        using var handler = new FakeHttpHandler { Respond = (_, _) => Task.FromResult(FakeHttpHandler.Json(HttpStatusCode.OK, body)) };

        var failure = await Assert.ThrowsAsync<ArtifactKeyUnavailableException>(() => KeyClient(handler).UnwrapAsync(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), default));

        Assert.DoesNotContain("AAAA", failure.Message);
    }

    [Fact]
    public async Task AnUnreachableBackendIsAKeyUnavailableFailure()
    {
        using var handler = new FakeHttpHandler { Respond = (_, _) => throw new HttpRequestException("connection refused") };

        await Assert.ThrowsAsync<ArtifactKeyUnavailableException>(() => KeyClient(handler).UnwrapAsync(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), default));
    }
}
