using BackupPlatform.Contracts.Agents;

namespace BackupPlatform.Agent.Core.Tasks;

public sealed class AgentTaskDispatcher(BackupTaskWorker backups, DeleteReplicaTaskWorker deletions) : IAgentTaskSink
{
    public bool TryPublish(AgentTaskEnvelope task) => task.Type switch
    {
        "database-backup" => backups.TryPublish(task),
        "delete-replica" => deletions.TryPublish(task),
        _ => false
    };
}
