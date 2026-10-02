namespace BackupPlatform.Agent.Core.Connectivity;

/// <summary>The agent cannot start because no usable identity is stored. The message is safe to show to an operator.</summary>
public sealed class AgentIdentityUnavailableException : Exception
{
    public AgentIdentityUnavailableException(string message) : base(message) { }

    public AgentIdentityUnavailableException(string message, Exception innerException) : base(message, innerException) { }
}
