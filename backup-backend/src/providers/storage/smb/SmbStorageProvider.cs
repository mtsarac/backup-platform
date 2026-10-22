using System.ComponentModel;
using System.Diagnostics;
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
    public StorageProviderId Id { get; } = new("smb");
    public StorageCapabilities Capabilities { get; } = new([StorageCapabilityIds.Read, StorageCapabilityIds.Delete,
        StorageCapabilityIds.AtomicPublish, StorageCapabilityIds.VerifyAbsence]);
    public StorageTopology Topology => StorageTopology.AgentDirect;

    public Task<StorageObjectMetadata> PutAsync(StorageObjectKey key, Stream content, StoragePutOptions options, IProgress<long>? progress, CancellationToken token) =>
        ExecuteAsync((provider, cancellation) => provider.PutAsync(key, content, options, progress, cancellation), token);
    public Task<bool> ExistsAsync(StorageObjectKey key, CancellationToken token) => ExecuteAsync((provider, cancellation) => provider.ExistsAsync(key, cancellation), token);
    public Task<StorageObjectMetadata?> GetMetadataAsync(StorageObjectKey key, CancellationToken token) => ExecuteAsync((provider, cancellation) => provider.GetMetadataAsync(key, cancellation), token);
    public Task<StorageVerificationResult> VerifyAsync(StorageObjectKey key, StorageVerificationExpectation expected, CancellationToken token) =>
        ExecuteAsync((provider, cancellation) => provider.VerifyAsync(key, expected, cancellation), token);
    public Task<bool> DeleteAsync(StorageObjectKey key, CancellationToken token) => ExecuteAsync((provider, cancellation) => provider.DeleteAsync(key, cancellation), token);

    public async Task<Stream> OpenReadAsync(StorageObjectKey key, CancellationToken token)
    {
        // The open handle is bound to the authenticated session; a Linux mount must outlive its reader.
        if (OperatingSystem.IsWindows()) return await ExecuteAsync((provider, cancellation) => provider.OpenReadAsync(key, cancellation), token);
        var credential = await ReadCredentialAsync(token);
        var mount = await MountAsync(credential, token);
        try { return new MountedReadStream(await Provider(mount.Root).OpenReadAsync(key, token), mount); }
        catch { await mount.DisposeAsync(); throw; }
    }

    public IAsyncEnumerable<StorageEntry> EnumerateAsync(StorageObjectKey? prefix, DateTimeOffset modifiedBefore, CancellationToken token) =>
        throw new StorageException(StorageFailureKind.NotSupported, "SMB enumeration is not exposed through a connection.");

    private ShareProvider Provider(string root) => new(root, _options);
    private async Task<T> ExecuteAsync<T>(Func<IStorageProvider, CancellationToken, Task<T>> operation, CancellationToken token)
    {
        var credential = await ReadCredentialAsync(token);
        if (OperatingSystem.IsWindows())
        {
            if (windowsSession is null) throw new StorageException(StorageFailureKind.NotSupported, "SMB requires the Windows network session adapter.");
            return await windowsSession.RunAsync(credential.Username, credential.Password, credential.Domain,
                () => operation(Provider($"\\\\{_options.Host}\\{_options.Share}"), token)).ConfigureAwait(false);
        }
        if (!OperatingSystem.IsLinux()) throw new StorageException(StorageFailureKind.NotSupported, "SMB storage requires Windows or Linux with cifs-utils.");
        await using var mount = await MountAsync(credential, token);
        return await operation(Provider(mount.Root), token);
    }

    private async ValueTask<SmbCredential> ReadCredentialAsync(CancellationToken token)
    {
        var credential = await credentials(token);
        if (string.IsNullOrWhiteSpace(credential.Username) || string.IsNullOrEmpty(credential.Password)
            || new[] { credential.Username, credential.Password, credential.Domain }.Any(value => value?.Any(c => c is '\r' or '\n' or '\0') == true))
            throw new StorageException(StorageFailureKind.AuthenticationFailed, "The SMB credentials are invalid.");
        return credential;
    }


    private async Task<MountLease> MountAsync(SmbCredential credential, CancellationToken token)
    {
        if (!OperatingSystem.IsLinux()) throw new StorageException(StorageFailureKind.NotSupported, "SMB mounting requires Linux with cifs-utils.");
        var root = Path.Combine(Path.GetTempPath(), $"bp-smb-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        var credentialPath = root + ".credentials";
        var mounted = false;
        try
        {
            await using (var file = new FileStream(credentialPath, new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write,
                UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite, Options = FileOptions.Asynchronous }))
            await using (var writer = new StreamWriter(file))
                await writer.WriteAsync($"username={credential.Username}\npassword={credential.Password}\ndomain={credential.Domain ?? ""}\n".AsMemory(), token);
            await RunAsync("mount.cifs", [$"//{_options.Host}/{_options.Share}", root, "-o",
                $"credentials={credentialPath},vers=3.1.1,seal,cache=none,actimeo=0,nosuid,nodev,noexec"], token);
            mounted = true;
            return new MountLease(root);
        }
        finally
        {
            File.Delete(credentialPath);
            if (!mounted) Directory.Delete(root);
        }
    }

    private static async Task RunAsync(string executable, string[] arguments, CancellationToken token)
    {
        var start = new ProcessStartInfo(executable) { UseShellExecute = false, RedirectStandardError = true, RedirectStandardOutput = true, CreateNoWindow = true };
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        using var process = new Process { StartInfo = start };
        try
        {
            if (!process.Start()) throw new StorageException(StorageFailureKind.NetworkUnavailable, "The SMB mount helper could not start.");
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
            deadline.CancelAfter(TimeSpan.FromSeconds(30));
            var output = process.StandardOutput.ReadToEndAsync(deadline.Token);
            var error = process.StandardError.ReadToEndAsync(deadline.Token);
            try { await Task.WhenAll(process.WaitForExitAsync(deadline.Token), output, error); }
            catch (OperationCanceledException)
            {
                if (!process.HasExited) process.Kill(entireProcessTree: true);
                await process.WaitForExitAsync(CancellationToken.None);
                if (token.IsCancellationRequested) throw;
                throw new StorageException(StorageFailureKind.Timeout, "The SMB mount helper timed out.");
            }
            if (process.ExitCode != 0) throw new StorageException(StorageFailureKind.AccessDenied, "SMB share access failed; check credentials, SMB 3 encryption and mount permissions.");
        }
        catch (Win32Exception) { throw new StorageException(StorageFailureKind.InvalidConfiguration, "SMB requires the cifs-utils mount helper and mount privileges."); }
    }

    private sealed class ShareProvider(string root, SmbStorageOptions options) : FileSystemStorageProvider(new("smb"), StorageTopology.AgentDirect, root,
        ["tenants", options.TenantId.ToString("N"), "connections", options.ConnectionId.ToString("N")]);

    private sealed class MountLease(string root) : IAsyncDisposable
    {
        public string Root => root;
        public async ValueTask DisposeAsync()
        {
            using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(35));
            await RunAsync("umount", [root], cleanup.Token);
            Directory.Delete(root);
        }
    }


    private sealed class MountedReadStream(Stream stream, MountLease mount) : Stream
    {
        public override bool CanRead => stream.CanRead;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => stream.Length;
        public override long Position { get => stream.Position; set => throw new NotSupportedException(); }
        public override int Read(byte[] buffer, int offset, int count) => stream.Read(buffer, offset, count);
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken token = default) => stream.ReadAsync(buffer, token);
        public override void Flush() => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        protected override void Dispose(bool disposing)
        {
            if (disposing) { stream.Dispose(); mount.DisposeAsync().AsTask().GetAwaiter().GetResult(); }
            base.Dispose(disposing);
        }
        public override async ValueTask DisposeAsync() { await stream.DisposeAsync(); await mount.DisposeAsync(); GC.SuppressFinalize(this); }
    }
}
