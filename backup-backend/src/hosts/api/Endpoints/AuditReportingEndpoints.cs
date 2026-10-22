using BackupPlatform.Api.Security;
using BackupPlatform.Application.Tenancy;
using BackupPlatform.Domain.Backups;
using BackupPlatform.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace BackupPlatform.Api.Endpoints;

public static class AuditReportingEndpoints
{
    public static IEndpointRouteBuilder MapAuditReportingEndpoints(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet("/api/platform/audit/integrity", VerifyAsync).RequireAuthorization(AuthorizationPolicies.PlatformAdmin);
        endpoints.MapGet("/api/tenants/{tenantId:guid}/reports/backups", ReportAsync)
            .WithMetadata(new TenantScopedEndpoint()).RequireAuthorization(AuthorizationPolicies.TenantViewer);
        return endpoints;
    }

    private static async Task<IResult> VerifyAsync(DateTimeOffset? after, int? limit, AuditSealKeys keys,
        DbContextOptions<PlatformDbContext> options, CancellationToken cancellationToken)
    {
        if (limit is < 1 or > 1000) return Results.BadRequest(new { error = "invalid_limit" });
        await using var context = PlatformDbContext.CreateForPlatformOperations(options);
        var rows = await context.AuditEvents.Where(row => after == null || row.At > after)
            .OrderBy(row => row.At).ThenBy(row => row.Id).Take(limit ?? 100).ToListAsync(cancellationToken);
        var events = rows.Select(row =>
        {
            var keyId = (string?)context.Entry(row).Property(AuditSigningInterceptor.KeyProperty).CurrentValue;
            var signature = (string?)context.Entry(row).Property(AuditSigningInterceptor.SignatureProperty).CurrentValue;
            var status = keyId is null || signature is null ? "Unsealed"
                : !keys.PublicKeys.TryGetValue(keyId, out var pem) ? "UnknownKey"
                : AuditSigningInterceptor.Verify(row, signature, pem) ? "Verified" : "Invalid";
            return new { row.Id, row.At, keyId, signature, status, canonicalPayloadBase64 = Convert.ToBase64String(AuditSigningInterceptor.Canonical(row)) };
        }).ToArray();
        return Results.Ok(new { signingEnabled = keys.Interceptor is not null, publicKeys = keys.PublicKeys, events });
    }

    private static async Task<IResult> ReportAsync(DateTimeOffset? from, DateTimeOffset? to, ITenantContext tenant,
        DbContextOptions<PlatformDbContext> options, TimeProvider time, CancellationToken cancellationToken)
    {
        var end = to ?? time.GetUtcNow();
        var start = from ?? end.AddDays(-30);
        if (start >= end || end - start > TimeSpan.FromDays(366)) return Results.BadRequest(new { error = "invalid_report_period" });
        await using var context = PlatformDbContext.CreateForPlatformOperations(options);
        var runs = context.BackupRuns.AsNoTracking().Where(run => run.TenantId == tenant.TenantId && run.CompletedAt >= start && run.CompletedAt < end);
        var outcomes = await runs.GroupBy(run => run.Status).Select(group => new { status = group.Key, count = group.LongCount() }).ToListAsync(cancellationToken);
        var byDay = await runs.GroupBy(run => new { day = run.CompletedAt!.Value.Date, run.Status })
            .Select(group => new { group.Key.day, status = group.Key.Status, count = group.LongCount() }).OrderBy(row => row.day).ToListAsync(cancellationToken);
        var byJob = await runs.GroupBy(run => new { run.JobId, run.Status })
            .Select(group => new { group.Key.JobId, status = group.Key.Status, count = group.LongCount() }).OrderBy(row => row.JobId).ToListAsync(cancellationToken);
        return Results.Ok(new
        {
            from = start, to = end,
            succeeded = outcomes.Where(row => row.status == BackupStatus.Succeeded).Sum(row => row.count),
            failed = outcomes.Where(row => row.status is BackupStatus.Failed or BackupStatus.AgentLost).Sum(row => row.count),
            cancelled = outcomes.Where(row => row.status == BackupStatus.Cancelled).Sum(row => row.count),
            byDay, byJob
        });
    }
}
