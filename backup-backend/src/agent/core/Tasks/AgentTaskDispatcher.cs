using BackupPlatform.Contracts.Agents;
using BackupPlatform.Contracts.Restores;
using BackupPlatform.Contracts.Downloads;

namespace BackupPlatform.Agent.Core.Tasks;

public sealed class AgentTaskDispatcher(BackupTaskWorker backups, RestoreTaskWorker restores, DeleteReplicaTaskWorker deletions, DownloadTaskWorker downloads,
    RestoreCleanupWorker cleanups) : IAgentTaskSink
{
    public bool TryPublish(AgentTaskEnvelope task) => task.Type switch
    {
        "database-backup" => backups.TryPublish(task),
        RestoreTaskTypes.DatabaseRestore or RestoreTaskTypes.DatabaseRestorePrecheck or RestoreTaskTypes.DatabaseRestoreDiscovery or RestoreTaskTypes.DatabaseRestoreVerification => restores.TryPublish(task),
        RestoreTaskTypes.DatabaseRestoreCleanup => cleanups.TryPublish(task),
        "delete-replica" => deletions.TryPublish(task),
        DownloadTaskTypes.BackupDownload => downloads.TryPublish(task),
        _ => false
    };
}
