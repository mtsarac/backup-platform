using System.Collections.Concurrent;
using BackupPlatform.Agent.Core.Backup;
using BackupPlatform.Agent.Core.Connectivity;
using BackupPlatform.Agent.Core.Identity;
using BackupPlatform.Agent.Core.Tasks;
using BackupPlatform.BackupProviders.Abstractions.Database;
using BackupPlatform.Contracts.Agents;
using BackupPlatform.Contracts.Backups;
using BackupPlatform.StorageProviders.Abstractions;
using Microsoft.Extensions.Logging.Abstractions;

namespace BackupPlatform.AgentTests.Support;

internal sealed class BackupTaskHarness : IAsyncDisposable
{
    private readonly CancellationTokenSource _stop = new();
    private Task? _running;
    public DeterministicTimeProvider Time { get; } = new();
    public FakeCredentialStore Store { get; } = new(TestIdentity.Create());
    public TaskClient Client { get; }
    public TaskPipeline Pipeline { get; } = new();
    public TaskCredentials Credentials { get; } = new();
    public BackupTaskWorker Worker { get; }

    public BackupTaskHarness()
    {
        Client = new TaskClient(Time);
        Worker = new BackupTaskWorker(Client, Store, Pipeline,
            new BackupTaskRequestFactory(Credentials, [StagingArea.SameHost(Path.GetTempPath())]),
            new AgentSoftwareInfo("1.2.3", "linux", null, null), Time, NullLogger<BackupTaskWorker>.Instance);
        Pipeline.Execute = (request, _, _) => Task.FromResult(Success(request));
    }

    public AgentTaskEnvelope Envelope(bool integrated = true)
    {
        var task = new BackupTaskSnapshot
        {
            TenantId = Guid.NewGuid(), JobId = Guid.NewGuid(), RunId = Guid.NewGuid(), AttemptId = Guid.NewGuid(),
            AttemptNumber = 1, LeaseToken = Guid.NewGuid(), LeaseExpiresAt = Time.GetUtcNow().AddSeconds(120),
            Configuration = new BackupExecutionConfiguration
            {
                Database = "Sales", Host = "sql.example.test", Port = 1433,
                IntegratedSecurity = integrated, DatabaseCredentialReference = integrated ? null : "sales-db",
                StoragePrefix = "backups",
                Destinations = [new BackupDestinationContract { StorageProvider = "local", StorageExecutionLocation = "Agent", IsPrimary = true }]
            }
        };
        return new AgentTaskEnvelope { Id = task.AttemptId.ToString(), Type = "database-backup", Backup = task };
    }

    public void Start() => _running = Worker.RunAsync(_stop.Token);

    public DatabaseBackupPipelineResult Success(DatabaseBackupPipelineRequest request)
    {
        var now = Time.GetUtcNow();
        var sourceChecksum = new ContentChecksum(ChecksumAlgorithm.Sha256, new string('a', 64));
        var artifactChecksum = new ContentChecksum(ChecksumAlgorithm.Sha256, new string('b', 64));
        return new DatabaseBackupPipelineResult
        {
            Status = DatabaseBackupStatus.Succeeded, FinalStage = BackupExecutionStage.Completed,
            StartedAt = now.AddSeconds(-1), FinishedAt = now, BackupStartedAt = now.AddSeconds(-1), BackupFinishedAt = now,
            Source = new DatabaseBackupSource(new DatabaseInstanceInfo(DatabaseEngineId.MsSql, "SQL-SERVER", null, "16.0", "Developer Edition", "Linux"),
                request.DatabaseName, new Dictionary<string, string> { ["recovery-model"] = "simple", ["compatibility-level"] = "160" }),
            SourceFile = new BackupSourceFile("source.bak", 512, sourceChecksum),
            Artifact = new BackupArtifactInfo("source.bak.gz", 256, artifactChecksum, false, [new ArtifactTransform(ArtifactTransformKind.Compression, "gzip")]),
            Destinations = [.. request.EffectiveDestinations.Select(destination => new BackupDestinationResult(destination, BackupDestinationState.Verified,
                new BackupReplicaInfo(new StorageLocation(destination.Storage, request.StoragePrefix.Combine("source.bak.gz")),
                    256, artifactChecksum, StorageVerificationStatus.Verified, StorageVerificationMethod.ReadBack, now),
                null, true, request.StoragePrefix.Combine("source.bak.gz"), false, now.AddSeconds(-1), now))],
            Options = new BackupOptionsOutcome(request.Options, new AppliedFullBackupOptions(true, true, false)),
            Verification = new BackupVerification(BackupVerificationStatus.Passed, "RESTORE VERIFYONLY", now.AddSeconds(-1), now)
        };
    }

    public async ValueTask DisposeAsync()
    {
        await _stop.CancelAsync();
        if (_running is not null)
        {
            await _running.WaitAsync(TimeSpan.FromSeconds(10));
        }
        _stop.Dispose();
    }
}

internal sealed class TaskCredentials : IBackupTaskCredentialResolver
{
    public int Calls { get; private set; }
    public Exception? Failure { get; set; }
    public ValueTask<DatabaseCredential> ResolveAsync(string reference, CancellationToken cancellationToken)
    {
        Calls++;
        if (Failure is not null)
        {
            throw Failure;
        }
        return ValueTask.FromResult<DatabaseCredential>(new PasswordDatabaseCredential("backup-user", new DatabaseSecret("local-only-password")));
    }
}

internal sealed class TaskPipeline : IDatabaseBackupPipeline
{
    public ConcurrentQueue<DatabaseBackupPipelineRequest> Calls { get; } = new();
    public Func<DatabaseBackupPipelineRequest, IProgress<BackupProgress>?, CancellationToken, Task<DatabaseBackupPipelineResult>> Execute { get; set; } = null!;
    public Task<DatabaseBackupPipelineResult> RunFullBackupAsync(DatabaseBackupPipelineRequest request, IProgress<BackupProgress>? progress, CancellationToken cancellationToken)
    {
        Calls.Enqueue(request);
        return Execute(request, progress, cancellationToken);
    }
}

internal sealed class TaskClient(TimeProvider time) : IBackupTaskClient
{
    public ConcurrentQueue<BackupTaskStartRequest> Starts { get; } = new();
    public ConcurrentQueue<BackupTaskProgressRequest> Progress { get; } = new();
    public ConcurrentQueue<BackupTaskCompletionRequest> Completions { get; } = new();
    public ConcurrentQueue<Guid> CompletionCredentials { get; } = new();
    public Func<AgentBackendResult<BackupTaskLeaseResponse>>? OnStart { get; set; }
    public Func<AgentBackendResult<BackupTaskLeaseResponse>>? OnRenew { get; set; }
    public Func<AgentBackendResult<bool>>? OnProgress { get; set; }
    public Func<int, AgentBackendResult<bool>>? OnComplete { get; set; }

    private AgentBackendResult<BackupTaskLeaseResponse> Lease() => AgentBackendResult<BackupTaskLeaseResponse>.Success(
        new BackupTaskLeaseResponse { LeaseExpiresAt = time.GetUtcNow().AddSeconds(120) });

    public Task<AgentBackendResult<BackupTaskLeaseResponse>> StartAsync(AgentLocalIdentity identity, Guid attemptId, BackupTaskStartRequest request, CancellationToken cancellationToken)
    {
        Starts.Enqueue(request);
        return Task.FromResult(OnStart?.Invoke() ?? Lease());
    }
    public Task<AgentBackendResult<BackupTaskLeaseResponse>> RenewAsync(AgentLocalIdentity identity, Guid attemptId, BackupTaskLeaseRequest request, CancellationToken cancellationToken) =>
        Task.FromResult(OnRenew?.Invoke() ?? Lease());
    public Task<AgentBackendResult<bool>> ReportProgressAsync(AgentLocalIdentity identity, Guid attemptId, BackupTaskProgressRequest request, CancellationToken cancellationToken)
    {
        Progress.Enqueue(request);
        return Task.FromResult(OnProgress?.Invoke() ?? AgentBackendResult<bool>.Success(true));
    }
    public Task<AgentBackendResult<bool>> CompleteAsync(AgentLocalIdentity identity, Guid attemptId, BackupTaskCompletionRequest request, CancellationToken cancellationToken)
    {
        Completions.Enqueue(request);
        CompletionCredentials.Enqueue(identity.Current.CredentialId);
        return Task.FromResult(OnComplete?.Invoke(Completions.Count) ?? AgentBackendResult<bool>.Success(true));
    }
}
