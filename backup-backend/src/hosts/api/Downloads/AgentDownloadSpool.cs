using System.Collections.Concurrent;
using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;
using BackupPlatform.Domain.Downloads;
using BackupPlatform.Domain.Identity;
using BackupPlatform.StorageProviders.Abstractions;

namespace BackupPlatform.Api.Downloads;

/// <summary>Process-private verified copies, held by delete-on-close handles. A restart cannot reuse an unauthenticated or half-written spool.</summary>
public sealed class AgentDownloadSpool(TimeProvider time, ILogger<AgentDownloadSpool> logger) : IDisposable
{
    private readonly ConcurrentDictionary<Guid, Entry> _entries = new();
    private readonly ConcurrentDictionary<Guid, byte> _uploads = new();
    private readonly string _directory = CreateDirectory();

    public bool TryBeginUpload(Guid sessionId) => _uploads.TryAdd(sessionId, 0);
    public void EndUpload(Guid sessionId) => _uploads.TryRemove(sessionId, out _);

    public FileStream CreateFile()
    {
        var options = new FileStreamOptions
        {
            Mode = FileMode.CreateNew,
            Access = FileAccess.ReadWrite,
            Share = FileShare.Read | FileShare.Delete,
            Options = FileOptions.Asynchronous | FileOptions.SequentialScan | FileOptions.DeleteOnClose,
            BufferSize = 0
        };
        if (!OperatingSystem.IsWindows()) options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
        return new FileStream(Path.Combine(_directory, Guid.NewGuid().ToString("N")), options);
    }

    public bool Publish(DownloadSession session, FileStream file) =>
        _entries.TryAdd(session.Id, new Entry(session.TenantId, session.ExpiresAt, file));

    public void RefreshExpiry(DownloadSession session)
    {
        if (!_entries.TryGetValue(session.Id, out var entry)) return;
        lock (entry) entry.ExpiresAt = session.ExpiresAt;
    }

    public Stream OpenRead(DownloadSession session)
    {
        if (!_entries.TryGetValue(session.Id, out var entry) || entry.TenantId != session.TenantId || entry.ExpiresAt <= time.GetUtcNow())
        {
            throw new StorageException(StorageFailureKind.NotFound, "The prepared download is no longer available.");
        }
        lock (entry)
        {
            if (entry.Released) throw new StorageException(StorageFailureKind.NotFound, "The prepared download is no longer available.");
            return new FileStream(entry.File.Name, new FileStreamOptions
            {
                Mode = FileMode.Open,
                Access = FileAccess.Read,
                Share = FileShare.ReadWrite | FileShare.Delete,
                Options = FileOptions.Asynchronous | FileOptions.SequentialScan,
                BufferSize = 0
            });
        }
    }

    public void Release(Guid sessionId)
    {
        if (!_entries.TryRemove(sessionId, out var entry)) return;
        lock (entry)
        {
            entry.Released = true;
            entry.File.Dispose();
        }
    }

    public void SweepExpired()
    {
        var now = time.GetUtcNow();
        foreach (var entry in _entries)
        {
            if (entry.Value.ExpiresAt <= now) Release(entry.Key);
        }
    }

    public void Dispose()
    {
        foreach (var entry in _entries) Release(entry.Key);
        try { Directory.Delete(_directory); }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            logger.LogWarning("Download spool cleanup failed: Code=download_spool_cleanup_failed Type={ExceptionType}.", exception.GetType().Name);
        }
    }

    private static string CreateDirectory()
    {
        var directory = Path.Combine(Path.GetTempPath(), "backup-download-" + Guid.NewGuid().ToString("N"));
        if (OperatingSystem.IsWindows()) CreateWindowsDirectory(directory);
        else Directory.CreateDirectory(directory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        return directory;
    }

    [SupportedOSPlatform("windows")]
    private static void CreateWindowsDirectory(string path)
    {
        using var identity = WindowsIdentity.GetCurrent();
        var owner = identity.User ?? throw new InvalidOperationException("The download spool owner is unavailable.");
        var security = new DirectorySecurity();
        security.SetOwner(owner);
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        foreach (var principal in new[]
        {
            owner,
            new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null),
            new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null)
        })
        {
            security.AddAccessRule(new FileSystemAccessRule(principal, FileSystemRights.FullControl,
                InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow));
        }
        new DirectoryInfo(path).Create(security);
    }

    private sealed class Entry(TenantId tenantId, DateTimeOffset expiresAt, FileStream file)
    {
        public TenantId TenantId { get; } = tenantId;
        public DateTimeOffset ExpiresAt { get; set; } = expiresAt;
        public FileStream File { get; } = file;
        public bool Released { get; set; }
    }
}
