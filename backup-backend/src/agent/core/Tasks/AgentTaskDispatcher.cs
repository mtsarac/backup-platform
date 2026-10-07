using BackupPlatform.Contracts.Agents;
using BackupPlatform.Contracts.Restores;

namespace BackupPlatform.Agent.Core.Tasks;

public sealed class AgentTaskDispatcher(BackupTaskWorker backups, RestoreTaskWorker restores, DeleteReplicaTaskWorker deletions) : IAgentTaskSink
{
    public bool TryPublish(AgentTaskEnvelope task) => task.Type switch
    {
        "database-backup" => backups.TryPublish(task),
        RestoreTaskTypes.DatabaseRestore => restores.TryPublish(task),
        "delete-replica" => deletions.TryPublish(task),
        _ => false
    };
}
