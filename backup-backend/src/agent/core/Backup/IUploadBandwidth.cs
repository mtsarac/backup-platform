using BackupPlatform.StorageProviders.Abstractions;

namespace BackupPlatform.Agent.Core.Backup;

/// <summary>
/// The agent's side of the upload limits. The backend relay paces every upload it reads itself, so the agent paces only the bytes the backend cannot
/// see (<see cref="StorageTopology.AgentDirect"/>) and merely learns the policy for the rest (to widen the deadline); a byte is never paced twice.
/// It is about upload bytes only: the engine backup, hashing, heartbeats and lease renewal never pass through it. Engines and storage providers do not
/// know it exists; the pipeline wraps the source stream it hands to <c>IStorageProvider.PutAsync</c>.
/// </summary>
public interface IUploadBandwidth
{
    /// <summary>
    /// Learns the limits that apply right now. For an AgentDirect destination it also keeps them current until the session is disposed and fails closed:
    /// if the policy cannot be obtained the upload must not start unpaced.
    /// </summary>
    /// <exception cref="BackupPlatform.StorageProviders.Abstractions.Transfer.UploadPolicyException">The policy could not be obtained.</exception>
    Task<IUploadBandwidthSession> BeginAsync(StorageTopology destination, CancellationToken cancellationToken);
}

/// <summary>One upload's pacing. Disposing it stops the policy renewal and gives the tenant slice back.</summary>
public interface IUploadBandwidthSession : IAsyncDisposable
{
    /// <summary>The stream to upload from: paced for an AgentDirect destination, <paramref name="source"/> itself for a relayed one (the backend paces that).</summary>
    Stream Wrap(Stream source);

    /// <summary>How much longer than usual an upload of <paramref name="sizeBytes"/> may legitimately take because it is paced.</summary>
    TimeSpan DeadlineAllowance(long sizeBytes);
}
