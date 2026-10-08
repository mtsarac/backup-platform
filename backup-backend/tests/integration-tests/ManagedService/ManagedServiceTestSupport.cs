using System.Net;
using System.Text.Json;
using BackupPlatform.Domain.Audit;
using BackupPlatform.Domain.Identity;
using BackupPlatform.Infrastructure.ManagedService;
using BackupPlatform.Infrastructure.Persistence;
using BackupPlatform.ManagedService.Administration;
using BackupPlatform.ManagedService.Plans;
using BackupPlatform.ManagedService.Subscriptions;
using Microsoft.EntityFrameworkCore;

namespace BackupPlatform.IntegrationTests.ManagedService;

internal sealed record ApiReply(JsonElement Json, HttpStatusCode Status)
{
    public string Error => Json.GetProperty("error").GetString()!;
}

internal static class ManagedServiceTestSupport
{
    public const long Gb = 1_000_000_000;

    public static async Task<ApiReply> CallAsync(BrowserSession session, HttpMethod method, string path, object? body = null)
    {
        using var response = await session.SendAsync(method, path, body);
        var text = await response.Content.ReadAsStringAsync();
        return new ApiReply(string.IsNullOrEmpty(text) ? default : JsonDocument.Parse(text).RootElement.Clone(), response.StatusCode);
    }

    public static string UniqueName(string prefix = "Plan") => $"{prefix} {Guid.NewGuid():N}";

    public static DbContextOptions<PlatformDbContext> OptionsOf(PostgreSqlFixture fixture) =>
        new DbContextOptionsBuilder<PlatformDbContext>().UseNpgsql(fixture.ConnectionString).Options;

    public static PostgreSqlManagedServiceStore StoreOf(PostgreSqlFixture fixture) => new(OptionsOf(fixture));

    public static async Task<Guid> CreatePlanAsync(BrowserSession admin, string? name = null, long? bytes = 5 * Gb, bool enabled = true)
    {
        var reply = await CallAsync(admin, HttpMethod.Post, "/api/platform/plans",
            bytes is null ? new { name = name ?? UniqueName(), unlimited = true, enabled } : new { name = name ?? UniqueName(), storageLimitBytes = bytes, enabled });
        Assert.Equal(HttpStatusCode.Created, reply.Status);
        return reply.Json.GetProperty("planId").GetGuid();
    }

    public static async Task<PlanId> CreatePlanDirectAsync(PostgreSqlFixture fixture, long? bytes = 5 * Gb, bool enabled = true)
    {
        var plan = new Plan(PlanId.New(), UniqueName(), bytes, null, enabled, DateTimeOffset.UtcNow);
        var change = await StoreOf(fixture).CreatePlanAsync(plan, UserId.New(), DateTimeOffset.UtcNow, default);
        Assert.Equal(ManagedServiceOutcome.Changed, change.Outcome);
        return plan.Id;
    }

    public static async Task<List<TenantSubscription>> RowsOfAsync(PostgreSqlFixture fixture, TenantId tenantId)
    {
        await using var context = fixture.CreatePlatformContext();
        return await context.TenantSubscriptions.AsNoTracking().Where(row => row.TenantId == tenantId).ToListAsync();
    }

    public static async Task<int> OpenRowCountAsync(PostgreSqlFixture fixture, TenantId tenantId) =>
        (await RowsOfAsync(fixture, tenantId)).Count(row => row.IsOpen);

    public static async Task<List<AuditEvent>> AuditOfTenantAsync(PostgreSqlFixture fixture, TenantId tenantId)
    {
        await using var context = fixture.CreatePlatformContext();
        return await context.AuditEvents.AsNoTracking().Where(row => row.TenantId == tenantId).OrderBy(row => row.At).ThenBy(row => row.Id).ToListAsync();
    }

    public static async Task<List<AuditEvent>> AuditOfTargetAsync(PostgreSqlFixture fixture, Guid targetId)
    {
        await using var context = fixture.CreatePlatformContext();
        return await context.AuditEvents.AsNoTracking().Where(row => row.TargetId == targetId.ToString("D")).OrderBy(row => row.At).ThenBy(row => row.Id).ToListAsync();
    }

    public static string SubscriptionPath(Guid tenantId, string suffix = "") => $"/api/platform/tenants/{tenantId}/subscription{suffix}";
}
