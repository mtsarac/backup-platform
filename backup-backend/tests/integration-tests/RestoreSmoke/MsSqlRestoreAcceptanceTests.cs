using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using BackupPlatform.Contracts.Backups;
using BackupPlatform.IntegrationTests.Backup;
using BackupPlatform.BackupProviders.MSSQL.Backup;
using Microsoft.Data.SqlClient;
using Xunit.Abstractions;

namespace BackupPlatform.IntegrationTests.RestoreSmoke;

/// <summary>Exercises the real HTTPS API, PostgreSQL persistence, published Agent.Host and SQL Server, not a mocked provider.</summary>
public sealed class MsSqlRestoreAcceptanceTests(PostgreSqlFixture database, ITestOutputHelper output) : IClassFixture<PostgreSqlFixture>
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task VerifiedArtifactRestoresAfterSourceDeletionAndExplicitlyReplacesDisposableTarget(bool crossInstance)
    {
        var hostName = AgentHostSmokeProcess.Required("BACKUP_PLATFORM_TEST_MSSQL_HOST");
        var sourcePort = int.Parse(AgentHostSmokeProcess.Required("BACKUP_PLATFORM_TEST_MSSQL_PORT"), System.Globalization.CultureInfo.InvariantCulture);
        var targetPort = crossInstance
            ? int.Parse(AgentHostSmokeProcess.Required("BACKUP_PLATFORM_TEST_MSSQL_RESTORE_TARGET_PORT"), System.Globalization.CultureInfo.InvariantCulture)
            : sourcePort;
        var suffix = Guid.NewGuid().ToString("N");
        var sourceName = "bp_restore_source_" + suffix;
        var targetName = "bp_restore_target_" + suffix;
        var rejectedName = "bp_restore_rejected_" + suffix;
        await using var source = new RestoreSmokeSql(hostName, sourcePort);
        await using var target = new RestoreSmokeSql(hostName, targetPort);
        try
        {
            await source.ExecuteAsync($"CREATE DATABASE {Identifier(sourceName)};");
            await source.ExecuteAsync($"""
                ALTER DATABASE {Identifier(sourceName)} SET RECOVERY SIMPLE;
                USE {Identifier(sourceName)};
                CREATE TABLE dbo.restore_probe (id int PRIMARY KEY, value nvarchar(80) NOT NULL);
                INSERT dbo.restore_probe VALUES (1, N'original verified payload'), (2, N'second row');
                USE master;
                """);
            await using var world = await AgentWorld.CreateAsync(database, clock: new MutableClock(live: true), kestrel: true);
            var tenant = await world.CreateTenantAsync();
            await world.EntitleAsync(tenant.Tenant);
            var agent = await world.EnrollAsync(tenant);
            var configuration = new BackupExecutionConfiguration
            {
                Host = hostName, Port = sourcePort, Database = sourceName,
                DatabaseCredentialReference = "sql_smoke", TrustServerCertificate = true,
                StagingReference = "default", Destinations = BackupTestDestinations.LocalAgent, StoragePrefix = "restore-smoke",
                CopyOnly = true, ChecksumPolicy = "Required", CompressionPolicy = "Preferred"
            };
            using var jobResponse = await tenant.Admin.SendAsync(HttpMethod.Post,
                $"/api/tenants/{tenant.Tenant.Id.Value}/backup-jobs", new { name = "Restore acceptance source", agentId = agent.AgentId, configuration });
            Assert.Equal(HttpStatusCode.Created, jobResponse.StatusCode);
            var jobId = AgentWorld.ReadJson(jobResponse).GetProperty("id").GetGuid();
            var run = await MsSqlManualAgentSmokeTests.StartRunAsync(tenant, jobId, "restore-source");
            await using var agentHost = await AgentHostSmokeProcess.StartAsync(world, agent, output.WriteLine);
            var backup = await WaitAsync(tenant, "backup-runs", run.GetProperty("id").GetGuid(), agentHost);
            Assert.Equal("Succeeded", backup.GetProperty("status").GetString());
            var artifact = Assert.Single(Assert.Single(backup.GetProperty("attempts").EnumerateArray()).GetProperty("artifacts").EnumerateArray());
            var artifactId = artifact.GetProperty("id").GetGuid();
            var replica = Assert.Single(artifact.GetProperty("replicas").EnumerateArray());
            Assert.Equal("Verified", replica.GetProperty("verificationState").GetString());
            await source.DropAsync(sourceName);
            Assert.Equal(0, await source.DatabaseCountAsync(sourceName));

            var request = new
            {
                agentId = agent.AgentId,
                target = new { host = hostName, port = targetPort, databaseCredentialReference = "sql_smoke", trustServerCertificate = true },
                databaseName = targetName
            };
            var restore = await RequestAsync(tenant, artifactId, "new-name", request);
            var restoreId = restore.GetProperty("id").GetGuid();
            var replay = await RequestAsync(tenant, artifactId, "new-name", request, replay: true);
            Assert.Equal(restoreId, replay.GetProperty("id").GetGuid());
            var completed = await WaitAsync(tenant, "restores", restoreId, agentHost);
            AssertSucceeded(completed, sourceName);
            await target.AssertPayloadAsync(targetName);
            Assert.Equal("ONLINE/MULTI_USER", await target.DatabaseStateAsync(targetName));
            output.WriteLine("Real Agent restore: source dropped; {0} instance -> different database name; checksum + VERIFYONLY; ONLINE/MULTI_USER and exact expected rows.", crossInstance ? "cross" : "same");

            // NewDatabase must not overwrite an existing database, even when its source was already validated.
            var refused = await RequestAsync(tenant, artifactId, "no-implicit-overwrite", request);
            var refusedResult = await WaitAsync(tenant, "restores", refused.GetProperty("id").GetGuid(), agentHost);
            Assert.Equal("Failed", refusedResult.GetProperty("status").GetString());
            Assert.Equal("NotModified", refusedResult.GetProperty("targetState").GetString());
            await target.AssertPayloadAsync(targetName);

            await target.ExecuteAsync($"UPDATE {Identifier(targetName)}.dbo.restore_probe SET value=N'changed target' WHERE id=1;");
            var replace = await RequestAsync(tenant, artifactId, "explicit-replace", new
            {
                agentId = agent.AgentId, request.target, databaseName = targetName,
                mode = "ReplaceExistingDatabase", confirmOverwrite = targetName,
                options = new { terminateActiveConnections = true }
            });
            var replaced = await WaitAsync(tenant, "restores", replace.GetProperty("id").GetGuid(), agentHost);
            AssertSucceeded(replaced, sourceName);
            await target.AssertPayloadAsync(targetName);
            Assert.Equal("ONLINE/MULTI_USER", await target.DatabaseStateAsync(targetName));
            output.WriteLine("Explicit disposable overwrite restored the original rows and left the target ONLINE/MULTI_USER; implicit overwrite was rejected without modifying it.");

            // Change bytes without changing their length: persisted Verified metadata must not authorize today's corrupt file.
            var physical = Path.Combine(agentHost.StorageDirectory, replica.GetProperty("key").GetString()!.Replace('/', Path.DirectorySeparatorChar));
            await using (var corrupt = new FileStream(physical, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            {
                var first = corrupt.ReadByte();
                corrupt.Position = 0;
                corrupt.WriteByte((byte)(first ^ 0xff));
                await corrupt.FlushAsync();
            }
            var mismatch = await RequestAsync(tenant, artifactId, "checksum-mismatch", new
            {
                agentId = agent.AgentId, request.target, databaseName = rejectedName
            });
            var mismatchResult = await WaitAsync(tenant, "restores", mismatch.GetProperty("id").GetGuid(), agentHost);
            Assert.Equal("Failed", mismatchResult.GetProperty("status").GetString());
            Assert.Equal("ChecksumMismatch", mismatchResult.GetProperty("failureCode").GetString());
            Assert.Equal("NotModified", mismatchResult.GetProperty("targetState").GetString());
            Assert.False(mismatchResult.GetProperty("replicaPinned").GetBoolean());
            Assert.Equal(0, await target.DatabaseCountAsync(rejectedName));
            Assert.Empty(Directory.GetFiles(AgentHostSmokeProcess.Required("BACKUP_PLATFORM_TEST_MSSQL_STAGING_AGENT_DIR"), "restore_*"));
            output.WriteLine("Changed physical replica rejected as ChecksumMismatch before RESTORE; no target created, pin released and staging removed.");
        }
        finally
        {
            await target.DropAsync(rejectedName);
            await target.DropAsync(targetName);
            await source.DropAsync(sourceName);
        }
    }

    private static void AssertSucceeded(JsonElement completed, string sourceName)
    {
        Assert.True(completed.GetProperty("status").GetString() == "Succeeded", completed.ToString());
        Assert.Equal("Online", completed.GetProperty("targetState").GetString());
        Assert.False(completed.GetProperty("replicaPinned").GetBoolean());
        var result = completed.GetProperty("result");
        Assert.True(result.GetProperty("checksumVerified").GetBoolean());
        Assert.True(result.GetProperty("backupVerified").GetBoolean());
        Assert.Equal("ONLINE", result.GetProperty("databaseState").GetString());
        Assert.Equal(sourceName, result.GetProperty("sourceBackup").GetProperty("databaseName").GetString());
        Assert.Equal("Database", result.GetProperty("sourceBackup").GetProperty("backupType").GetString());
        var mappings = result.GetProperty("fileMappings").EnumerateArray().ToArray();
        Assert.Contains(mappings, row => row.GetProperty("fileType").GetString() == "Data");
        Assert.Contains(mappings, row => row.GetProperty("fileType").GetString() == "Log");
        foreach (var mapping in mappings)
            Assert.NotEqual(mapping.GetProperty("sourcePhysicalName").GetString(), mapping.GetProperty("targetPhysicalName").GetString());
    }

    private static async Task<JsonElement> RequestAsync(TenantSetup tenant, Guid artifactId, string key, object body, bool replay = false)
    {
        using var tokenResponse = await tenant.Admin.GetAsync("/api/auth/antiforgery");
        using var request = new HttpRequestMessage(HttpMethod.Post, $"/api/tenants/{tenant.Tenant.Id.Value}/backup-artifacts/{artifactId}/restore")
        {
            Content = JsonContent.Create(body)
        };
        request.Headers.Add("X-CSRF-TOKEN", AgentWorld.ReadJson(tokenResponse).GetProperty("requestToken").GetString());
        request.Headers.Add("Idempotency-Key", key);
        using var response = await tenant.Admin.Client.SendAsync(request);
        var json = AgentWorld.ReadJson(response);
        Assert.True(response.StatusCode == (replay ? HttpStatusCode.OK : HttpStatusCode.Accepted), $"{response.StatusCode}: {json}");
        return json;
    }

    private static async Task<JsonElement> WaitAsync(TenantSetup tenant, string collection, Guid id, AgentHostSmokeProcess host)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(4));
        while (true)
        {
            timeout.Token.ThrowIfCancellationRequested();
            host.AssertAlive();
            using var response = await tenant.Admin.GetAsync($"/api/tenants/{tenant.Tenant.Id.Value}/{collection}/{id}");
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            var detail = AgentWorld.ReadJson(response);
            if (detail.GetProperty("status").GetString() is "Succeeded" or "Failed" or "Cancelled" or "AgentLost") return detail;
            await Task.Delay(50, timeout.Token);
        }
    }

    private static string Identifier(string value) => SqlServerBackupCommandBuilder.QuoteIdentifier(value);
}

internal sealed class RestoreSmokeSql : IAsyncDisposable
{
    private readonly SqlConnection _connection;
    public RestoreSmokeSql(string host, int port)
    {
        _connection = new SqlConnection(new SqlConnectionStringBuilder
        {
            DataSource = $"tcp:{host},{port}", InitialCatalog = "master", Encrypt = true, TrustServerCertificate = true, Pooling = false,
            UserID = AgentHostSmokeProcess.Required("BACKUP_PLATFORM_TEST_MSSQL_USER"),
            Password = AgentHostSmokeProcess.Required("BACKUP_PLATFORM_TEST_MSSQL_PASSWORD")
        }.ConnectionString);
    }
    private async Task<SqlCommand> CommandAsync(string text)
    {
        if (_connection.State != System.Data.ConnectionState.Open) await _connection.OpenAsync();
        var command = _connection.CreateCommand();
        command.CommandText = text;
        command.CommandTimeout = 120;
        return command;
    }
    public async Task ExecuteAsync(string text)
    {
        await using var command = await CommandAsync(text);
        await command.ExecuteNonQueryAsync();
    }
    public async Task DropAsync(string name)
    {
        await using var command = await CommandAsync($"IF DB_ID(@name) IS NOT NULL BEGIN ALTER DATABASE {SqlServerBackupCommandBuilder.QuoteIdentifier(name)} SET SINGLE_USER WITH ROLLBACK IMMEDIATE; DROP DATABASE {SqlServerBackupCommandBuilder.QuoteIdentifier(name)}; END;");
        command.Parameters.AddWithValue("@name", name);
        await command.ExecuteNonQueryAsync();
    }
    public async Task<int> DatabaseCountAsync(string name)
    {
        await using var command = await CommandAsync("SELECT COUNT(*) FROM sys.databases WHERE name=@name");
        command.Parameters.AddWithValue("@name", name);
        return Convert.ToInt32(await command.ExecuteScalarAsync(), System.Globalization.CultureInfo.InvariantCulture);
    }
    public async Task<string?> DatabaseStateAsync(string name)
    {
        await using var command = await CommandAsync("SELECT state_desc + '/' + user_access_desc FROM sys.databases WHERE name=@name");
        command.Parameters.AddWithValue("@name", name);
        return await command.ExecuteScalarAsync() as string;
    }
    public async Task AssertPayloadAsync(string name)
    {
        await using var command = await CommandAsync($"SELECT id, value FROM {SqlServerBackupCommandBuilder.QuoteIdentifier(name)}.dbo.restore_probe ORDER BY id;");
        await using var reader = await command.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        Assert.Equal(1, reader.GetInt32(0));
        Assert.Equal("original verified payload", reader.GetString(1));
        Assert.True(await reader.ReadAsync());
        Assert.Equal(2, reader.GetInt32(0));
        Assert.Equal("second row", reader.GetString(1));
        Assert.False(await reader.ReadAsync());
    }
    public ValueTask DisposeAsync() => _connection.DisposeAsync();
}
