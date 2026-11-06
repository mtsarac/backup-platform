using System.Net;
using BackupPlatform.Api.Databases;
using BackupPlatform.Application.Backups;
using BackupPlatform.Contracts.Backups;
using BackupPlatform.Domain.Identity;
using BackupPlatform.Infrastructure.Persistence;
using BackupPlatform.SecretProviders.Abstractions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace BackupPlatform.IntegrationTests;

public sealed class JobCredentialLifecycleIntegrationTests(PostgreSqlFixture fixture) : IClassFixture<PostgreSqlFixture>
{
    private const string Password = "Lifecycle-S3cret-pw";

    private static object Body(Guid agentId) => new
    {
        name = "Lifecycle", agentId,
        configuration = new BackupExecutionConfiguration
        {
            Database = "master", Host = "sql-server", IntegratedSecurity = false, DatabaseLogin = new("sa", Password), Destinations = BackupTestDestinations.LocalBackend
        }
    };

    private static Dictionary<string, string> Key(string key) => new() { ["Idempotency-Key"] = key };

    private async Task<int> SecretsAsync(Guid agentId)
    {
        await using var context = fixture.CreatePlatformContext();
        var prefix = $"dbc-{agentId:N}-%";
        return await context.Database.SqlQuery<int>($"select count(*)::int as \"Value\" from platform_secrets where \"Name\" like {prefix}").SingleAsync();
    }

    /// <summary>The fixture database is shared by every test of the class and claims are global: start from an empty ledger.</summary>
    private async Task ClearClaimsAsync()
    {
        await using var context = fixture.CreatePlatformContext();
        await context.Database.ExecuteSqlRawAsync("DELETE FROM job_credential_claims");
    }

    private async Task<int> ClaimsAsync()
    {
        await using var context = fixture.CreatePlatformContext();
        return await context.Database.SqlQuery<int>($"select count(*)::int as \"Value\" from job_credential_claims").SingleAsync();
    }

    /// <summary>Holds the agent row lock the job insert needs, so a creation request stalls right after it stored the credential.</summary>
    private async Task<NpgsqlConnection> LockAgentAsync(Guid agentId)
    {
        var connection = new NpgsqlConnection(fixture.ConnectionString);
        await connection.OpenAsync();
        await new NpgsqlCommand("BEGIN", connection).ExecuteNonQueryAsync();
        await new NpgsqlCommand($"SELECT \"Id\" FROM agents WHERE \"Id\" = '{agentId}' FOR UPDATE", connection).ExecuteNonQueryAsync();
        return connection;
    }

    [Fact]
    public async Task An_interrupted_creation_leaves_its_credential_claimed_and_the_same_retry_completes_with_that_one_credential()
    {
        await ClearClaimsAsync();
        await using var scenario = await BackupApiScenario.CreateAsync(fixture);
        var agent = scenario.Agent.AgentId;
        var locked = await LockAgentAsync(agent);
        using (var abort = new CancellationTokenSource(TimeSpan.FromSeconds(3)))
        {
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => scenario.Admin.SendAsync(HttpMethod.Post, scenario.Root + "/backup-jobs", Body(agent),
                headers: Key("crash-1"), cancellationToken: abort.Token));
        }
        await Task.Delay(500);
        Assert.Equal(1, await SecretsAsync(agent));
        Assert.Equal(1, await ClaimsAsync());
        await using (var context = fixture.CreatePlatformContext()) Assert.Equal(0, await context.BackupJobs.CountAsync(job => job.IdempotencyKey == "crash-1"));

        await new NpgsqlCommand("ROLLBACK", locked).ExecuteNonQueryAsync();
        await locked.DisposeAsync();
        using var retry = await scenario.Admin.SendAsync(HttpMethod.Post, scenario.Root + "/backup-jobs", Body(agent), headers: Key("crash-1"));
        Assert.Equal(HttpStatusCode.Created, retry.StatusCode);
        Assert.Equal(1, await SecretsAsync(agent));
        await using var check = fixture.CreatePlatformContext();
        var job = await check.BackupJobs.AsNoTracking().SingleAsync(row => row.IdempotencyKey == "crash-1");
        Assert.True(await scenario.World.Factory.Services.GetRequiredService<ISecretStore>().ExistsAsync(new SecretReference(job.Configuration.DatabaseCredentialReference!), default));
    }

    [Fact]
    public async Task A_creation_nobody_retries_loses_its_credential_to_the_sweep_after_the_grace_period_and_a_used_credential_never_does()
    {
        await ClearClaimsAsync();
        await using var scenario = await BackupApiScenario.CreateAsync(fixture);
        var agent = scenario.Agent.AgentId;
        var locked = await LockAgentAsync(agent);
        using (var abort = new CancellationTokenSource(TimeSpan.FromSeconds(3)))
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => scenario.Admin.SendAsync(HttpMethod.Post, scenario.Root + "/backup-jobs", Body(agent), headers: Key("orphan"), cancellationToken: abort.Token));
        await Task.Delay(500);
        await new NpgsqlCommand("ROLLBACK", locked).ExecuteNonQueryAsync();
        await locked.DisposeAsync();
        using var used = await scenario.Admin.SendAsync(HttpMethod.Post, scenario.Root + "/backup-jobs", Body(agent), headers: Key("used"));
        Assert.Equal(HttpStatusCode.Created, used.StatusCode);
        Assert.Equal(2, await SecretsAsync(agent)); // the orphan and the used one (distinct keys, distinct references)

        var sweep = scenario.World.Factory.Services.GetRequiredService<JobCredentialSweepWorker>();
        Assert.Equal(0, await sweep.SweepAsync(default));
        scenario.Clock.Advance(JobCredentialSweepWorker.Grace + TimeSpan.FromMinutes(1));
        Assert.Equal(1, await sweep.SweepAsync(default));
        Assert.Equal(1, await SecretsAsync(agent));
        Assert.Equal(0, await ClaimsAsync());
        await using var context = fixture.CreatePlatformContext();
        var job = await context.BackupJobs.AsNoTracking().SingleAsync(row => row.IdempotencyKey == "used");
        Assert.True(await scenario.World.Factory.Services.GetRequiredService<ISecretStore>().ExistsAsync(new SecretReference(job.Configuration.DatabaseCredentialReference!), default));
    }

    [Fact]
    public async Task A_retry_that_arrives_while_the_sweep_holds_a_stale_claim_waits_for_the_removal_then_rewrites_the_credential_and_the_job_resolves_it()
    {
        await ClearClaimsAsync();
        await using var scenario = await BackupApiScenario.CreateAsync(fixture);
        var services = scenario.World.Factory.Services;
        var agent = scenario.Agent.AgentId;
        var locked = await LockAgentAsync(agent);
        using (var abort = new CancellationTokenSource(TimeSpan.FromSeconds(3)))
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => scenario.Admin.SendAsync(HttpMethod.Post, scenario.Root + "/backup-jobs", Body(agent), headers: Key("race"), cancellationToken: abort.Token));
        await Task.Delay(500);
        await new NpgsqlCommand("ROLLBACK", locked).ExecuteNonQueryAsync();
        await locked.DisposeAsync();
        Assert.Equal(1, await ClaimsAsync());
        scenario.Clock.Advance(JobCredentialSweepWorker.Grace + TimeSpan.FromMinutes(1));

        // Node A's sweep has selected the stale claim (row locked) and is about to delete the credential; the gate stands in for that window.
        var claims = services.GetRequiredService<IJobCredentialClaims>();
        var vault = services.GetRequiredService<DatabaseCredentialVault>();
        var selected = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var sweep = Task.Run(() => claims.SweepStaleAsync(scenario.Clock.GetUtcNow() - JobCredentialSweepWorker.Grace, 10, async (reference, token) =>
        {
            selected.SetResult();
            await gate.Task;
            await vault.DeleteStoredAsync(reference, token);
        }, default));
        await selected.Task.WaitAsync(TimeSpan.FromSeconds(30));

        // Node B: the retry of the same request arrives now. It must not get past its claim until the sweep is done with that row.
        var retry = Task.Run(() => scenario.Admin.SendAsync(HttpMethod.Post, scenario.Root + "/backup-jobs", Body(agent), headers: Key("race")));
        await Task.WhenAny(retry, Task.Delay(TimeSpan.FromSeconds(2)));
        Assert.False(retry.IsCompleted, "the retry overtook the sweep's lock on the claim");

        gate.SetResult();
        Assert.Equal(1, await sweep);
        using var response = await retry.WaitAsync(TimeSpan.FromSeconds(60));
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);

        await using var context = fixture.CreatePlatformContext();
        var job = await context.BackupJobs.AsNoTracking().SingleAsync(row => row.IdempotencyKey == "race");
        var resolved = await vault.ResolveAsync(agent, job.Configuration.DatabaseCredentialReference, default);
        Assert.Equal(("sa", Password), (resolved!.UserName, resolved.Password));
        Assert.Equal(1, await SecretsAsync(agent));

        // The finished creation leaves only a claim a job references: the next sweep drops it and keeps the credential.
        scenario.Clock.Advance(JobCredentialSweepWorker.Grace + TimeSpan.FromMinutes(1));
        Assert.Equal(0, await services.GetRequiredService<JobCredentialSweepWorker>().SweepAsync(default));
        Assert.Equal(0, await ClaimsAsync());
        Assert.NotNull(await vault.ResolveAsync(agent, job.Configuration.DatabaseCredentialReference, default));
    }

    [Fact]
    public async Task The_fingerprint_key_is_created_once_and_every_node_and_restart_gets_the_same_bytes()
    {
        await using var scenario = await BackupApiScenario.CreateAsync(fixture);
        var services = scenario.World.Factory.Services;
        var secrets = services.GetRequiredService<ISecretStore>();
        var options = services.GetRequiredService<DbContextOptions<PlatformDbContext>>();
        var nodes = Enumerable.Range(0, 6).Select(_ => new IdempotencyFingerprintKey(secrets, options)).ToArray();
        var keys = await Task.WhenAll(nodes.Select(async node => Convert.ToHexString(await node.GetAsync(default))));
        Assert.Single(keys.Distinct());
        Assert.Equal(64, keys[0].Length);
        var restarted = await new IdempotencyFingerprintKey(secrets, options).GetAsync(default);
        Assert.Equal(keys[0], Convert.ToHexString(restarted));
        await using var context = fixture.CreatePlatformContext();
        Assert.Equal(1, await context.Database.SqlQuery<int>($"select count(*)::int as \"Value\" from platform_secrets where \"Name\" = {IdempotencyFingerprintKey.SecretName}").SingleAsync());
    }
}
