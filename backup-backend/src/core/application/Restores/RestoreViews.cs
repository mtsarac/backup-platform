using BackupPlatform.Contracts.Restores;

namespace BackupPlatform.Application.Restores;

/// <summary>The target as the API shows it: references only. The credential is a name an agent resolves locally, never a secret.</summary>
public sealed record RestoreTargetView(string Host, int? Port, bool IntegratedSecurity, string? DatabaseCredentialReference, bool Encrypt, bool TrustServerCertificate);

public sealed record RestoreOptionsView(bool TerminateActiveConnections, bool VerifyChecksum, bool VerifyBackup, RestoreFileSelection? Files = null);

public sealed record RestoreProgressView(string? Stage, double? Percent, DateTimeOffset? At);

public sealed record RestoreAttemptView(Guid Id, int Number, string Status, DateTimeOffset CreatedAt, DateTimeOffset? LeasedAt, DateTimeOffset? StartedAt, DateTimeOffset? CompletedAt,
    string? FailureCode, string? Reason, string? TargetState, string? ProgressStage, double? ProgressPercent, DateTimeOffset? ProgressAt);

/// <summary>
/// The platform-created database an operation owes cleanup for. <see cref="DatabaseName"/> is shown because an operator may need to find a leftover; it is a
/// platform-generated name, never user data. <see cref="CanRequest"/> says whether an operator action (remove the rollback copy, retry now) is currently allowed.
/// </summary>
public sealed record RestoreCleanupView(string Kind, string State, string? DatabaseName, int Attempts, DateTimeOffset? NextAttemptAt, string? LastFailureCode, string? Reason,
    bool NeedsAttention, bool CanRequest, DateTimeOffset? RetainedUntil);

/// <summary>
/// One restore. <see cref="Reason"/> and <see cref="TargetStateSummary"/> are fixed sentences chosen by the backend from the failure code and the target
/// state: nothing an agent or SQL Server wrote is shown as such. <see cref="Result"/> (pre-check items, warnings, file mappings, header summary) is only filled
/// in the detail view. <see cref="VerificationOutcome"/> is <c>Restored</c>, <c>Failed</c> or <c>NotRun</c> for a verification that ended (null while it runs and for other purposes). A Cancelled or Failed restore says explicitly what it left behind in <see cref="TargetState"/>; it is never implied by the status.
/// </summary>
public sealed record RestoreView(Guid Id, Guid ArtifactId, Guid ReplicaId, Guid AgentId, string Status, string Mode, string OverwritePolicy, string SourceAccess, string DatabaseName,
    RestoreTargetView Target, RestoreOptionsView Options, DateTimeOffset CreatedAt, DateTimeOffset? StartedAt, DateTimeOffset? CompletedAt, DateTimeOffset? CancellationRequestedAt,
    string? FailureCode, string? Reason, string? TargetState, string? TargetStateSummary, RestoreProgressView Progress, bool ReplicaPinned, RestoreTaskResult? Result,
    IReadOnlyList<RestoreAttemptView> Attempts, string Purpose = "Execution", Guid? PrecheckId = null, RestoreCleanupView? Cleanup = null, string? VerificationOutcome = null);

public sealed record RestorePageView(IReadOnlyList<RestoreView> Items, long TotalCount, int Page, int PageSize);
