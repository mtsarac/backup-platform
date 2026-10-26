using System.Security.Cryptography;
using BackupPlatform.Application.Backups;
using BackupPlatform.Domain.Storage;
using BackupPlatform.SecretProviders.Abstractions;
using BackupPlatform.StorageProviders.Abstractions;

namespace BackupPlatform.Storage;

/// <summary>Stable codes of a connection test, so a view can say why without repeating provider output.</summary>
public static class StorageTestCodes
{
    public const string Ok = "ok";
    public const string AccessVerified = "access_verified";
    public const string StorageNotConfigured = "storage_not_configured";
    public const string AgentSideStorage = "agent_side_storage";
    public const string ProviderNotImplemented = "provider_not_implemented";
    public const string SecretUnavailable = "secret_unavailable";
    public const string AccessDenied = "access_denied";
    public const string InvalidConfiguration = "invalid_configuration";
    public const string InsufficientSpace = "insufficient_space";
    public const string IoError = "io_error";
    public const string IntegrityMismatch = "integrity_mismatch";
    public const string NotSupported = "not_supported";
    public const string HostKeyMismatch = "host_key_mismatch";
    public const string AuthenticationFailed = "authentication_failed";
    public const string Timeout = "timeout";
    public const string NetworkDenied = "network_denied";
    public const string NetworkUnavailable = "network_unavailable";
    public const string Unexpected = "unexpected";
}

/// <summary>
/// Checks one provider type's connections. A tester claims success only for something it actually did against the real storage. A provider
/// without a tester (a planned one) is reported as failed with <see cref="StorageTestCodes.ProviderNotImplemented"/>, never as passing.
/// </summary>
public interface IStorageConnectionTester
{
    string ProviderType { get; }

    Task<StorageTestResult> TestAsync(StorageConnection connection, ISecretProvider secrets, CancellationToken cancellationToken);
}

/// <summary>
/// Agent-direct storage the backend cannot reach: the platform never looks, so it never says "ok". The first real check is the agent's own pre-check when a backup runs.
/// </summary>
public sealed class AgentSideConnectionTester(string providerType) : IStorageConnectionTester
{
    public string ProviderType { get; } = providerType;

    public Task<StorageTestResult> TestAsync(StorageConnection connection, ISecretProvider secrets, CancellationToken cancellationToken) =>
        Task.FromResult(new StorageTestResult(StorageTestStatus.NotTestable, StorageTestCodes.AgentSideStorage,
            "This storage is reached by the agent, not by the platform; it is checked by the agent when a backup runs."));
}

/// <summary>
/// The Central/Managed storage: the backend writes a small probe object, reads it back through verification, deletes it and checks it is gone,
/// through the very provider (and tenant scope) it uses for retention deletes and downloads. A tenant without a configured storage fails with
/// <see cref="StorageTestCodes.StorageNotConfigured"/>.
/// </summary>
public sealed class CentralConnectionTester(IBackupStorageResolver resolver) : IStorageConnectionTester
{
    private const string ProbeDirectory = "connection-tests";

    public string ProviderType => StorageProviderTypes.Central;

    public async Task<StorageTestResult> TestAsync(StorageConnection connection, ISecretProvider secrets, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(connection);
        var provider = resolver.Resolve(connection.TenantId, StorageProviderTypes.Central, StorageProviderTypes.Central);
        if (provider is null)
        {
            return Failed(StorageTestCodes.StorageNotConfigured, "The platform has no central storage configured for this tenant.");
        }
        if (!provider.Capabilities.Supports(StorageCapabilityIds.Delete))
        {
            return Failed(StorageTestCodes.NotSupported, "The storage cannot delete, so a probe could not be cleaned up.");
        }
        var content = RandomNumberGenerator.GetBytes(64);
        var key = new StorageObjectKey($"{ProbeDirectory}/{Guid.NewGuid():N}.probe");
        var checksum = ContentChecksum.Sha256(SHA256.HashData(content));
        var written = false;
        try
        {
            await using (var stream = new MemoryStream(content, writable: false))
            {
                await provider.PutAsync(key, stream, new StoragePutOptions(StorageOverwritePolicy.Fail, checksum, content.Length), null, cancellationToken).ConfigureAwait(false);
            }
            written = true;
            var verification = await provider.VerifyAsync(key, new StorageVerificationExpectation(content.Length, checksum), cancellationToken).ConfigureAwait(false);
            if (verification.Status != StorageVerificationStatus.Verified)
            {
                return Failed(StorageTestCodes.IntegrityMismatch, "The probe object did not read back as written.");
            }
            await provider.DeleteAsync(key, cancellationToken).ConfigureAwait(false);
            written = false;
            if (await provider.ExistsAsync(key, cancellationToken).ConfigureAwait(false))
            {
                return Failed(StorageTestCodes.IoError, "The probe object still exists after deletion.");
            }
            return new StorageTestResult(StorageTestStatus.Succeeded, StorageTestCodes.Ok, "Wrote, verified and deleted a probe object.");
        }
        catch (StorageException exception)
        {
            return Failed(CentralConnectionTester.CodeFor(exception.Kind), exception.Message);
        }
        finally
        {
            if (written)
            {
                // A probe that could not be verified or removed above must not stay behind. Best effort: the test already reports why.
                try { await provider.DeleteAsync(key, CancellationToken.None).ConfigureAwait(false); }
                catch (StorageException) { }
            }
        }
    }

    private static StorageTestResult Failed(string code, string message) => new(StorageTestStatus.Failed, code, message);

    internal static string CodeFor(StorageFailureKind kind) => kind switch
    {
        StorageFailureKind.AccessDenied => StorageTestCodes.AccessDenied,
        StorageFailureKind.InvalidConfiguration => StorageTestCodes.InvalidConfiguration,
        StorageFailureKind.InsufficientSpace => StorageTestCodes.InsufficientSpace,
        StorageFailureKind.IntegrityMismatch => StorageTestCodes.IntegrityMismatch,
        StorageFailureKind.NotSupported => StorageTestCodes.NotSupported,
        StorageFailureKind.Io or StorageFailureKind.NotFound or StorageFailureKind.AlreadyExists => StorageTestCodes.IoError,
        StorageFailureKind.HostKeyMismatch => StorageTestCodes.HostKeyMismatch,
        StorageFailureKind.AuthenticationFailed => StorageTestCodes.AuthenticationFailed,
        StorageFailureKind.Timeout => StorageTestCodes.Timeout,
        StorageFailureKind.NetworkDenied => StorageTestCodes.NetworkDenied,
        StorageFailureKind.NetworkUnavailable => StorageTestCodes.NetworkUnavailable,
        _ => StorageTestCodes.Unexpected
    };
}

/// <summary>
/// Tests an SFTP connection by writing, verifying and deleting an isolated probe object inside the tenant's remote base directory.
/// </summary>
public sealed class SftpConnectionTester(Func<StorageConnection, ISecretProvider, IStorageProvider> factory) : IStorageConnectionTester
{
    public string ProviderType => StorageProviderTypes.Sftp;

    public async Task<StorageTestResult> TestAsync(StorageConnection connection, ISecretProvider secrets, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(connection);
        IStorageProvider provider;
        try
        {
            provider = factory(connection, secrets);
        }
        catch (StorageException ex)
        {
            return new StorageTestResult(StorageTestStatus.Failed, CodeFor(ex.Kind), ex.Message);
        }
        catch (Exception)
        {
            return new StorageTestResult(StorageTestStatus.Failed, StorageTestCodes.InvalidConfiguration, "The storage connection configuration is invalid.");
        }

        var content = RandomNumberGenerator.GetBytes(64);
        var key = new StorageObjectKey($"tenants/{connection.TenantId.Value:N}/connection-tests/{Guid.NewGuid():N}.probe");
        var checksum = ContentChecksum.Sha256(SHA256.HashData(content));
        var written = false;
        try
        {
            await using (var stream = new MemoryStream(content, writable: false))
            {
                await provider.PutAsync(key, stream, new StoragePutOptions(StorageOverwritePolicy.Fail, checksum, content.Length), null, cancellationToken).ConfigureAwait(false);
            }
            written = true;
            var verification = await provider.VerifyAsync(key, new StorageVerificationExpectation(content.Length, checksum), cancellationToken).ConfigureAwait(false);
            if (verification.Status != StorageVerificationStatus.Verified)
            {
                return new StorageTestResult(StorageTestStatus.Failed, StorageTestCodes.IntegrityMismatch, "The probe object did not read back as written.");
            }
            await provider.DeleteAsync(key, cancellationToken).ConfigureAwait(false);
            written = false;
            if (await provider.ExistsAsync(key, cancellationToken).ConfigureAwait(false))
            {
                return new StorageTestResult(StorageTestStatus.Failed, StorageTestCodes.IoError, "The probe object still exists after deletion.");
            }
            return new StorageTestResult(StorageTestStatus.Succeeded, StorageTestCodes.Ok, "Wrote, verified and deleted a probe object.");
        }
        catch (StorageException ex) when (ex.Kind == StorageFailureKind.NetworkDenied)
        {
            // A private address is the customer's own device: the server may not reach it, the agent on that network does.
            return new StorageTestResult(StorageTestStatus.NotTestable, StorageTestCodes.AgentSideStorage,
                "This storage is on a private network; it is reached by the agent and checked by the agent when a backup runs.");
        }
        catch (StorageException ex)
        {
            return new StorageTestResult(StorageTestStatus.Failed, CodeFor(ex.Kind), ex.Message);
        }
        finally
        {
            if (written)
            {
                try { await provider.DeleteAsync(key, CancellationToken.None).ConfigureAwait(false); }
                catch (StorageException) { }
            }
        }
    }

    private static string CodeFor(StorageFailureKind kind) => CentralConnectionTester.CodeFor(kind);
}

/// <summary>
/// Tests a Google Drive connection by verifying OAuth credentials, folder access, probe upload, verification and deletion.
/// </summary>
public sealed class GoogleDriveConnectionTester(Func<StorageConnection, ISecretProvider, IStorageProvider> factory) : IStorageConnectionTester
{
    public string ProviderType => StorageProviderTypes.GoogleDrive;

    public async Task<StorageTestResult> TestAsync(StorageConnection connection, ISecretProvider secrets, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(connection);
        if (!connection.HasSecret)
        {
            return new StorageTestResult(StorageTestStatus.Failed, StorageTestCodes.SecretUnavailable, "Google Drive account is not connected. Reauthorization required.");
        }

        IStorageProvider provider;
        try
        {
            provider = factory(connection, secrets);
        }
        catch (StorageException ex)
        {
            return new StorageTestResult(StorageTestStatus.Failed, CodeFor(ex.Kind), ex.Message);
        }
        catch (Exception)
        {
            return new StorageTestResult(StorageTestStatus.Failed, StorageTestCodes.InvalidConfiguration, "The storage connection configuration is invalid.");
        }

        var content = RandomNumberGenerator.GetBytes(64);
        var key = new StorageObjectKey($"{connection.TenantId.Value:N}/connection-tests/{Guid.NewGuid():N}.probe");
        var checksum = ContentChecksum.Sha256(SHA256.HashData(content));
        var written = false;
        try
        {
            await using (var stream = new MemoryStream(content, writable: false))
            {
                await provider.PutAsync(key, stream, new StoragePutOptions(StorageOverwritePolicy.Fail, checksum, content.Length), null, cancellationToken).ConfigureAwait(false);
            }
            written = true;
            var verification = await provider.VerifyAsync(key, new StorageVerificationExpectation(content.Length, checksum), cancellationToken).ConfigureAwait(false);
            if (verification.Status != StorageVerificationStatus.Verified)
            {
                return new StorageTestResult(StorageTestStatus.Failed, StorageTestCodes.IntegrityMismatch, "The probe object did not read back as written.");
            }
            await provider.DeleteAsync(key, cancellationToken).ConfigureAwait(false);
            written = false;
            if (await provider.ExistsAsync(key, cancellationToken).ConfigureAwait(false))
            {
                return new StorageTestResult(StorageTestStatus.Failed, StorageTestCodes.IoError, "The probe object still exists after deletion.");
            }
            return new StorageTestResult(StorageTestStatus.Succeeded, StorageTestCodes.Ok, "Verified Google Drive credentials, folder access, upload, read-back and deletion.");
        }
        catch (StorageException ex)
        {
            return new StorageTestResult(StorageTestStatus.Failed, CodeFor(ex.Kind), ex.Message);
        }
        finally
        {
            if (written)
            {
                try { await provider.DeleteAsync(key, CancellationToken.None).ConfigureAwait(false); }
                catch (StorageException) { }
            }
        }
    }

    private static string CodeFor(StorageFailureKind kind) => CentralConnectionTester.CodeFor(kind);
}

public sealed class S3ConnectionTester(Func<StorageConnection, ISecretProvider, IStorageProvider> factory) : IStorageConnectionTester
{
    public string ProviderType => StorageProviderTypes.S3;
    public async Task<StorageTestResult> TestAsync(StorageConnection connection, ISecretProvider secrets, CancellationToken cancellationToken)
    {
        try
        {
            if (factory(connection, secrets) is not IS3AccessChecker checker)
                return new(StorageTestStatus.Failed, StorageTestCodes.IoError, "The S3 provider cannot verify bucket access.");
            await checker.CheckAccessAsync(cancellationToken);
            return new(StorageTestStatus.Succeeded, StorageTestCodes.AccessVerified, StorageTestMessages.For(StorageTestCodes.AccessVerified));
        }
        catch (StorageException error)
        {
            return new(StorageTestStatus.Failed, CentralConnectionTester.CodeFor(error.Kind), error.Message);
        }
    }
}
