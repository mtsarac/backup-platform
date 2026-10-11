using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using BackupPlatform.Contracts.Backups;

namespace BackupPlatform.Infrastructure.Backups;

public static class BackupTransferLock
{
    public static long Id(Guid connectionId, string key)
    {
        var slash = key.LastIndexOf('/');
        var name = key[(slash + 1)..];
        if (name.StartsWith('.') && name.EndsWith(".partial", StringComparison.Ordinal) &&
            ManagedBackupLayout.IsManagedArtifactName(name[1..^8]))
            key = key[..(slash + 1)] + name[1..^8];
        return BinaryPrimitives.ReadInt64LittleEndian(SHA256.HashData(Encoding.UTF8.GetBytes($"{connectionId:N}:{key}")));
    }
}
