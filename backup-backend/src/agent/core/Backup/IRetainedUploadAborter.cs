namespace BackupPlatform.Agent.Core.Backup;

/// <summary>Aborts the remote parts of a journaled upload after its attempt ended and the task lease is gone.</summary>
public interface IRetainedUploadAborter
{
    /// <summary>Returns only when the upload is proven aborted (or has nothing to abort); throws otherwise so the journal is kept and the next sweep retries.</summary>
    Task AbortAsync(Guid attemptId, RetainedUpload upload, CancellationToken cancellationToken);
}
