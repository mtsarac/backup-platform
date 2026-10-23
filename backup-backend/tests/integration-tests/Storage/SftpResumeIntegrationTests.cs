using System.Security.Cryptography;
using BackupPlatform.StorageProviders.Abstractions;
using BackupPlatform.StorageProviders.Sftp;
using Xunit;

namespace BackupPlatform.IntegrationTests.Storage;

/// <summary>
/// Resume from a retained SFTP partial against a real server (same BACKUP_PLATFORM_TEST_SFTP_* settings as the lifecycle tests).
/// Set BACKUP_PLATFORM_TEST_SFTP_PRIVATE_KEY_FILE to authenticate with a key instead of the password.
/// </summary>
public sealed class SftpResumeIntegrationTests
{
    private static readonly bool Enabled =
        string.Equals(Environment.GetEnvironmentVariable("BACKUP_PLATFORM_TEST_SFTP_ENABLED"), "true", StringComparison.OrdinalIgnoreCase);
    private static readonly string[] AllowedNetworks = ["127.0.0.0/8", "10.0.0.0/8", "172.16.0.0/12", "192.168.0.0/16"];
    private static readonly string Host = Environment.GetEnvironmentVariable("BACKUP_PLATFORM_TEST_SFTP_HOST") ?? "127.0.0.1";
    private static readonly int Port = int.TryParse(Environment.GetEnvironmentVariable("BACKUP_PLATFORM_TEST_SFTP_PORT"), out var p) ? p : 2222;
    private static readonly string Username = Environment.GetEnvironmentVariable("BACKUP_PLATFORM_TEST_SFTP_USER") ?? "testuser";
    private static readonly string Password = Environment.GetEnvironmentVariable("BACKUP_PLATFORM_TEST_SFTP_PASSWORD") ?? "testpass";
    private static readonly string Fingerprint = Environment.GetEnvironmentVariable("BACKUP_PLATFORM_TEST_SFTP_FINGERPRINT") ?? "";
    private static readonly string BaseDir = Environment.GetEnvironmentVariable("BACKUP_PLATFORM_TEST_SFTP_BASE_DIR") ?? "/upload";
    private static readonly string? KeyFile = Environment.GetEnvironmentVariable("BACKUP_PLATFORM_TEST_SFTP_PRIVATE_KEY_FILE");

    private readonly Guid _tenantId = Guid.NewGuid();
    private readonly StorageObjectKey _key;
    private readonly StorageObjectKey _partial;
    private readonly SftpStorageProvider _provider;

    public SftpResumeIntegrationTests()
    {
        _key = new($"{_tenantId:N}/job/run/attempt/backup.bak");
        _partial = new($"{_tenantId:N}/job/run/attempt/.backup.bak.partial");
        var options = new SftpStorageOptions(Host, Port, Username, KeyFile is null ? "password" : "private-key", BaseDir, Fingerprint, _tenantId, Guid.NewGuid());
        var credential = KeyFile is null ? new SftpCredential(Password, null, null) : new SftpCredential(null, File.ReadAllText(KeyFile), null);
        _provider = new(options, _ => ValueTask.FromResult(credential), new SftpNetworkOptions(allowedPrivateNetworks: AllowedNetworks));
    }

    private static bool Skip => !Enabled || string.IsNullOrWhiteSpace(Fingerprint);

    private async Task SeedPartial(byte[] prefix) =>
        await _provider.PutAsync(_partial, new MemoryStream(prefix), new(StorageOverwritePolicy.Fail), null, CancellationToken.None);

    private Task<StorageObjectMetadata> Resume(byte[] content, List<long> checkpoints, long retained, List<long>? progress = null) =>
        _provider.PutAsync(_key, new MemoryStream(content),
            new(StorageOverwritePolicy.Fail, ContentChecksum.Sha256(SHA256.HashData(content)), content.Length)
            {
                Checkpoint = new(retained, "sftp-v1"),
                RecordCheckpointAsync = (checkpoint, _) => { checkpoints.Add(checkpoint.VerifiedByteOffset); return Task.CompletedTask; }
            },
            progress is null ? null : new Progress<long>(progress.Add), CancellationToken.None);

    [Fact]
    public async Task Put_VerifiesRetainedPrefixThenContinuesFromItsEnd_WhenConfigured()
    {
        if (Skip) return;
        var content = RandomNumberGenerator.GetBytes(300_000);
        const int retained = 100_000;
        await SeedPartial(content[..retained]);
        var checkpoints = new List<long>();
        var progress = new List<long>();

        var stored = await Resume(content, checkpoints, retained, progress);

        Assert.Equal(content.Length, stored.SizeBytes);
        Assert.Equal(retained, checkpoints[0]);
        // A restart from zero would report 64 KiB first; resuming reports bytes past the verified prefix.
        Assert.InRange(progress[0], retained + 1, retained + 64 * 1024);
        await using (var read = await _provider.OpenReadAsync(_key, CancellationToken.None))
        {
            using var copy = new MemoryStream();
            await read.CopyToAsync(copy);
            Assert.Equal(content, copy.ToArray());
        }
        Assert.False(await _provider.ExistsAsync(_partial, CancellationToken.None));
        await _provider.DeleteAsync(_key, CancellationToken.None);
    }

    [Fact]
    public async Task Put_RejectsCorruptedRetainedPrefix_AndKeepsPartial_WhenConfigured()
    {
        if (Skip) return;
        var content = RandomNumberGenerator.GetBytes(300_000);
        var corrupted = content[..100_000];
        corrupted[50_000] ^= 1;
        await SeedPartial(corrupted);

        var failure = await Assert.ThrowsAsync<StorageException>(() => Resume(content, [], corrupted.Length));

        Assert.Equal(StorageFailureKind.IntegrityMismatch, failure.Kind);
        Assert.False(await _provider.ExistsAsync(_key, CancellationToken.None));
        Assert.True(await _provider.ExistsAsync(_partial, CancellationToken.None));
        await _provider.DeleteAsync(_partial, CancellationToken.None);
    }

    [Fact]
    public async Task Put_RejectsRetainedPrefixLongerThanTheArtifact_WhenConfigured()
    {
        if (Skip) return;
        var content = RandomNumberGenerator.GetBytes(1_000);
        await SeedPartial(RandomNumberGenerator.GetBytes(2_000));

        var failure = await Assert.ThrowsAsync<StorageException>(() => Resume(content, [], 2_000));

        Assert.Equal(StorageFailureKind.IntegrityMismatch, failure.Kind);
        Assert.False(await _provider.ExistsAsync(_key, CancellationToken.None));
        await _provider.DeleteAsync(_partial, CancellationToken.None);
    }

    [Fact]
    public async Task Put_OfAnAlreadyVerifiedObjectReturnsItWithoutWritingAgain_WhenConfigured()
    {
        if (Skip) return;
        var content = RandomNumberGenerator.GetBytes(200_000);
        await Resume(content, [], 0);
        var progress = new List<long>();
        var checkpoints = new List<long>();

        var again = await Resume(content, checkpoints, content.Length, progress);

        Assert.Equal(content.Length, again.SizeBytes);
        Assert.Empty(progress);
        Assert.Empty(checkpoints);
        await _provider.DeleteAsync(_key, CancellationToken.None);
    }

    [Fact]
    public async Task Put_CheckpointsPerInterval_NotPerBuffer_WhenConfigured()
    {
        if (Skip) return;
        var content = RandomNumberGenerator.GetBytes(20 * 1024 * 1024);
        var checkpoints = new List<long>();

        await _provider.PutAsync(_key, new MemoryStream(content),
            new(StorageOverwritePolicy.Fail, ContentChecksum.Sha256(SHA256.HashData(content)), content.Length)
            { RecordCheckpointAsync = (checkpoint, _) => { checkpoints.Add(checkpoint.VerifiedByteOffset); return Task.CompletedTask; } },
            null, CancellationToken.None);

        Assert.Equal([8L * 1024 * 1024, 16L * 1024 * 1024], checkpoints);
        await _provider.DeleteAsync(_key, CancellationToken.None);
    }
}
