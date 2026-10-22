namespace BackupPlatform.StorageProviders.Abstractions;

public interface IS3AccessChecker
{
    Task CheckAccessAsync(CancellationToken cancellationToken);
}
