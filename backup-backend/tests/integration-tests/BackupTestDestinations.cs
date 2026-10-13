using BackupPlatform.Contracts.Backups;

namespace BackupPlatform.IntegrationTests;

/// <summary>Canonical destination lists for job configurations built by tests; a job configuration has no other destination source.</summary>
internal static class BackupTestDestinations
{
    public static BackupDestinationContract Local(bool primary = true, string location = "Agent") =>
        new() { StorageProvider = "local", StorageExecutionLocation = location, IsPrimary = primary };

    public static BackupDestinationContract Central(Guid? connectionId = null, bool primary = true) =>
        new() { StorageProvider = "central", StorageConnectionId = connectionId, StorageExecutionLocation = "Backend", IsPrimary = primary };

    public static BackupDestinationContract Relayed(string provider, Guid connectionId, bool primary = false) =>
        new() { StorageProvider = provider, StorageConnectionId = connectionId, StorageExecutionLocation = "Backend", IsPrimary = primary };

    /// <summary>A local destination written by the agent: the default for jobs that do not care where the artifact goes.</summary>
    public static IReadOnlyList<BackupDestinationContract> LocalAgent => [Local()];

    /// <summary>A local destination written by the backend.</summary>
    public static IReadOnlyList<BackupDestinationContract> LocalBackend => [Local(location: "Backend")];
}
