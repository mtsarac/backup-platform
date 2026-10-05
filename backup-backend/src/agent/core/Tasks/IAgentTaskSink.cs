using BackupPlatform.Contracts.Agents;

namespace BackupPlatform.Agent.Core.Tasks;

public interface IAgentTaskSink
{
    /// <summary>Offers claimed work without waiting for execution or queue capacity.</summary>
    bool TryPublish(AgentTaskEnvelope task);
}
