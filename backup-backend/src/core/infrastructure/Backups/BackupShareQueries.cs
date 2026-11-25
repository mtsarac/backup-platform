using System.Text.Json;
using BackupPlatform.Domain.Downloads;
using BackupPlatform.Application.Backups;
using BackupPlatform.Application.Downloads;
using BackupPlatform.Application.Lists;
using BackupPlatform.Application.Platform;
using BackupPlatform.Domain.Audit;
using BackupPlatform.Domain.Backups;
using BackupPlatform.Domain.Identity;
using BackupPlatform.Domain.Tenancy;
using BackupPlatform.Infrastructure.Downloads;
using BackupPlatform.Infrastructure.Lists;
using BackupPlatform.Domain.Notifications;
using BackupPlatform.Infrastructure.Notifications;
using BackupPlatform.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace BackupPlatform.Infrastructure.Backups;

/// <summary>Queries and row helpers the tenant and platform-operations stores share.</summary>
internal static class BackupShareQueries
{
    internal static async Task<BackupShareCreateResult> CreateCoreAsync(
        PlatformDbContext context,
        TenantId tenantId,
        Guid artifactId,
        UserId createdByUserId,
        UserRole createdByRole,
        TimeSpan duration,
        string? reason,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        await using var transaction = await context.Database.BeginTransactionAsync(cancellationToken);

        var artifact = await context.BackupArtifacts
            .Include(a => a.Replicas)
            .SingleOrDefaultAsync(a => a.TenantId == tenantId && a.Id == artifactId, cancellationToken);

        if (artifact is null)
        {
            return new BackupShareCreateResult(
                BackupShareCreateOutcome.ArtifactNotFound,
                ErrorCode: "artifact_not_found",
                ErrorMessage: "Artifact was not found.");
        }

        var hasDownloadableReplica = artifact.Replicas.Any(r =>
            r.Status == BackupReplicaStatus.Available &&
            r.VerificationState == "Verified" &&
            r.Checksum == artifact.ArtifactChecksum &&
            r.SizeBytes == artifact.SizeBytes);

        if (!hasDownloadableReplica)
        {
            return new BackupShareCreateResult(
                BackupShareCreateOutcome.NoDownloadableReplica,
                ErrorCode: "no_downloadable_replica",
                ErrorMessage: "A share can be created only when the artifact has at least one downloadable replica that is Verified and Available.");
        }

        var createdAtUtc = now.ToUniversalTime();
        var expiresAtUtc = createdAtUtc + duration;
        var share = new BackupShare(
            Guid.NewGuid(),
            tenantId,
            artifactId,
            createdByUserId,
            createdByRole,
            createdAtUtc,
            expiresAtUtc,
            reason);

        context.BackupShares.Add(share);

        var actorKind = createdByRole == UserRole.PlatformAdmin
            ? AuditActorKind.PlatformAdmin
            : AuditActorKind.UserAdmin;

        context.RecordAudit(AuditEvent.Record(
            createdAtUtc,
            createdByUserId,
            actorKind,
            AuditActions.BackupShareCreated,
            tenantId,
            "backup_share",
            share.Id.ToString("D"),
            new Dictionary<string, object?>
            {
                ["actor"] = createdByUserId.ToString(),
                ["role"] = createdByRole.ToString(),
                ["actorRole"] = createdByRole.ToString(),
                ["tenant"] = tenantId.Value.ToString("D"),
                ["artifact"] = artifactId.ToString("D"),
                ["expiry"] = share.ExpiresAtUtc.ToString("o"),
                ["reason"] = share.Reason,
                ["result"] = "created"
            }));

        var artifactNames = await ResolveArtifactNamesAsync(context, [artifactId], cancellationToken, tenantId);
        await RecordShareNotificationAsync(
            context.PlatformSide(),
            share,
            NotificationEventTypes.BackupShareCreated,
            $"{NotificationEventTypes.BackupShareCreated}:{share.Id:N}",
            artifactNames.GetValueOrDefault(artifactId),
            createdByUserId,
            createdByRole,
            platformAdmins: createdByRole == UserRole.UserAdmin,
            tenantAdmins: createdByRole == UserRole.PlatformAdmin,
            createdAtUtc,
            cancellationToken);

        await context.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        var userNames = await ResolveUserNamesAsync(context, [createdByUserId], cancellationToken);
        var tenantNames = await ResolveTenantNamesAsync(context, [tenantId], cancellationToken);

        return new BackupShareCreateResult(
            BackupShareCreateOutcome.Success,
            Share: ToView(
                share,
                now,
                artifactNames.GetValueOrDefault(artifactId),
                userNames.GetValueOrDefault(createdByUserId),
                tenantNames.GetValueOrDefault(tenantId)));
    }

    internal const string RefusedAction = "download.refused";

    /// <summary>
    /// One page of download attempts, newest first by default. An attempt is a download session, or a refusal that happened before any session existed.
    /// A refusal of a prepared session carries that session's id and is shown as the session's outcome, never matched by time, user or artifact.
    /// Both sources are one SQL union, so search, sort and paging see every attempt.
    /// </summary>
    internal static async Task<PagedResult<DownloadAttemptView>> ListDownloadAttemptsCoreAsync(
        PlatformDbContext context,
        DownloadAttemptFilter filter,
        bool tenantScoped,
        ListQuery query,
        CancellationToken cancellationToken)
    {

        var rows = context.Database.SqlQueryRaw<AttemptRow>(AttemptsSql).AsNoTracking();
        if (filter.TenantId is { } tenantId) rows = rows.Where(r => r.TenantId == tenantId.Value);
        if (filter.ShareId is { } shareId) rows = rows.Where(r => r.ShareId == shareId);
        if (filter.ArtifactId is { } artifactId) rows = rows.Where(r => r.ArtifactId == artifactId);
        if (filter.InitiatingUserId is { } userId) rows = rows.Where(r => r.UserId == userId);
        if (!string.IsNullOrWhiteSpace(filter.Result))
        {
            var result = filter.Result.Trim();
            var wanted = string.Equals(result, "Refused", StringComparison.OrdinalIgnoreCase) ? "Refused" : Enum.Parse<DownloadState>(result, ignoreCase: true).ToString();
            rows = rows.Where(r => r.Result == wanted);
        }

        var page = await rows
            .ApplySearch(query, r => r.ArtifactName, r => r.UserName, r => r.TenantName, r => r.Result)
            .ApplySort(AttemptSorts, query, "startedAt", r => r.Id)
            .ToPagedResultAsync(query, cancellationToken);

        // A tenant never learns the id of a share that is not its own, even when someone used it against its artifact.
        HashSet<Guid>? tenantShareIds = tenantScoped && page.Items.Any(r => r.ShareId is not null)
            ? [.. await context.BackupShares.AsNoTracking().Where(s => s.TenantId == filter.TenantId).Select(s => s.Id).ToListAsync(cancellationToken)]
            : null;
        var items = page.Items.Select(r => new DownloadAttemptView(
            r.Id, r.ShareId is { } id && tenantShareIds is not null && !tenantShareIds.Contains(id) ? null : r.ShareId,
            r.TenantId, r.TenantName, r.ArtifactId, r.ArtifactName, r.UserId, r.UserName, r.StartedAtUtc, r.EndedAtUtc, r.Result, r.Bytes, r.FailureReason, r.Decrypted)).ToList();
        return new PagedResult<DownloadAttemptView>(items, page.TotalCount, page.Page, page.PageSize);
    }

    internal sealed class AttemptRow
    {
        public Guid Id { get; set; }
        public Guid? ShareId { get; set; }
        public Guid TenantId { get; set; }
        public string? TenantName { get; set; }
        public Guid ArtifactId { get; set; }
        public string? ArtifactName { get; set; }
        public Guid UserId { get; set; }
        public string? UserName { get; set; }
        public DateTimeOffset StartedAtUtc { get; set; }
        public DateTimeOffset? EndedAtUtc { get; set; }
        public string Result { get; set; } = "";
        public long Bytes { get; set; }
        public string? FailureReason { get; set; }
        public bool Decrypted { get; set; }
    }

    internal static readonly SortMap<AttemptRow> AttemptSorts = SortMap<AttemptRow>.Create()
        .Date("startedAt", r => r.StartedAtUtc)
        .Date("endedAt", r => r.EndedAtUtc)
        .Text("artifact", r => r.ArtifactName)
        .Text("user", r => r.UserName)
        .Text("tenant", r => r.TenantName)
        // The result is shown by its Turkish label: Başarısız, Hazır, Hazırlanıyor, İndiriliyor, İptal edildi, Reddedildi, Tamamlandı.
        .Ordinal("result", r => r.Result, "Failed", "Ready", "Preparing", "Streaming", "Cancelled", "Refused", "Completed")
        .Value("bytes", r => r.Bytes);

    // The text goes through string.Format (SqlQueryRaw), so a literal brace is doubled.
    // Sessions, with a refusal of a prepared (Ready) session shown as their outcome, united with the refusals that have no session.
    // Only a refusal the platform recorded against a session counts for it: the target id of a sessionless refusal is caller-chosen.
    internal const string AttemptsSql = """
        SELECT u.*, tenant."Name" AS "TenantName", usr."DisplayName" AS "UserName", artifact."Name" AS "ArtifactName"
        FROM (
            SELECT s."Id", s."ShareId", s."TenantId", s."ArtifactId", s."UserId", s."CreatedAt" AS "StartedAtUtc",
                   CASE WHEN refusal."Id" IS NULL THEN s."ReleasedAt" ELSE refusal."At" END AS "EndedAtUtc",
                   CASE WHEN refusal."Id" IS NULL THEN s."State" ELSE 'Refused' END AS "Result",
                   s."TransferredBytes" AS "Bytes",
                   CASE WHEN refusal."Id" IS NULL THEN s."FailureCode" ELSE COALESCE(refusal."DetailsJson" ->> 'failureReason', 'refused') END AS "FailureReason",
                   s."Decrypted"
            FROM download_sessions s
            LEFT JOIN LATERAL (
                SELECT e."Id", e."At", e."DetailsJson" FROM audit_events e
                WHERE s."State" = 'Ready' AND e."Action" = 'download.refused' AND e."TargetType" = 'download' AND e."TenantId" = s."TenantId"
                  AND e."TargetId" = s."Id"::text AND jsonb_exists(e."DetailsJson", 'sessionId')
                ORDER BY e."At" DESC LIMIT 1) refusal ON TRUE
            UNION ALL
            SELECT e."Id", CASE WHEN e."DetailsJson" ->> 'shareId' ~* '^[0-9a-f-]{{36}}$' THEN (e."DetailsJson" ->> 'shareId')::uuid END, e."TenantId",
                   CASE WHEN e."DetailsJson" ->> 'artifactId' ~* '^[0-9a-f-]{{36}}$' THEN (e."DetailsJson" ->> 'artifactId')::uuid ELSE '00000000-0000-0000-0000-000000000000'::uuid END,
                   e."ActorUserId", e."At", e."At", 'Refused', 0::bigint, COALESCE(e."DetailsJson" ->> 'failureReason', 'refused'), FALSE
            FROM audit_events e
            WHERE e."Action" = 'download.refused' AND e."TargetType" = 'download' AND e."TenantId" IS NOT NULL AND NOT jsonb_exists(e."DetailsJson", 'sessionId')
        ) u
        LEFT JOIN tenants tenant ON tenant."Id" = u."TenantId"
        LEFT JOIN users usr ON usr."Id" = u."UserId"
        LEFT JOIN LATERAL (
            SELECT COALESCE(NULLIF(BTRIM(run."Configuration" ->> 'Database'), ''), job."Name") AS "Name"
            FROM backup_artifacts art
            JOIN backup_run_attempts attempt ON attempt."Id" = art."AttemptId" AND attempt."TenantId" = art."TenantId"
            JOIN backup_runs run ON run."Id" = attempt."RunId" AND run."TenantId" = attempt."TenantId"
            JOIN backup_jobs job ON job."Id" = run."JobId" AND job."TenantId" = run."TenantId"
            WHERE art."Id" = u."ArtifactId" AND art."TenantId" = u."TenantId" LIMIT 1) artifact ON TRUE
        """;

    internal static BackupShareView ToView(
        BackupShare share,
        DateTimeOffset now,
        string? artifactName,
        string? createdByDisplayName,
        string? tenantName = null)
    {
        return new BackupShareView(
            share.Id,
            share.TenantId.Value,
            share.ArtifactId,
            share.CreatedByUserId.Value,
            share.CreatedByRole.ToString(),
            share.Reason,
            share.CreatedAtUtc,
            share.ExpiresAtUtc,
            share.RevokedAtUtc,
            share.RevokedByUserId?.Value,
            share.GetStatus(now).ToString(),
            artifactName,
            createdByDisplayName,
            tenantName);
    }

    internal static async Task RecordShareNotificationAsync(
        PlatformDbContext context,
        BackupShare share,
        string eventType,
        string dedupKey,
        string? artifactName,
        UserId actor,
        UserRole actorRole,
        bool platformAdmins,
        bool tenantAdmins,
        DateTimeOffset occurredAt,
        CancellationToken cancellationToken)
    {
        var subjectName = !string.IsNullOrWhiteSpace(artifactName) ? artifactName : "Backup share";
        var notificationEvent = new NotificationEvent(
            Guid.NewGuid(),
            share.TenantId,
            eventType,
            NotificationPhase.Raised,
            NotificationSeverity.Info,
            new NotificationSubject("backup_share", share.Id, subjectName),
            new Dictionary<string, string>
            {
                ["shareId"] = share.Id.ToString("D"),
                ["artifactId"] = share.ArtifactId.ToString("D"),
                ["actorRole"] = actorRole.ToString(),
                ["expiresAtUtc"] = share.ExpiresAtUtc.ToString("o")
            },
            dedupKey,
            occurredAt,
            occurredAt);

        await UserNotificationWriter.RecordAsync(
            context,
            notificationEvent,
            [],
            platformAdmins: platformAdmins,
            tenantAdmins: tenantAdmins,
            except: actor,
            cancellationToken);
    }

    internal static async Task<Dictionary<UserId, string>> ResolveUserNamesAsync(
        PlatformDbContext context,
        IReadOnlyCollection<UserId> userIds,
        CancellationToken cancellationToken)
    {
        if (userIds.Count == 0) return [];
        return await context.Users
            .AsNoTracking()
            .Where(u => userIds.Contains(u.Id))
            .ToDictionaryAsync(u => u.Id, u => u.DisplayName, cancellationToken);
    }

    internal static async Task<Dictionary<TenantId, string>> ResolveTenantNamesAsync(
        PlatformDbContext context,
        IReadOnlyCollection<TenantId> tenantIds,
        CancellationToken cancellationToken)
    {
        if (tenantIds.Count == 0) return [];
        return await context.Tenants
            .AsNoTracking()
            .Where(t => tenantIds.Contains(t.Id))
            .ToDictionaryAsync(t => t.Id, t => t.Name, cancellationToken);
    }

    internal static async Task<Dictionary<Guid, string>> ResolveArtifactNamesAsync(
        PlatformDbContext context,
        IReadOnlyCollection<Guid> artifactIds,
        CancellationToken cancellationToken,
        TenantId? tenantId = null)
    {
        if (artifactIds.Count == 0) return [];

        var query = from artifact in context.BackupArtifacts.AsNoTracking()
                    join attempt in context.BackupRunAttempts.AsNoTracking() on new { artifact.AttemptId, artifact.TenantId } equals new { AttemptId = attempt.Id, attempt.TenantId }
                    join run in context.BackupRuns.AsNoTracking() on new { attempt.RunId, attempt.TenantId } equals new { RunId = run.Id, run.TenantId }
                    join job in context.BackupJobs.AsNoTracking() on new { run.JobId, run.TenantId } equals new { JobId = job.Id, job.TenantId }
                    where artifactIds.Contains(artifact.Id)
                    select new
                    {
                        ArtifactId = artifact.Id,
                        TenantId = artifact.TenantId,
                        DatabaseName = run.Configuration.Database,
                        JobName = job.Name
                    };

        if (tenantId != null)
        {
            query = query.Where(r => r.TenantId == tenantId);
        }

        var rows = await query.ToListAsync(cancellationToken);

        return rows
            .DistinctBy(r => r.ArtifactId)
            .ToDictionary(
                r => r.ArtifactId,
                r => !string.IsNullOrWhiteSpace(r.DatabaseName) ? r.DatabaseName : r.JobName);
    }
}
