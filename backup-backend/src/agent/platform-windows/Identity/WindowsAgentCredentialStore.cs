using System.Runtime.Versioning;
using System.Security.Cryptography;
using BackupPlatform.Agent.Core.Identity;

namespace BackupPlatform.Agent.Platform.Windows.Identity;

/// <summary>
/// Stores the agent identity encrypted with DPAPI under the account the process runs as (CurrentUser scope: for a Windows Service
/// that is the service identity) in a directory reachable only by that account, SYSTEM and Administrators. Another standard
/// account, or the disk read offline, cannot decrypt it; a local Administrator or code running as the service account can, which
/// DPAPI cannot prevent.
/// </summary>
/// <remarks>
/// Replacement writes a temp file in the same directory and swaps it in with File.Move(overwrite), so a crash leaves
/// either the old or the new blob and a concurrent reader is never blocked. Application entropy binds the blob to this store format.
/// The location is never trusted by assumption: no component of the path may be a reparse point, and an existing directory or file
/// must be owned and accessible only by trusted accounts (see <see cref="WindowsStoreDirectory"/>); anything else is repaired
/// where that cannot widen access and refused otherwise.
/// </remarks>
[SupportedOSPlatform("windows")]
public sealed class WindowsAgentCredentialStore : IAgentCredentialStore
{
    /// <summary>Application entropy that binds a blob to this store format. Internal so tests can tell "DPAPI refused" from "the file was unreadable".</summary>
    internal static readonly byte[] Entropy = "backup-platform/agent/identity/v1"u8.ToArray();
    private readonly WindowsStoreDirectory _directory;
    private readonly SemaphoreSlim _gate = new(1, 1);

    /// <param name="directory">Absolute directory, normally under %ProgramData%.</param>
    public WindowsAgentCredentialStore(string directory)
    {
        _directory = new WindowsStoreDirectory(directory);
    }

    public static string DefaultDirectory => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "BackupAgent");

    public async Task<AgentLocalIdentity?> LoadAsync(CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!_directory.Prepare(create: false))
            {
                return null;
            }
            // A link (even a dangling one) is refused, never mistaken for "no identity stored".
            if (WindowsStoreDirectory.IsReparsePoint(_directory.FilePath) is null)
            {
                return null;
            }
            _directory.VerifyIdentityFile(_directory.FilePath);
            byte[] protectedData;
            // FileShare.Delete lets a concurrent SaveAsync (another process) swap the file in while this read is open.
            await using (var stream = new FileStream(_directory.FilePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 1, useAsync: true))
            {
                protectedData = new byte[stream.Length];
                await stream.ReadExactlyAsync(protectedData, cancellationToken).ConfigureAwait(false);
            }
            byte[] plain;
            try
            {
                plain = ProtectedData.Unprotect(protectedData, Entropy, DataProtectionScope.CurrentUser);
            }
            catch (CryptographicException exception)
            {
                throw new AgentCredentialStoreException("The agent identity could not be decrypted for this account.", exception);
            }
            try
            {
                return AgentLocalIdentitySerializer.Deserialize(plain);
            }
            finally
            {
                CryptographicOperations.ZeroMemory(plain);
            }
        }
        catch (IOException exception)
        {
            throw new AgentCredentialStoreException("The agent identity file could not be read.", exception);
        }
        catch (UnauthorizedAccessException exception)
        {
            throw new AgentCredentialStoreException("Access to the agent identity file was denied.", exception);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task SaveAsync(AgentLocalIdentity identity, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(identity);
        var plain = AgentLocalIdentitySerializer.Serialize(identity);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        string? temporary = null;
        try
        {
            _directory.Prepare(create: true);
            var protectedData = ProtectedData.Protect(plain, Entropy, DataProtectionScope.CurrentUser);
            temporary = Path.Combine(_directory.Location, $"{WindowsStoreDirectory.TemporaryPrefix}{Guid.NewGuid():N}{WindowsStoreDirectory.TemporarySuffix}");
            await using (var stream = new FileStream(temporary, new FileStreamOptions
            {
                Mode = FileMode.CreateNew,
                Access = FileAccess.Write,
                Share = FileShare.None,
                Options = FileOptions.WriteThrough
            }))
            {
                await stream.WriteAsync(protectedData, cancellationToken).ConfigureAwait(false);
                stream.Flush(flushToDisk: true);
            }
            // A link where the identity file belongs is refused: File.Move would replace the link itself, silently discarding evidence of tampering.
            if (WindowsStoreDirectory.IsReparsePoint(_directory.FilePath) is true)
            {
                throw new AgentCredentialStoreException("The agent identity location must not be a symbolic link or junction.");
            }
            await MoveIntoPlaceAsync(temporary, _directory.FilePath, cancellationToken).ConfigureAwait(false);
            temporary = null;
        }
        catch (IOException exception)
        {
            throw new AgentCredentialStoreException("The agent identity file could not be written.", exception);
        }
        catch (UnauthorizedAccessException exception)
        {
            throw new AgentCredentialStoreException("Access to the agent identity directory was denied.", exception);
        }
        finally
        {
            if (temporary is not null)
            {
                try
                {
                    File.Delete(temporary);
                }
                catch (IOException)
                {
                }
            }
            CryptographicOperations.ZeroMemory(plain);
            _gate.Release();
        }
    }

    public async Task DeleteAsync(CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_directory.Prepare(create: false))
            {
                if (WindowsStoreDirectory.IsReparsePoint(_directory.FilePath) is true)
                {
                    throw new AgentCredentialStoreException("The agent identity location must not be a symbolic link or junction.");
                }
                File.Delete(_directory.FilePath);
            }
        }
        catch (IOException exception)
        {
            throw new AgentCredentialStoreException("The agent identity file could not be removed.", exception);
        }
        catch (UnauthorizedAccessException exception)
        {
            throw new AgentCredentialStoreException("Access to the agent identity file was denied.", exception);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// Swaps the new file in. When two writers replace the same file at once (two processes, or two store instances) Windows can refuse
    /// one of the moves for a moment, with a sharing violation or access denied, while the other swap completes; the move is therefore
    /// retried briefly. A refusal that does not go away (a real permission problem) is reported after the last attempt.
    /// </summary>
    private static async Task MoveIntoPlaceAsync(string source, string destination, CancellationToken cancellationToken)
    {
        const int attempts = 25;
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                File.Move(source, destination, overwrite: true);
                return;
            }
            catch (Exception exception) when (attempt < attempts && exception is IOException or UnauthorizedAccessException)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(Random.Shared.Next(10, 60)), cancellationToken).ConfigureAwait(false);
            }
        }
    }
}
