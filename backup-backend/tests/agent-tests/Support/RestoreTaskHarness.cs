using System.Collections.Concurrent;
using BackupPlatform.Agent.Core.Connectivity;
using BackupPlatform.Agent.Core.Identity;
using BackupPlatform.Agent.Core.Restore;
using BackupPlatform.Agent.Core.Tasks;
using BackupPlatform.BackupProviders.Abstractions.Database;
using BackupPlatform.Contracts.Agents;
using BackupPlatform.Contracts.Backups;
using BackupPlatform.Contracts.Restores;
using BackupPlatform.StorageProviders.Abstractions;
using Microsoft.Extensions.Logging.Abstractions;

namespace BackupPlatform.AgentTests.Support;

/// <summary>The real restore worker on a deterministic clock with a fake client and a fake pipeline; the only thing under test is the worker's own behaviour.</summary>
internal sealed class RestoreTaskHarness : IAsyncDisposable
{
    private readonly CancellationTokenSource _stop = new();
    private Task? _running;

    public DeterministicTimeProvider Time { get; } = new();
    public FakeCredentialStore Store { get; } = new(TestIdentity.Create());
    public RestoreClient Client { get; }
    public RestorePipelineFake Pipeline { get; } = new();
    public TaskCredentials Credentials { get; } = new();
    public RestoreTaskWorker Worker { get; }

    public RestoreTaskHarness(bool staging = true)
    {
        Client = new RestoreClient(Time);
        Worker = new RestoreTaskWorker(Client, Store, Pipeline,
            new RestoreTaskRequestFactory(Credentials, staging ? [StagingArea.SameHost(Path.GetTempPath())] : []),
            new AgentSoftwareInfo("1.2.3", "linux", null, null), Time, NullLogger<RestoreTaskWorker>.Instance);
        Pipeline.Execute = (request, _, _) => Task.FromResult(Success(request));
    }

    public AgentTaskEnvelope Envelope(bool integrated = true, string mode = "NewDatabase")
    {
        var tenant = Guid.NewGuid();
        var key = $"{tenant:N}/{Guid.NewGuid():N}/{Guid.NewGuid():N}/{Guid.NewGuid():N}/{ManagedBackupLayout.StagedFileName("Sales", Time.GetUtcNow(), Guid.NewGuid())}";
        var task = new RestoreTaskSnapshot
        {
            OperationId = Guid.NewGuid(), AttemptId = Guid.NewGuid(), AttemptNumber = 1, TenantId = tenant,
            LeaseToken = Guid.NewGuid(), LeaseExpiresAt = Time.GetUtcNow().AddSeconds(120),
            ArtifactId = Guid.NewGuid(), ReplicaId = Guid.NewGuid(),
            Source = new RestoreTaskSource
            {
                StorageProvider = "local", StorageTarget = "local", StorageKey = key, StorageExecutionLocation = "Agent",
                SizeBytes = 4096, Checksum = new string('a', 64), Access = "AgentLocal"
            },
            Target = new RestoreTaskTarget
            {
                Host = "sql.example.test", Port = 1433, IntegratedSecurity = integrated,
                DatabaseCredentialReference = integrated ? null : "sales-db"
            },
            DatabaseName = "SalesRestored", Mode = mode
        };
        return new AgentTaskEnvelope { Id = task.AttemptId.ToString(), Type = RestoreTaskTypes.DatabaseRestore, Restore = task };
    }

    public AgentTaskEnvelope With(AgentTaskEnvelope envelope, Func<RestoreTaskSnapshot, RestoreTaskSnapshot> change) =>
        envelope with { Restore = change(envelope.Restore!) };

    public void Start() => _running = Worker.RunAsync(_stop.Token);

    public DatabaseRestorePipelineResult Success(DatabaseRestorePipelineRequest? request = null)
    {
        var now = Time.GetUtcNow();
        var instance = new DatabaseInstanceInfo(DatabaseEngineId.MsSql, "SQL-TARGET", null, "16.0.4135.4", "Developer Edition", "Linux");
        var header = new RestoreBackupHeader("Sales", RestoreBackupKind.Database, new Version(16, 0, 4135), "SQL-SOURCE", 160, "SQL_Latin1_General_CP1_CI_AS",
            now.DateTime.AddHours(-1), now.DateTime.AddHours(-1).AddMinutes(1), 1, 3000, true, true, true, false, Guid.NewGuid());
        var inspection = new RestoreBackupInspection(header, [new RestoreBackupFile("Sales", "/var/opt/mssql/data/Sales.mdf", RestoreFileKind.Data, "PRIMARY", 1024)],
            true, now.AddSeconds(-5), now.AddSeconds(-4));
        var mapping = new RestoreFileMapping("Sales", RestoreFileKind.Data, "/var/opt/mssql/data/Sales.mdf", new DatabaseServerPath("/var/opt/mssql/data/SalesRestored.mdf"), 1024, "PRIMARY");
        var plan = new RestorePlan([mapping], new RestoreTargetInfo(instance, new DatabaseServerPath("/var/opt/mssql/data"), new DatabaseServerPath("/var/opt/mssql/data"), false, null));
        return new DatabaseRestorePipelineResult
        {
            Status = DatabaseRestoreStatus.Succeeded, FinalStage = RestoreExecutionStage.Completed, TargetState = DatabaseRestoreTargetState.Online,
            DatabaseState = "ONLINE", StartedAt = now.AddSeconds(-10), FinishedAt = now, RestoreStartedAt = now.AddSeconds(-3), RestoreFinishedAt = now.AddSeconds(-1),
            PreCheck = [new BackupPreCheckItem("server.reachable", BackupPreCheckStatus.Passed, "ok", "The server is reachable.")],
            Inspection = inspection, Plan = plan, FileMappings = plan.Mappings,
            Warnings = [new BackupWarning("w1", "A warning.")], ChecksumVerified = request?.VerifyChecksum ?? true, BackupVerified = request?.VerifyBackup ?? true
        };
    }

    public DatabaseRestorePipelineResult Failed(string code, DatabaseRestoreTargetState state = DatabaseRestoreTargetState.NotModified,
        DatabaseRestoreStatus status = DatabaseRestoreStatus.Failed, string reason = "internal-reason", string message = "A safe sentence.")
    {
        var now = Time.GetUtcNow();
        return new DatabaseRestorePipelineResult
        {
            Status = status,
            FinalStage = status == DatabaseRestoreStatus.Cancelled ? RestoreExecutionStage.Cancelled : RestoreExecutionStage.Failed,
            Failure = new RestorePipelineFailure(code, RestoreExecutionStage.Restoring, reason, message),
            TargetState = state, StartedAt = now, FinishedAt = now
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

internal sealed class RestorePipelineFake : IDatabaseRestorePipeline
{
    public ConcurrentQueue<DatabaseRestorePipelineRequest> Calls { get; } = new();
    public Func<DatabaseRestorePipelineRequest, IProgress<RestoreProgress>?, CancellationToken, Task<DatabaseRestorePipelineResult>> Execute { get; set; } = null!;

    public Task<DatabaseRestorePipelineResult> RunRestoreAsync(DatabaseRestorePipelineRequest request, IProgress<RestoreProgress>? progress, CancellationToken cancellationToken)
    {
        Calls.Enqueue(request);
        return Execute(request, progress, cancellationToken);
    }
}

internal sealed class RestoreClient(TimeProvider time) : IRestoreTaskClient
{
    public ConcurrentQueue<BackupTaskStartRequest> Starts { get; } = new();
    public ConcurrentQueue<BackupTaskProgressRequest> Progress { get; } = new();
    public ConcurrentQueue<RestoreTaskCompletionRequest> Completions { get; } = new();
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

    public Task<AgentBackendResult<bool>> CompleteAsync(AgentLocalIdentity identity, Guid attemptId, RestoreTaskCompletionRequest request, CancellationToken cancellationToken)
    {
        Completions.Enqueue(request);
        CompletionCredentials.Enqueue(identity.Current.CredentialId);
        return Task.FromResult(OnComplete?.Invoke(Completions.Count) ?? AgentBackendResult<bool>.Success(true));
    }
}
