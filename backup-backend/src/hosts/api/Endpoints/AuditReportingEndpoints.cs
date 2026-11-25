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
        endpoints.MapAuditIntegrityEndpoint();
        endpoints.MapTenantGroup().MapGet("/reports/backups", ReportAsync);
        return endpoints;
    }

    private static async Task<IResult> ReportAsync(DateTimeOffset? from, DateTimeOffset? to, ITenantContext tenant,
        DbContextOptions<PlatformDbContext> options, TimeProvider time, CancellationToken cancellationToken)
    {
        var end = to ?? time.GetUtcNow();
        var start = from ?? end.AddDays(-30);
        if (start >= end || end - start > TimeSpan.FromDays(366)) return Results.BadRequest(new { error = "invalid_report_period" });
        await using var context = PlatformDbContext.ForTenant(options, tenant, tenant.TenantId);
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
