namespace BackupPlatform.Application.Restores;

/// <summary>
/// The SQL Server a restore writes to, as the caller states it: references only. <see cref="DatabaseCredentialReference"/> is the NAME of a credential the
/// target agent resolves locally; no secret and no filesystem path ever crosses the API.
/// </summary>
public sealed record RestoreTargetRequest(string Host, int? Port = null, bool IntegratedSecurity = false, string? DatabaseCredentialReference = null,
    bool Encrypt = true, bool TrustServerCertificate = false);

/// <summary>Omitted members take the careful default: verify the bytes, verify the backup, never kill sessions on an existing target.</summary>
public sealed record RestoreOptionsRequest(bool? TerminateActiveConnections = null, bool? VerifyChecksum = null, bool? VerifyBackup = null);

/// <summary>
/// Restores one backup artifact. <paramref name="ReplicaId"/> null lets the platform choose the best eligible replica for <paramref name="AgentId"/>.
/// <paramref name="Mode"/> is <c>NewDatabase</c> (default; can never touch an existing database) or <c>ReplaceExistingDatabase</c>, which needs
/// <paramref name="ConfirmOverwrite"/> to repeat the database name exactly.
/// </summary>
public sealed record StartRestoreRequest(Guid AgentId, RestoreTargetRequest Target, string DatabaseName, Guid? ReplicaId = null, string? Mode = null,
    string? ConfirmOverwrite = null, RestoreOptionsRequest? Options = null, Guid? PrecheckId = null);

public sealed record StartRestorePrecheckRequest(Guid AgentId, RestoreTargetRequest Target, string DatabaseName, Guid? ReplicaId = null, string? Mode = null,
    RestoreOptionsRequest? Options = null);

public sealed record StartRestoreDiscoveryRequest(Guid AgentId, RestoreTargetRequest Target, Guid? ReplicaId = null);

/// <summary>A request field is invalid. Carries the field and a stable code, never the offending value or a parser message.</summary>
public sealed class InvalidRestoreRequestException(string field, string code = "invalid_value") : ArgumentException("The restore request field is invalid.")
{
    public string Field { get; } = field;
    public string Code { get; } = code;
}
