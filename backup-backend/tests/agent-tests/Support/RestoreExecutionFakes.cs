using BackupPlatform.BackupProviders.Abstractions.Database;
using BackupPlatform.StorageProviders.Abstractions;

namespace BackupPlatform.AgentTests.Support;

/// <summary>A restore provider that answers with canned results, for tests that run the real pipeline and worker around it.</summary>
internal sealed class CannedRestoreProvider : IDatabaseRestoreProvider
{
    public DatabaseEngineId Engine => DatabaseEngineId.MsSql;

    public Func<DatabaseRestoreRequest, CancellationToken, Task<DatabaseRestoreResult>>? Execute { get; set; }

    public int Executions { get; private set; }

    public Task<BackupPreCheckReport> PreCheckRestoreAsync(DatabaseConnectionSettings connection, DatabaseCredential credential, RestorePreCheckRequest request, CancellationToken cancellationToken) =>
        Task.FromResult(new BackupPreCheckReport([new BackupPreCheckItem("server.reachable", BackupPreCheckStatus.Passed, "ok", "The server is reachable.")]));

    public Task<RestoreInspectionResult> InspectBackupAsync(DatabaseConnectionSettings connection, DatabaseCredential credential, RestoreInspectionRequest request, CancellationToken cancellationToken) =>
        Task.FromResult(new RestoreInspectionResult(
            new RestoreBackupInspection(
                new RestoreBackupHeader("Sales", RestoreBackupKind.Database, new Version(16, 0, 1000), "SOURCE", 160, "Latin1_General_CI_AS",
                    DateTime.UtcNow.AddHours(-1), DateTime.UtcNow.AddHours(-1), 1, 100, true, true, true, false, Guid.NewGuid()),
                [new RestoreBackupFile("Sales", "/source/Sales.mdf", RestoreFileKind.Data, "PRIMARY", 100)], request.RunVerifyOnly, null, null),
            null));

    public Task<RestorePlanResult> PlanRestoreAsync(DatabaseConnectionSettings connection, DatabaseCredential credential, RestorePlanRequest request, CancellationToken cancellationToken) =>
        Task.FromResult(new RestorePlanResult(
            new BackupPreCheckReport([new BackupPreCheckItem("plan.version", BackupPreCheckStatus.Passed, "ok", "Fine.")]),
            new RestorePlan(
                [new RestoreFileMapping("Sales", RestoreFileKind.Data, "/source/Sales.mdf", new DatabaseServerPath("/target/Sales.mdf"), 100, "PRIMARY")],
                new RestoreTargetInfo(new DatabaseInstanceInfo(DatabaseEngineId.MsSql, "TARGET", null, "16.0", "Developer", "Linux"),
                    new DatabaseServerPath("/target"), new DatabaseServerPath("/target"), false, null))));

    public Task<DatabaseRestoreResult> ExecuteRestoreAsync(DatabaseConnectionSettings connection, DatabaseCredential credential, DatabaseRestoreRequest request, IProgress<RestoreProgress>? progress, CancellationToken cancellationToken)
    {
        Executions++;
        return Execute is { } custom
            ? custom(request, cancellationToken)
            : Task.FromResult(new DatabaseRestoreResult(
                DatabaseRestoreStatus.Succeeded, null, DatabaseRestoreTargetState.Online, "ONLINE",
                DateTimeOffset.UtcNow.AddSeconds(-2), DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddSeconds(-1), DateTimeOffset.UtcNow, request.Plan.Mappings, []));
    }
}

/// <summary>A readable storage holding one object in memory under the <c>local</c> id.</summary>
internal sealed class MemoryReadableStorage(StorageObjectKey key, byte[] content) : IStorageProvider
{
    public StorageProviderId Id => StorageProviderId.Local;
    public StorageTopology Topology => StorageTopology.AgentDirect;
    public StorageCapabilities Capabilities { get; } = new([StorageCapabilityIds.Read]);

    public Task<StorageObjectMetadata?> GetMetadataAsync(StorageObjectKey requested, CancellationToken cancellationToken) =>
        Task.FromResult(requested == key ? new StorageObjectMetadata(new StorageLocation(Id, key), content.Length, null, DateTimeOffset.UtcNow) : null);

    public Task<Stream> OpenReadAsync(StorageObjectKey requested, CancellationToken cancellationToken) =>
        requested == key
            ? Task.FromResult<Stream>(new MemoryStream(content, writable: false))
            : Task.FromException<Stream>(new StorageException(StorageFailureKind.NotFound, "The stored object does not exist."));

    public Task<StorageObjectMetadata> PutAsync(StorageObjectKey k, Stream c, StoragePutOptions options, IProgress<long>? progress, CancellationToken cancellationToken) => throw new NotSupportedException();
    public Task<bool> ExistsAsync(StorageObjectKey k, CancellationToken cancellationToken) => throw new NotSupportedException();
    public Task<StorageVerificationResult> VerifyAsync(StorageObjectKey k, StorageVerificationExpectation expected, CancellationToken cancellationToken) => throw new NotSupportedException();
    public Task<bool> DeleteAsync(StorageObjectKey k, CancellationToken cancellationToken) => throw new NotSupportedException();
    public IAsyncEnumerable<StorageEntry> EnumerateAsync(StorageObjectKey? prefix, DateTimeOffset modifiedBefore, CancellationToken cancellationToken) => throw new NotSupportedException();
}
