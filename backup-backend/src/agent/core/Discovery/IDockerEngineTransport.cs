namespace BackupPlatform.Agent.Core.Discovery;

public interface IDockerEngineTransport
{
    Task<string?> QueryContainersAsync(CancellationToken cancellationToken);
}
