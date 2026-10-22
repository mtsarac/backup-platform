using BackupPlatform.Domain.Backups;

namespace BackupPlatform.Domain.Restores;

/// <summary>
/// The SQL Server a restore writes to, as references only: the credential is a NAME the target agent resolves locally, never a secret, and
/// no filesystem path is part of it. Same vocabulary as <see cref="BackupConfiguration"/>, so one agent resolves both the same way.
/// </summary>
public sealed record RestoreTarget(string Host, int? Port, bool IntegratedSecurity, string? DatabaseCredentialReference, bool Encrypt, bool TrustServerCertificate)
{
    public RestoreTarget Validate()
    {
        BackupGuard.Text(Host, 255);
        if (Port is < 1 or > 65535) throw new ArgumentException("Invalid database port.");
        if (IntegratedSecurity ? DatabaseCredentialReference is not null : DatabaseCredentialReference is null)
            throw new ArgumentException("Authentication requires either integrated security or a credential reference.");
        if (DatabaseCredentialReference is not null)
        {
            BackupGuard.Reference(DatabaseCredentialReference);
            if (DatabaseCredentialReference.Length > 64) throw new ArgumentException("Invalid configuration reference.");
        }
        return this;
    }
}

/// <summary>
/// Safety knobs of one restore. Everything defaults to the careful choice: the bytes are re-verified and the backup re-validated before the
/// target is touched, and sessions on an existing target are never killed unless the user said so.
/// </summary>
public sealed record RestoreOptions(bool TerminateActiveConnections = false, bool VerifyChecksum = true, bool VerifyBackup = true)
{
    public static RestoreOptions Default { get; } = new();
    public FileRestorePolicy? Files { get; init; }
}

public sealed record FileRestorePolicy
{
    public IReadOnlyList<string>? Paths { get; init; }
    public string RelativeDirectory { get; init; } = "";

    public FileRestorePolicy Validate()
    {
        if (Paths is { Count: 0 or > 256 } || RelativeDirectory is null) throw new ArgumentException("Invalid file restore selection.");
        if (RelativeDirectory.Length > 0) FileBackupPolicy.ValidatePath(RelativeDirectory, allowRoot: true);
        foreach (var path in Paths ?? []) FileBackupPolicy.ValidatePath(path, allowRoot: true);
        return this;
    }

    public bool Equals(FileRestorePolicy? other) => other is not null && RelativeDirectory == other.RelativeDirectory
        && (Paths is null ? other.Paths is null : other.Paths is not null && Paths.SequenceEqual(other.Paths));
    public override int GetHashCode()
    {
        var hash = new HashCode();
        hash.Add(RelativeDirectory);
        if (Paths is not null) foreach (var path in Paths) hash.Add(path);
        return hash.ToHashCode();
    }
}
