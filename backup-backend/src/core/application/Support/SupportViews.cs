using System.Text.Json;
using BackupPlatform.Application.Backups;
using BackupPlatform.Domain.Support;

namespace BackupPlatform.Application.Support;

public sealed record SupportSessionView(Guid Id, Guid TenantId, string? TenantName, Guid PlatformAdminId, string? ActorDisplayName, string Reason,
    IReadOnlyList<string> Permissions, DateTimeOffset StartedAt, DateTimeOffset ExpiresAt, string Status, DateTimeOffset? ClosedAt, Guid? ClosedBy,
    string? ClosedByDisplayName, string? CloseReason, DateTimeOffset CreatedAt, bool IsOwner, DateTimeOffset ServerNow);

public sealed record SupportAuditItemView(Guid Id, DateTimeOffset At, string Action, Guid ActorUserId, string? ActorDisplayName, string TargetType, string TargetId,
    string? Result, JsonElement Details);

public sealed record SupportActivityItemView(Guid Id, DateTimeOffset At, string Action, string TargetType, string TargetId, string? Result);

public sealed record SupportTenantSessionView(Guid Id, string? ActorDisplayName, string Reason, IReadOnlyList<string> Permissions, DateTimeOffset StartedAt, DateTimeOffset ExpiresAt,
    string Status, DateTimeOffset? ClosedAt, string? CloseReason, DateTimeOffset CreatedAt, SupportActivitySummary Summary);

/// <summary>A job as support sees it: names, state and shape only. No host, no credential reference, no storage prefix.</summary>
public sealed record SupportJobView(Guid Id, Guid AgentId, string? AgentName, string Name, string BackupType, bool Enabled, string Engine, string Database, string StorageProvider,
    BackupScheduleView? Schedule, BackupRetentionPolicyView? Retention, BackupLastRunView? LastRun);

public sealed record SupportAttemptView(int Number, string Status, DateTimeOffset CreatedAt, DateTimeOffset? StartedAt, DateTimeOffset? CompletedAt, string? ReasonCode, string? Reason);

public sealed record SupportRunView(Guid Id, Guid JobId, string? JobName, Guid AgentId, string? AgentName, string Status, string BackupType, string TriggerType, int CurrentAttemptNumber,
    DateTimeOffset CreatedAt, DateTimeOffset? StartedAt, DateTimeOffset? CompletedAt, string? ReasonCode, string? Reason, IReadOnlyList<SupportAttemptView> Attempts, IReadOnlyList<Guid> ArtifactIds);

/// <summary>A copy of a backup as support sees it. Deliberately has no storage target and no key: where the bytes live is the customer's, and support only ever reaches them through the download engine.</summary>
public sealed record SupportReplicaView(Guid Id, string StorageProvider, string ExecutionLocation, long SizeBytes, string Checksum, string ChecksumAlgorithm, string VerificationState,
    string VerificationMethod, DateTimeOffset CreatedAt, DateTimeOffset? VerifiedAt, string Status, bool Usable, DateTimeOffset? DeleteRequestedAt, DateTimeOffset? DeleteAfter, DateTimeOffset? DeletedAt);

public sealed record SupportArtifactView(Guid Id, long SizeBytes, string SourceChecksum, string ArtifactChecksum, string ChecksumAlgorithm, DateTimeOffset CreatedAt, string State,
    DateTimeOffset? AcceptedAt, DateTimeOffset? RetentionExpiresAt, DateTimeOffset? RetentionPinnedAt, DateTimeOffset? DeleteAfter, string? DeleteReason, Guid? RunId, Guid? JobId,
    string? JobName, string? DatabaseName, Guid? AgentId, string? AgentName, string BackupType, BackupMetadataView? BackupMetadata, IReadOnlyList<SupportReplicaView> Replicas);

public sealed record SupportDownloadView(Guid DownloadId, Guid ArtifactId, Guid ReplicaId, string State, string? FailureCode, long TransferredBytes, long SizeBytes, string Filename,
    DateTimeOffset CreatedAt, DateTimeOffset? ReleasedAt);

public static class SupportViews
{
    public static SupportSessionView ToView(SupportSessionRow row, Domain.Identity.UserId viewer, DateTimeOffset now)
    {
        var session = row.Session;
        return new SupportSessionView(session.Id, session.TenantId.Value, row.TenantName, session.PlatformAdminId.Value, row.AdminName, session.Reason,
            SupportPermissionNames.ToNames(session.Permissions), session.StartedAt, session.ExpiresAt, session.EffectiveStatus(now).ToString(), session.ClosedAt,
            session.ClosedBy?.Value, row.ClosedByName, session.CloseReason, session.CreatedAt, session.PlatformAdminId == viewer, now);
    }

    public static SupportTenantSessionView ToTenantView(SupportTenantSessionRow row, DateTimeOffset now)
    {
        var session = row.Row.Session;
        return new SupportTenantSessionView(session.Id, row.Row.AdminName, session.Reason, SupportPermissionNames.ToNames(session.Permissions), session.StartedAt, session.ExpiresAt,
            session.EffectiveStatus(now).ToString(), session.ClosedAt, session.CloseReason, session.CreatedAt, row.Summary);
    }

    public static SupportAuditItemView ToAuditView(SupportAuditRow row)
    {
        using var document = JsonDocument.Parse(row.DetailsJson);
        var details = document.RootElement.Clone();
        return new SupportAuditItemView(row.Id, row.At, row.Action, row.ActorUserId, row.ActorName, row.TargetType, row.TargetId, Result(details), details);
    }

    public static SupportActivityItemView ToActivityView(SupportAuditRow row)
    {
        using var document = JsonDocument.Parse(row.DetailsJson);
        return new SupportActivityItemView(row.Id, row.At, row.Action, row.TargetType, row.TargetId, Result(document.RootElement));
    }

    private static string? Result(JsonElement details) =>
        details.ValueKind == JsonValueKind.Object && details.TryGetProperty("result", out var result) && result.ValueKind == JsonValueKind.String ? result.GetString() : null;

    public static SupportJobView ToView(BackupJobView job) => new(job.Id, job.AgentId, job.AgentName, job.Name, job.BackupType, job.Enabled, job.Configuration.Engine,
        job.Configuration.Database, job.Configuration.Destinations.Single(d => d.IsPrimary).StorageProvider, job.Schedule, job.Retention, job.LastRun);

    public static SupportRunView ToView(BackupRunView run) => new(run.Id, run.JobId, run.JobName, run.AgentId, run.AgentName, run.Status, run.BackupType, run.TriggerType,
        run.CurrentAttemptNumber, run.CreatedAt, run.StartedAt, run.CompletedAt, run.ReasonCode, run.Reason,
        [.. run.Attempts.Select(attempt => new SupportAttemptView(attempt.Number, attempt.Status, attempt.CreatedAt, attempt.StartedAt, attempt.CompletedAt, attempt.ReasonCode, attempt.Reason))],
        [.. run.Attempts.SelectMany(attempt => attempt.Artifacts).Select(artifact => artifact.Id).Distinct()]);

    public static SupportArtifactView ToView(BackupArtifactView artifact) => new(artifact.Id, artifact.SizeBytes, artifact.SourceChecksum, artifact.ArtifactChecksum, artifact.ChecksumAlgorithm,
        artifact.CreatedAt, artifact.State, artifact.AcceptedAt, artifact.RetentionExpiresAt, artifact.RetentionPinnedAt, artifact.DeleteAfter, artifact.DeleteReason, artifact.RunId,
        artifact.JobId, artifact.JobName, artifact.DatabaseName, artifact.AgentId, artifact.AgentName, artifact.BackupType, artifact.BackupMetadata,
        [.. artifact.Replicas.Select(replica => new SupportReplicaView(replica.Id, replica.StorageProvider, replica.DeletionExecutor, replica.SizeBytes, replica.Checksum, replica.ChecksumAlgorithm,
            replica.VerificationState, replica.VerificationMethod, replica.CreatedAt, replica.VerifiedAt, replica.Status, replica.Usable, replica.DeleteRequestedAt, replica.DeleteAfter, replica.DeletedAt))]);
}
