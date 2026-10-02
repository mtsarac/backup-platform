using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using BackupPlatform.Agent.Core.Identity;

namespace BackupPlatform.Agent.Platform.Windows.Identity;

/// <summary>
/// Stores the agent identity encrypted with DPAPI under the service account (CurrentUser scope) in a directory whose
/// ACL is limited to that account, SYSTEM and Administrators. Another standard user, or the disk read offline, cannot
/// decrypt it; a local Administrator or code running as the service account can, which DPAPI cannot prevent.
/// </summary>
/// <remarks>
/// Replacement writes a temp file in the same directory and swaps it in with File.Replace/Move, so a crash leaves
/// either the old or the new blob. Application entropy binds the blob to this store format. Reparse points
/// (symlinks, junctions) at the directory or file are refused.
/// </remarks>
[SupportedOSPlatform("windows")]
public sealed class WindowsAgentCredentialStore : IAgentCredentialStore
{
    private const string FileName = "agent-identity.bin";
    private static readonly byte[] Entropy = "backup-platform/agent/identity/v1"u8.ToArray();
    private readonly string _directory;
    private readonly string _path;
    private readonly SemaphoreSlim _gate = new(1, 1);

    /// <param name="directory">Absolute directory, normally under %ProgramData%.</param>
    public WindowsAgentCredentialStore(string directory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        if (!Path.IsPathFullyQualified(directory))
        {
            throw new ArgumentException("The store directory must be an absolute path.", nameof(directory));
        }
        _directory = Path.TrimEndingDirectorySeparator(directory);
        _path = Path.Combine(_directory, FileName);
    }

    public static string DefaultDirectory => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "BackupAgent");

    public async Task<AgentLocalIdentity?> LoadAsync(CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!Directory.Exists(_directory))
            {
                return null;
            }
            VerifyNotReparsePoint(new DirectoryInfo(_directory));
            var file = new FileInfo(_path);
            if (!file.Exists)
            {
                return null;
            }
            VerifyNotReparsePoint(file);
            var protectedData = await File.ReadAllBytesAsync(_path, cancellationToken).ConfigureAwait(false);
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
            EnsureDirectory();
            var protectedData = ProtectedData.Protect(plain, Entropy, DataProtectionScope.CurrentUser);
            temporary = Path.Combine(_directory, $".{FileName}.{Guid.NewGuid():N}.tmp");
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
            if (File.Exists(_path))
            {
                VerifyNotReparsePoint(new FileInfo(_path));
                File.Replace(temporary, _path, destinationBackupFileName: null, ignoreMetadataErrors: true);
            }
            else
            {
                File.Move(temporary, _path);
            }
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
            if (Directory.Exists(_directory))
            {
                VerifyNotReparsePoint(new DirectoryInfo(_directory));
                File.Delete(_path);
            }
        }
        catch (IOException exception)
        {
            throw new AgentCredentialStoreException("The agent identity file could not be removed.", exception);
        }
        finally
        {
            _gate.Release();
        }
    }

    private void EnsureDirectory()
    {
        var directory = new DirectoryInfo(_directory);
        if (!directory.Exists)
        {
            var security = new DirectorySecurity();
            security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
            var rights = FileSystemRights.FullControl;
            var inheritance = InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit;
            using var current = WindowsIdentity.GetCurrent();
            security.AddAccessRule(new FileSystemAccessRule(current.User!, rights, inheritance, PropagationFlags.None, AccessControlType.Allow));
            security.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null), rights, inheritance, PropagationFlags.None, AccessControlType.Allow));
            security.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null), rights, inheritance, PropagationFlags.None, AccessControlType.Allow));
            directory.Create(security);
        }
        VerifyNotReparsePoint(new DirectoryInfo(_directory));
    }

    private static void VerifyNotReparsePoint(FileSystemInfo entry)
    {
        if ((entry.Attributes & FileAttributes.ReparsePoint) != 0)
        {
            throw new AgentCredentialStoreException("The agent identity location must not be a symbolic link or junction.");
        }
    }
}
