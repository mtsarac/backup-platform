using System.Runtime.Versioning;
using BackupPlatform.Agent.Core.Identity;

namespace BackupPlatform.Agent.Platform.Linux.Identity;

/// <summary>
/// Stores the agent identity in an owner-only file, because a system service has no desktop keyring. The protection is
/// filesystem permissions, not encryption: it defends against other local users, not against root or a root-equivalent
/// compromise, and not against someone who copies the disk of a machine where the file is readable.
/// </summary>
/// <remarks>
/// Controls: the directory must be mode 0700 and the file 0600 (a laxer mode is rejected on load rather than silently
/// repaired); the file is created with its final mode so no readable window exists; replacement writes a new
/// exclusive-create temp file in the same directory, flushes it to disk, then renames over the target, which is atomic
/// on POSIX and replaces (never follows) a symlink at the target path; the store refuses a symlinked directory or file.
/// Limits: parent directories above the store directory are trusted to be root-controlled, a symlink swapped between
/// the check and the open is not excluded without O_NOFOLLOW, and the parent directory is not fsynced after rename.
/// </remarks>
[UnsupportedOSPlatform("windows")]
public sealed class LinuxAgentCredentialStore : IAgentCredentialStore
{
    public const string DefaultDirectory = "/var/lib/backup-agent";
    private const string FileName = "agent-identity.json";
    private const UnixFileMode DirectoryMode = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute;
    private const UnixFileMode FileMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
    private readonly string _directory;
    private readonly string _path;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public LinuxAgentCredentialStore(string directory = DefaultDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        if (!Path.IsPathFullyQualified(directory))
        {
            throw new ArgumentException("The store directory must be an absolute path.", nameof(directory));
        }
        _directory = Path.TrimEndingDirectorySeparator(directory);
        _path = Path.Combine(_directory, FileName);
    }

    public async Task<AgentLocalIdentity?> LoadAsync(CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var directory = new DirectoryInfo(_directory);
            if (!directory.Exists && directory.LinkTarget is null)
            {
                return null;
            }
            VerifyDirectory(directory);
            var file = new FileInfo(_path);
            if (file.LinkTarget is not null)
            {
                throw new AgentCredentialStoreException("The agent identity file must not be a symbolic link.");
            }
            if (!file.Exists)
            {
                return null;
            }
            if ((file.Attributes & FileAttributes.Directory) != 0 || file.UnixFileMode != FileMode)
            {
                throw new AgentCredentialStoreException("The agent identity file must be a regular file with mode 0600.");
            }
            var data = await File.ReadAllBytesAsync(_path, cancellationToken).ConfigureAwait(false);
            return AgentLocalIdentitySerializer.Deserialize(data);
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
        var data = AgentLocalIdentitySerializer.Serialize(identity);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        string? temporary = null;
        try
        {
            EnsureDirectory();
            RemoveStaleTemporaryFiles();
            temporary = Path.Combine(_directory, $".{FileName}.{Guid.NewGuid():N}.tmp");
            var options = new FileStreamOptions
            {
                Mode = System.IO.FileMode.CreateNew,
                Access = FileAccess.Write,
                Share = FileShare.None,
                UnixCreateMode = FileMode,
                Options = FileOptions.WriteThrough
            };
            await using (var stream = new FileStream(temporary, options))
            {
                await stream.WriteAsync(data, cancellationToken).ConfigureAwait(false);
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
                stream.Flush(flushToDisk: true);
            }
            File.SetUnixFileMode(temporary, FileMode);
            File.Move(temporary, _path, overwrite: true);
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
                TryDelete(temporary);
            }
            System.Security.Cryptography.CryptographicOperations.ZeroMemory(data);
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
                VerifyDirectory(new DirectoryInfo(_directory));
                File.Delete(_path);
                RemoveStaleTemporaryFiles();
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
        if (directory.Exists || directory.LinkTarget is not null)
        {
            VerifyDirectory(directory);
            return;
        }
        Directory.CreateDirectory(_directory, DirectoryMode);
        File.SetUnixFileMode(_directory, DirectoryMode);
        VerifyDirectory(new DirectoryInfo(_directory));
    }

    private static void VerifyDirectory(DirectoryInfo directory)
    {
        if (directory.LinkTarget is not null)
        {
            throw new AgentCredentialStoreException("The agent identity directory must not be a symbolic link.");
        }
        if (!directory.Exists)
        {
            throw new AgentCredentialStoreException("The agent identity directory path is not a directory.");
        }
        if ((directory.UnixFileMode & (UnixFileMode.GroupRead | UnixFileMode.GroupWrite | UnixFileMode.GroupExecute
            | UnixFileMode.OtherRead | UnixFileMode.OtherWrite | UnixFileMode.OtherExecute)) != 0)
        {
            throw new AgentCredentialStoreException("The agent identity directory must be accessible by its owner only (mode 0700).");
        }
    }

    private void RemoveStaleTemporaryFiles()
    {
        foreach (var stale in Directory.EnumerateFiles(_directory, $".{FileName}.*.tmp"))
        {
            TryDelete(stale);
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
