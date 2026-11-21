using System.Net;
using System.Text.Json;
using BackupPlatform.Domain.Audit;
using Microsoft.EntityFrameworkCore;

namespace BackupPlatform.IntegrationTests;

/// <summary>Revoking a computer stops its schedules: its jobs are disabled, flagged agentRevoked in the job views and left out of the dashboard numbers.</summary>
public sealed class AgentRevocationJobsApiTests(PostgreSqlFixture fixture) : IClassFixture<PostgreSqlFixture>
{
    private static async Task<JsonElement> ReadAsync(BrowserSession session, string url)
    {
        using var response = await session.GetAsync(url);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return AgentWorld.ReadJson(response);
    }

    [Fact]
    public async Task RevokingAComputerDisablesItsJobsAndRemovesThemFromTheDashboardNumbersButNotFromTheJobList()
    {
        await using var scenario = await BackupApiScenario.CreateAsync(fixture);
        var jobId = await scenario.CreateJobAsync();
        var job = await ReadAsync(scenario.Admin, scenario.Root + $"/backup-jobs/{jobId}");
        Assert.True(job.GetProperty("enabled").GetBoolean());
        Assert.False(job.GetProperty("agentRevoked").GetBoolean());
        Assert.Equal(jobId, Assert.Single((await ReadAsync(scenario.Admin, scenario.Root + "/backup-jobs?enabled=true")).EnumerateArray()).GetProperty("id").GetGuid());
        Assert.Empty((await ReadAsync(scenario.Admin, scenario.Root + "/backup-jobs?enabled=false")).EnumerateArray());
        var platformAdmin = await scenario.World.SignInAsync(await scenario.World.SeedUserAsync(platformAdmin: true));
        var tenantPath = $"/api/platform/tenants/{scenario.Setup.Tenant.Id.Value}";
        Assert.Equal(1, (await ReadAsync(platformAdmin, tenantPath)).GetProperty("enabledBackupJobs").GetInt32());
        Assert.Equal(1, (await ReadAsync(scenario.Admin, scenario.Root + "/dashboard")).GetProperty("jobs").GetProperty("total").GetInt32());

        using var revoked = await scenario.Admin.SendAsync(HttpMethod.Post, scenario.Root + $"/agents/{scenario.Agent.AgentId}/revoke");
        Assert.True(revoked.IsSuccessStatusCode);

        var list = await ReadAsync(scenario.Admin, scenario.Root + "/backup-jobs");
        var listed = Assert.Single(list.EnumerateArray());
        Assert.False(listed.GetProperty("enabled").GetBoolean());
        Assert.True(listed.GetProperty("agentRevoked").GetBoolean());
        Assert.Empty((await ReadAsync(scenario.Admin, scenario.Root + "/backup-jobs?enabled=true")).EnumerateArray());
        Assert.Equal(jobId, Assert.Single((await ReadAsync(scenario.Admin, scenario.Root + "/backup-jobs?enabled=false")).EnumerateArray()).GetProperty("id").GetGuid());
        var detail = await ReadAsync(scenario.Admin, scenario.Root + $"/backup-jobs/{jobId}");
        Assert.False(detail.GetProperty("enabled").GetBoolean());
        Assert.True(detail.GetProperty("agentRevoked").GetBoolean());
        Assert.Equal(0, (await ReadAsync(scenario.Admin, scenario.Root + "/dashboard")).GetProperty("jobs").GetProperty("total").GetInt32());
        Assert.Empty((await ReadAsync(scenario.Admin, scenario.Root + "/observability/health-summary")).GetProperty("jobs").EnumerateArray());

        await using var context = fixture.CreatePlatformContext();
        var audit = await context.AuditEvents.AsNoTracking().Where(e => e.TargetId == jobId.ToString("D") && e.Action == AuditActions.BackupJobDisabled).ToListAsync();
        Assert.Single(audit);
        var revokeAudit = await context.AuditEvents.AsNoTracking().SingleAsync(e => e.Action == AuditActions.AgentRevoked && e.TargetId == scenario.Agent.AgentId.ToString());
        Assert.Contains("\"jobsDisabled\"", revokeAudit.DetailsJson, StringComparison.Ordinal);

        // A job still enabled on an already revoked computer (revoked before jobs were disabled with it) is excluded all the same.
        var stored = await context.BackupJobs.SingleAsync(row => row.Id == jobId);
        stored.Enable(DateTimeOffset.UtcNow);
        var summaryBefore = (await ReadAsync(platformAdmin, "/api/platform/summary")).GetProperty("enabledBackupJobs").GetInt64();
        await context.SaveChangesAsync();
        Assert.Equal(summaryBefore, (await ReadAsync(platformAdmin, "/api/platform/summary")).GetProperty("enabledBackupJobs").GetInt64());
        Assert.Equal(0, (await ReadAsync(platformAdmin, tenantPath)).GetProperty("enabledBackupJobs").GetInt32());
        Assert.Empty((await ReadAsync(scenario.Admin, scenario.Root + "/backup-jobs?enabled=true")).EnumerateArray());
        var disabled = Assert.Single((await ReadAsync(scenario.Admin, scenario.Root + "/backup-jobs?enabled=false")).EnumerateArray());
        Assert.Equal(jobId, disabled.GetProperty("id").GetGuid());
        Assert.True(disabled.GetProperty("agentRevoked").GetBoolean());
        Assert.Equal(0, (await ReadAsync(scenario.Admin, scenario.Root + "/dashboard")).GetProperty("jobs").GetProperty("total").GetInt32());
        Assert.True((await ReadAsync(scenario.Admin, scenario.Root + "/backup-jobs")).EnumerateArray().Single().GetProperty("agentRevoked").GetBoolean());
    }
}
