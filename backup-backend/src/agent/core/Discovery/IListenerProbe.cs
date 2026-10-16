namespace BackupPlatform.Agent.Core.Discovery;

/// <param name="Connected">The TCP connection was accepted.</param>
/// <param name="Recognized">The peer answered the engine's credential-free handshake like that engine.</param>
/// <param name="Version">Server version from the handshake when the protocol carries one (SQL Server only).</param>
public sealed record ListenerProbeResult(bool Connected, bool Recognized, string? Version);

/// <summary>Asks whether a database engine answers on a loopback port. Never throws except for cancellation.</summary>
public interface IListenerProbe
{
    Task<ListenerProbeResult> ProbeAsync(string engine, int port, CancellationToken cancellationToken);
}
