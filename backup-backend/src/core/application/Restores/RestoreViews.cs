using BackupPlatform.Contracts.Restores;

namespace BackupPlatform.Application.Restores;

/// <summary>The target as the API shows it: references only. The credential is a name an agent resolves locally, never a secret.</summary>
public sealed record RestoreTargetView(string Host, int? Port, bool IntegratedSecurity, string? DatabaseCredentialReference, bool Encrypt, bool TrustServerCertificate);

public sealed record RestoreOptionsView(bool TerminateActiveConnections, bool VerifyChecksum, bool VerifyBackup);

public sealed record RestoreProgressView(string? Stage, double? Percent, DateTimeOffset? At);

public sealed record RestoreAttemptView(Guid Id, int Number, string Status, DateTimeOffset CreatedAt, DateTimeOffset? LeasedAt, DateTimeOffset? StartedAt, DateTimeOffset? CompletedAt,
    string? FailureCode, string? Reason, string? TargetState, string? ProgressStage, double? ProgressPercent, DateTimeOffset? ProgressAt);

/// <summary>
/// One restore. <see cref="Reason"/> and <see cref="TargetStateSummary"/> are fixed sentences chosen by the backend from the failure code and the target
/// state: nothing an agent or SQL Server wrote is shown as such. <see cref="Result"/> (pre-check items, warnings, file mappings, header summary) is only filled
/// in the detail view. A Cancelled or Failed restore says explicitly what it left behind in <see cref="TargetState"/>; it is never implied by the status.
/// </summary>
public sealed record RestoreView(Guid Id, Guid ArtifactId, Guid ReplicaId, Guid AgentId, string Status, string Mode, string OverwritePolicy, string SourceAccess, string DatabaseName,
    RestoreTargetView Target, RestoreOptionsView Options, DateTimeOffset CreatedAt, DateTimeOffset? StartedAt, DateTimeOffset? CompletedAt, DateTimeOffset? CancellationRequestedAt,
    string? FailureCode, string? Reason, string? TargetState, string? TargetStateSummary, RestoreProgressView Progress, bool ReplicaPinned, RestoreTaskResult? Result,
    IReadOnlyList<RestoreAttemptView> Attempts, string Purpose = "Execution", Guid? PrecheckId = null);

public sealed record RestorePageView(IReadOnlyList<RestoreView> Items, long TotalCount, int Page, int PageSize);
