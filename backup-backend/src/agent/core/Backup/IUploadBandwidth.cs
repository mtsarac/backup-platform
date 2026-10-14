namespace BackupPlatform.Agent.Core.Backup;

/// <summary>
/// Paces the bytes of one upload to the limits the backend sets. It is deliberately about bytes only: the engine backup, hashing, heartbeats and
/// lease renewal never pass through it. Engines and storage providers do not know it exists; the pipeline wraps the source stream it hands to
/// <c>IStorageProvider.PutAsync</c>, which is why every provider, local or relayed, is paced the same way and only once.
/// </summary>
public interface IUploadBandwidth
{
    /// <summary>
    /// Learns the limits that apply right now and keeps them current until the session is disposed. Fails closed: if the policy cannot be obtained the upload
    /// must not start unpaced.
    /// </summary>
    /// <exception cref="UploadBandwidthUnavailableException">The policy could not be obtained.</exception>
    Task<IUploadBandwidthSession> BeginAsync(CancellationToken cancellationToken);
}

/// <summary>One upload's pacing. Disposing it stops the policy renewal and gives the tenant slice back.</summary>
public interface IUploadBandwidthSession : IAsyncDisposable
{
    /// <summary>The stream to upload from: reads of <paramref name="source"/> are paced; length and position pass through.</summary>
    Stream Wrap(Stream source);

    /// <summary>How much longer than usual an upload of <paramref name="sizeBytes"/> may legitimately take because it is paced.</summary>
    TimeSpan DeadlineAllowance(long sizeBytes);
}

/// <summary>The upload policy could not be obtained (backend unreachable, lease lost, credential refused); nothing is sent.</summary>
public sealed class UploadBandwidthUnavailableException(string message, Exception? innerException = null) : IOException(message, innerException);
