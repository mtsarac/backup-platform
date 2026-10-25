using BackupPlatform.StorageProviders.Abstractions.FileSystem;
using BackupPlatform.StorageProviders.Abstractions;

namespace BackupPlatform.StorageProviders.Smb;

public sealed record SmbCredential(string Username, string Password, string? Domain = null)
{
    public override string ToString() => "SmbCredential { [REDACTED] }";
}

public sealed record SmbStorageOptions(string Host, string Share, Guid TenantId, Guid ConnectionId)
{
    public SmbStorageOptions Validate()
    {
        if (Uri.CheckHostName(Host) == UriHostNameType.Unknown || Host.Contains('%') || Host.Contains(':')
            || Share.Length is < 1 or > 80 || Share.Any(c => !(char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or '$'))
            || TenantId == Guid.Empty || ConnectionId == Guid.Empty)
            throw new StorageException(StorageFailureKind.InvalidConfiguration, "The SMB share configuration is invalid.");
        return this;
    }
}

public sealed class SmbStorageProvider(SmbStorageOptions configuration,
    Func<CancellationToken, ValueTask<SmbCredential>> credentials, IWindowsNetworkSession? windowsSession) : IStorageProvider
{
    private readonly SmbStorageOptions _options = configuration.Validate();
    private readonly SmbUserSpaceStore _store = new(configuration, new("smb"));
    public StorageProviderId Id { get; } = new("smb");
    public StorageCapabilities Capabilities { get; } = new([StorageCapabilityIds.Read, StorageCapabilityIds.Delete,
        StorageCapabilityIds.AtomicPublish, StorageCapabilityIds.VerifyAbsence]);
    public StorageTopology Topology => StorageTopology.AgentDirect;

    public Task<StorageObjectMetadata> PutAsync(StorageObjectKey key, Stream content, StoragePutOptions options, IProgress<long>? progress, CancellationToken token) =>
        Run((provider, cancellation) => provider.PutAsync(key, content, options, progress, cancellation), (store, credential, cancellation) => store.PutAsync(key, content, options, progress, credential, cancellation), token);
    public Task<bool> ExistsAsync(StorageObjectKey key, CancellationToken token) =>
        Run((provider, cancellation) => provider.ExistsAsync(key, cancellation), (store, credential, cancellation) => store.ExistsAsync(key, credential, cancellation), token);
    public Task<StorageObjectMetadata?> GetMetadataAsync(StorageObjectKey key, CancellationToken token) =>
        Run((provider, cancellation) => provider.GetMetadataAsync(key, cancellation), (store, credential, cancellation) => store.GetMetadataAsync(key, credential, cancellation), token);
    public Task<StorageVerificationResult> VerifyAsync(StorageObjectKey key, StorageVerificationExpectation expected, CancellationToken token) =>
        Run((provider, cancellation) => provider.VerifyAsync(key, expected, cancellation), (store, credential, cancellation) => store.VerifyAsync(key, expected, credential, cancellation), token);
    public Task<bool> DeleteAsync(StorageObjectKey key, CancellationToken token) =>
        Run((provider, cancellation) => provider.DeleteAsync(key, cancellation), (store, credential, cancellation) => store.DeleteAsync(key, credential, cancellation), token);

    public async Task<Stream> OpenReadAsync(StorageObjectKey key, CancellationToken token)
    {
        // The open handle is bound to the authenticated session, so a read stream keeps its session (Windows: the network session) until it is disposed.
        if (OperatingSystem.IsWindows()) return await Run((provider, cancellation) => provider.OpenReadAsync(key, cancellation), null, token);
        return await _store.OpenReadAsync(key, await ReadCredentialAsync(token), token);
    }

    public IAsyncEnumerable<StorageEntry> EnumerateAsync(StorageObjectKey? prefix, DateTimeOffset modifiedBefore, CancellationToken token) =>
        throw new StorageException(StorageFailureKind.NotSupported, "SMB enumeration is not exposed through a connection.");

    private ShareProvider Provider(string root) => new(root, _options);

    /// <summary>Windows reaches the share as a UNC path inside an authenticated network session; Linux uses the managed client, which needs no mount privilege.</summary>
    private async Task<T> Run<T>(Func<IStorageProvider, CancellationToken, Task<T>> windows,
        Func<SmbUserSpaceStore, SmbCredential, CancellationToken, Task<T>>? other, CancellationToken token)
    {
        var credential = await ReadCredentialAsync(token);
        if (OperatingSystem.IsWindows())
        {
            if (windowsSession is null) throw new StorageException(StorageFailureKind.NotSupported, "SMB requires the Windows network session adapter.");
            return await windowsSession.RunAsync(credential.Username, credential.Password, credential.Domain,
                () => windows(Provider($"\\\\{_options.Host}\\{_options.Share}"), token)).ConfigureAwait(false);
        }
        if (other is null || !OperatingSystem.IsLinux()) throw new StorageException(StorageFailureKind.NotSupported, "SMB storage requires Windows or Linux.");
        return await other(_store, credential, token).ConfigureAwait(false);
    }

    private async ValueTask<SmbCredential> ReadCredentialAsync(CancellationToken token)
    {
        var credential = await credentials(token);
        if (string.IsNullOrWhiteSpace(credential.Username) || string.IsNullOrEmpty(credential.Password)
            || new[] { credential.Username, credential.Password, credential.Domain }.Any(value => value?.Any(c => c is '\r' or '\n' or '\0') == true))
            throw new StorageException(StorageFailureKind.AuthenticationFailed, "The SMB credentials are invalid.");
        return credential;
    }

    private sealed class ShareProvider(string root, SmbStorageOptions options) : FileSystemStorageProvider(new("smb"), StorageTopology.AgentDirect, root,
        ["tenants", options.TenantId.ToString("N"), "connections", options.ConnectionId.ToString("N")]);
}
