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
}
