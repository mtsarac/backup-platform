using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using BackupPlatform.Application.Support;
using BackupPlatform.Domain.Audit;
using BackupPlatform.Domain.Tenancy;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace BackupPlatform.IntegrationTests;

/// <summary>
/// A tenant with a real accepted backup (central bytes on disk, a restore-capable agent) driven over a real API host, plus two PlatformAdmins who are
/// deliberately NOT members of the tenant: everything they reach, they reach through a support session.
/// </summary>
internal sealed class SupportScenario : IAsyncDisposable
{
    public const string View = "ViewBackupMetadata";
    public const string Download = "DownloadBackup";
    public const string Restore = "RestoreBackup";
    private const string PrecheckCapability = "mssql.restore.precheck";

    private SupportScenario(DownloadApiScenario download, Account adminAccount, BrowserSession admin, Account otherAccount, BrowserSession other)
    {
        Source = download; AdminAccount = adminAccount; Admin = admin; OtherAccount = otherAccount; Other = other;
    }

    public DownloadApiScenario Source { get; }
    public Account AdminAccount { get; }
    public BrowserSession Admin { get; }
    public Account OtherAccount { get; }
    public BrowserSession Other { get; }
    public AgentWorld World => Source.World;
    public RestoreApiScenario Backup => Source.Backup;
    public PostgreSqlFixture Database => World.Database;
    public MutableClock Clock => World.Clock;
    public Tenant Tenant => Backup.Backup.Setup.Tenant;
    public Guid TenantId => Tenant.Id.Value;
    public Guid ArtifactId => Source.Accepted.ArtifactId;
    public Guid JobId => Source.Accepted.JobId;

    public static async Task<SupportScenario> CreateAsync(PostgreSqlFixture fixture)
    {
        var download = await DownloadApiScenario.CreateAsync(fixture);
        await download.Backup.SyncCapabilitiesAsync(download.Backup.Agent, RestoreApiScenario.Capability, PrecheckCapability);
        var admin = await download.World.SeedUserAsync(platformAdmin: true);
        var other = await download.World.SeedUserAsync(platformAdmin: true);
        return new(download, admin, await download.World.SignInAsync(admin), other, await download.World.SignInAsync(other));
    }

    public ValueTask DisposeAsync() => Source.DisposeAsync();

    public static string Path(Guid sessionId, string tail = "") => $"/api/platform/support-sessions/{sessionId}{tail}";

    public Task<HttpResponseMessage> SendAsync(BrowserSession session, HttpMethod method, string path, object? body = null, string? key = null) =>
        SendAsync(session.Client, method, path, body, key);

    public static async Task<HttpResponseMessage> SendAsync(HttpClient client, HttpMethod method, string path, object? body = null, string? key = null)
    {
        using var csrf = method == HttpMethod.Get ? null : await client.GetAsync("/api/auth/antiforgery");
        using var request = new HttpRequestMessage(method, path);
        if (csrf is not null) request.Headers.Add("X-CSRF-TOKEN", AgentWorld.ReadJson(csrf).GetProperty("requestToken").GetString());
        if (key is not null) request.Headers.Add("Idempotency-Key", key);
        if (body is not null) request.Content = JsonContent.Create(body);
        return await client.SendAsync(request);
    }

    public Task<HttpResponseMessage> OpenRawAsync(string[] permissions, int? minutes = null, string reason = "Ticket 4711: customer cannot restore", string? key = null,
        BrowserSession? who = null, Guid? tenantId = null) =>
        SendAsync(who ?? Admin, HttpMethod.Post, "/api/platform/support-sessions",
            new { tenantId = tenantId ?? TenantId, reason, permissions, durationMinutes = minutes }, key ?? Guid.NewGuid().ToString("N"));

    public async Task<JsonElement> OpenAsync(string[] permissions, int? minutes = null, string reason = "Ticket 4711: customer cannot restore", BrowserSession? who = null, Guid? tenantId = null)
    {
        using var response = await OpenRawAsync(permissions, minutes, reason, null, who, tenantId);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return AgentWorld.ReadJson(response);
    }

    public static Guid IdOf(JsonElement view) => view.GetProperty("id").GetGuid();

    public async Task<(HttpStatusCode Status, JsonElement Body)> GetAsync(string path, BrowserSession? who = null)
    {
        using var response = await (who ?? Admin).GetAsync(path);
        return (response.StatusCode, response.Content.Headers.ContentLength == 0 ? default : AgentWorld.ReadJson(response));
    }

    public async Task<(HttpStatusCode Status, JsonElement Body)> PostAsync(string path, object? body = null, string? key = null, BrowserSession? who = null)
    {
        using var response = await SendAsync(who ?? Admin, HttpMethod.Post, path, body, key);
        return (response.StatusCode, response.Content.Headers.ContentLength == 0 ? default : AgentWorld.ReadJson(response));
    }

    public async Task<string> StatusOfAsync(Guid sessionId)
    {
        var (_, view) = await GetAsync(Path(sessionId));
        return view.GetProperty("status").GetString()!;
    }

    public async Task<List<AuditEvent>> AuditAsync(Guid sessionId)
    {
        await using var context = Database.CreatePlatformContext();
        return await context.AuditEvents.AsNoTracking().Where(entry => entry.SupportSessionId == sessionId).OrderBy(entry => entry.At).ThenBy(entry => entry.Id).ToListAsync();
    }

    public async Task<List<string>> AuditActionsAsync(Guid sessionId) => [.. (await AuditAsync(sessionId)).Select(entry => entry.Action)];

    public async Task<int> ExpireDueAsync()
    {
        await using var scope = World.Factory.Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<ISupportAccessStore>().ExpireDueAsync(Clock.GetUtcNow(), 100, CancellationToken.None);
    }

    /// <summary>An enrolled-agent poll, start and (optionally) completion of the next restore task of <paramref name="taskType"/>.</summary>
    public async Task<RestoreTask> RunAgentAsync(string taskType, string status = "Succeeded", string targetState = "Online", bool complete = true)
    {
        var task = await Backup.MustPollRestoreAsync(taskType: taskType);
        using (var started = await Backup.StartAttemptRawAsync(task)) Assert.Equal(HttpStatusCode.OK, started.StatusCode);
        if (!complete) return task;
        using var completed = await Backup.CompleteRawAsync(task, RestoreApiScenario.Completion(task, status, targetState, result: RestoreApiScenario.SampleResult()));
        Assert.Equal(HttpStatusCode.NoContent, completed.StatusCode);
        return task;
    }

    public Task<HttpResponseMessage> TenantAdminGetAsync(string path) => Source.Admin.GetAsync(path);
}
