using System.Net;
using System.Security.Cryptography;
using System.Text.Json;
using BackupPlatform.Application.Downloads;
using BackupPlatform.Domain.Identity;
using BackupPlatform.Infrastructure.Downloads;
using BackupPlatform.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using BackupPlatform.Application.Backups;
using BackupPlatform.Encryption;
using Microsoft.Extensions.DependencyInjection;

namespace BackupPlatform.IntegrationTests;

internal sealed class DownloadApiScenario : IAsyncDisposable
{
    private DownloadApiScenario(RestoreApiScenario backup, AgentWorld world, BrowserSession admin, AcceptedBackup accepted, Guid replicaId, string directory, string file, byte[] bytes)
    {
        Backup = backup; World = world; Admin = admin; Accepted = accepted; ReplicaId = replicaId; DirectoryPath = directory; FilePath = file; Bytes = bytes;
    }
    public RestoreApiScenario Backup { get; }
    public AgentWorld World { get; }
    public BrowserSession Admin { get; }
    public AcceptedBackup Accepted { get; }
    public Guid ReplicaId { get; }
    public string DirectoryPath { get; }
    public string FilePath { get; }
    public byte[] Bytes { get; }
    public string Checksum => Convert.ToHexStringLower(SHA256.HashData(Bytes));
    public string Root => Backup.Root;
    public string ArtifactPath => Root + $"/backup-artifacts/{Accepted.ArtifactId}";
    public PostgreSqlDownloadStore Store() => new(new DbContextOptionsBuilder<PlatformDbContext>().UseNpgsql(World.Database.ConnectionString).Options);

    public static async Task<DownloadApiScenario> CreateAsync(PostgreSqlFixture fixture, bool agentLocal = false, bool kestrel = false, Action<IServiceCollection>? configureServices = null)
    {
        var backup = await RestoreApiScenario.CreateAsync(fixture);
        var bytes = Enumerable.Range(0, 1024).Select(index => (byte)(index % 251)).ToArray();
        var provider = agentLocal ? "local" : "central";
        var checksum = Convert.ToHexStringLower(SHA256.HashData(bytes));
        using var jobResponse = await backup.Backup.CreateJobRawAsync(new
        {
            name = "Download central backup", agentId = backup.Agent.AgentId,
            configuration = new BackupPlatform.Contracts.Backups.BackupExecutionConfiguration
            {
                Database = "master", Host = "sql-server", IntegratedSecurity = true,
                Destinations = [agentLocal ? BackupTestDestinations.Local() : BackupTestDestinations.Central()]
            }
        });
        Assert.Equal(HttpStatusCode.Created, jobResponse.StatusCode);
        var jobId = AgentWorld.ReadJson(jobResponse).GetProperty("id").GetGuid();
        var runId = await backup.Backup.StartRunAsync(jobId);
        var claim = await backup.Backup.ClaimAsync();
        await backup.Backup.StartAttemptAsync(claim);
        var completion = backup.Backup.SuccessRequest(claim);
        var replica = completion.Artifact!.Replicas[0] with { StorageProvider = provider, StorageTarget = provider, Checksum = checksum };
        completion = completion with { Artifact = completion.Artifact with { ArtifactChecksum = checksum, Replicas = [replica] } };
        using var completed = await backup.Backup.CompleteRawAsync(claim, completion);
        Assert.Equal(HttpStatusCode.NoContent, completed.StatusCode);
        var artifact = RetentionApiSupport.ArtifactOf(await backup.Backup.RunAsync(runId));
        var accepted = new AcceptedBackup(jobId, runId, claim.AttemptId, artifact.GetProperty("id").GetGuid(), replica.Key, backup.Clock.GetUtcNow());
        var replicaId = RetentionApiSupport.ReplicaOf(artifact).GetProperty("id").GetGuid();
        var directory = Path.Combine(Path.GetTempPath(), "download-api-" + Guid.NewGuid().ToString("N"));
        var file = Path.Combine(directory, "tenants", "download-tenant", accepted.Key.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        await File.WriteAllBytesAsync(file, bytes);
        var world = await AgentWorld.CreateAsync(fixture, clock: backup.Clock, kestrel: kestrel, configureServices: configureServices, configuration: new Dictionary<string, string?>
        {
            ["Backups:Storage:Central:RootDirectory"] = directory,
            [$"Backups:Storage:Central:TenantScopes:{backup.TenantId}"] = "download-tenant"
        });
        var admin = await world.SignInAsync(backup.Backup.Setup.Account);
        return new(backup, world, admin, accepted, replicaId, directory, file, bytes);
    }

    public Task<HttpResponseMessage> CreateRawAsync(BrowserSession? user = null, Guid? replicaId = null, string? artifactPath = null) =>
        (user ?? Admin).SendAsync(HttpMethod.Post, (artifactPath ?? ArtifactPath) + "/downloads", new { replicaId = replicaId ?? ReplicaId });

    public async Task<JsonElement> CreateSessionAsync(BrowserSession? user = null)
    {
        using var response = await CreateRawAsync(user);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return AgentWorld.ReadJson(response);
    }
    public async Task<byte[]> EncryptAsync(string? sourceChecksum = null)
    {
        using var scope = World.Factory.Services.CreateScope();
        var key = await scope.ServiceProvider.GetRequiredService<ITenantWrapKeyStore>().EnsureActiveAsync(Backup.Backup.Setup.Tenant.Id, default);
        using var rsa = RSA.Create();
        rsa.ImportSubjectPublicKeyInfo(key.PublicKeySpki, out _);
        using var ciphertext = new MemoryStream();
        await BpeEncryptor.EncryptAsync(new MemoryStream(Bytes), ciphertext, Bytes.Length, [new BpeRecipient(BpeRecipientType.TenantWrap, key.Version, rsa)],
            new BpeEncryptOptions(ChunkSize: 128));
        var stored = ciphertext.ToArray();
        ciphertext.Position = 0;
        var recipients = await BpeDecryptor.ReadRecipientsAsync(ciphertext);
        var wrapped = Convert.ToBase64String(Assert.Single(recipients).WrappedDek);
        var checksum = Convert.ToHexStringLower(SHA256.HashData(stored));
        await File.WriteAllBytesAsync(FilePath, stored);
        await using var context = World.Database.CreatePlatformContext();
        var tenant = Backup.Backup.Setup.Tenant.Id.Value;
        await using var transaction = await context.Database.BeginTransactionAsync();
        var replicas = await context.BackupReplicas.Where(r => r.TenantId == Backup.Backup.Setup.Tenant.Id && r.ArtifactId == Accepted.ArtifactId).ToListAsync();
        context.BackupReplicas.RemoveRange(replicas);
        await context.SaveChangesAsync();
        await context.Database.ExecuteSqlInterpolatedAsync($"""
            UPDATE backup_artifacts SET "Format" = 'v2-bpe', "EncryptionKeyId" = {key.KeyId}, "EncryptionKeyVersion" = {key.Version},
                "EncryptionWrappedDekBase64" = {wrapped}, "SourceChecksum" = {sourceChecksum ?? Checksum}, "ArtifactChecksum" = {checksum}, "SizeBytes" = {stored.LongLength}
            WHERE "TenantId" = {tenant} AND "Id" = {Accepted.ArtifactId}
            """);
        foreach (var r in replicas)
        {
            await context.Database.ExecuteSqlInterpolatedAsync($"""
                INSERT INTO backup_replicas ("Id", "TenantId", "ArtifactId", "StorageProvider", "StorageTarget", "StorageConnectionId", "Key", "SizeBytes", "Checksum", "ChecksumAlgorithm", "VerificationState", "VerificationMethod", "Status", "ExecutionLocation", "OwningAgentId", "VerifiedAt", "CreatedAt")
                VALUES ({r.Id}, {r.TenantId.Value}, {r.ArtifactId}, {r.StorageProvider}, {r.StorageTarget}, {r.StorageConnectionId}, {r.Key}, {stored.LongLength}, {checksum}, {r.ChecksumAlgorithm}, {r.VerificationState}, {r.VerificationMethod}, {r.Status.ToString()}, {r.ExecutionLocation.ToString()}, {r.OwningAgentId}, {r.VerifiedAt}, {r.CreatedAt})
                """);
        }
        await transaction.CommitAsync();
        return stored;
    }

    public async Task<JsonElement> StatusAsync(Guid id)
    {
        using var response = await Admin.GetAsync(ArtifactPath + $"/downloads/{id}");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return AgentWorld.ReadJson(response);
    }

    public async ValueTask DisposeAsync()
    {
        await World.DisposeAsync();
        await Backup.DisposeAsync();
        Directory.Delete(DirectoryPath, recursive: true);
    }
}
