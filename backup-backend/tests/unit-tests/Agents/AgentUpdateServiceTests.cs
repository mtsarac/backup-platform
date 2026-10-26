using System.Buffers.Binary;
using System.Formats.Tar;
using System.IO.Compression;
using System.Security.Cryptography;
using BackupPlatform.Agent.Core.Updates;
using BackupPlatform.Contracts.Agents;
using BackupPlatform.Encryption;
using Xunit;

namespace BackupPlatform.UnitTests.Agents;

public sealed class AgentUpdateServiceTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "agent-update-tests-" + Guid.NewGuid().ToString("N"));
    private readonly ECDsa _key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
    private readonly AgentUpdateService _updates = new();
    private Dictionary<string, string> Keys => new() { ["release"] = _key.ExportSubjectPublicKeyInfoPem() };

    public AgentUpdateServiceTests() => Directory.CreateDirectory(_root);
    public void Dispose() { _key.Dispose(); Directory.Delete(_root, true); }

    private AgentUpdateManifest Sign(byte[] bytes, string platform = "win-x64") => AgentUpdateManifestSigner.Sign(new AgentUpdateManifestPayload
    {
        Version = "2.0.0", MinimumSupportedVersion = "1.0.0", Platform = platform,
        PackageSha256 = Convert.ToHexStringLower(SHA256.HashData(bytes)), PackageSize = bytes.Length,
        PublishedAt = DateTimeOffset.UnixEpoch, DownloadUrl = $"/api/agent/updates/packages/{platform}/2.0.0"
    }, _key, "release");

    private static byte[] Zip(params (string Name, int Mode)[] entries)
    {
        using var bytes = new MemoryStream();
        using (var archive = new ZipArchive(bytes, ZipArchiveMode.Create, true))
            foreach (var item in entries)
            {
                var entry = archive.CreateEntry(item.Name);
                if (item.Mode != 0) entry.ExternalAttributes = unchecked(item.Mode << 16);
                using var stream = entry.Open();
                stream.Write("release executable"u8);
            }
        return bytes.ToArray();
    }

    private static byte[] Tar(TarEntryType type = TarEntryType.RegularFile, string name = "BackupPlatform.Agent.Host", UnixFileMode mode = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute)
    {
        using var bytes = new MemoryStream();
        using (var gzip = new GZipStream(bytes, CompressionLevel.Fastest, true))
        using (var archive = new TarWriter(gzip, leaveOpen: true))
        {
            var entry = new UstarTarEntry(type, name) { Mode = mode };
            if (type == TarEntryType.RegularFile) entry.DataStream = new MemoryStream("release executable"u8.ToArray());
            else entry.LinkName = "../identity";
            archive.WriteEntry(entry);
        }
        return bytes.ToArray();
    }

    [Theory]
    [InlineData("win-x64")]
    [InlineData("linux-x64")]
    [InlineData("linux-arm64")]
    public async Task Signed_release_downloads_to_disk_and_publishes_only_complete_version(string platform)
    {
        var bytes = platform == "win-x64" ? Zip(("BackupPlatform.Agent.Host.exe", 0)) : Tar();
        var manifest = Sign(bytes, platform);
        var package = Path.Combine(_root, "package");
        await _updates.DownloadAsync(new BoundedReadStream(bytes), package, manifest, platform, Keys, default);
        var releases = Path.Combine(_root, "releases");
        var release = await _updates.StageAsync(package, releases, manifest, platform, Keys, default);
        Assert.Equal(Path.Combine(releases, "2.0.0"), release);
        Assert.Equal("release executable", await File.ReadAllTextAsync(Path.Combine(release, platform == "win-x64" ? "BackupPlatform.Agent.Host.exe" : "BackupPlatform.Agent.Host")));
        Assert.Single(Directory.GetDirectories(releases));
        await Assert.ThrowsAsync<IOException>(() => _updates.StageAsync(package, releases, manifest, platform, Keys, default));
    }

    [Fact]
    public async Task Unpinned_or_forged_signature_is_rejected_before_reading_source_or_creating_files()
    {
        var bytes = Zip(("BackupPlatform.Agent.Host.exe", 0));
        var manifest = Sign(bytes);
        using var attacker = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var forged = AgentUpdateManifestSigner.Sign(manifest.Payload, attacker, "release");
        foreach (var rejected in new[] { forged, manifest with { KeyId = "unknown" } })
        {
            await Assert.ThrowsAsync<InvalidDataException>(() => _updates.DownloadAsync(Stream.Null, Path.Combine(_root, "package"), rejected, "win-x64", Keys, default));
            Assert.Empty(Directory.GetFiles(_root));
        }
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(1)]
    [InlineData(0)]
    public async Task Truncated_oversized_or_corrupt_stream_is_never_staged(int delta)
    {
        var bytes = Zip(("BackupPlatform.Agent.Host.exe", 0));
        var manifest = Sign(bytes);
        var invalid = new byte[bytes.Length + delta];
        Array.Copy(bytes, invalid, Math.Min(bytes.Length, invalid.Length));
        if (delta == 0) invalid[0] ^= 1;
        var package = Path.Combine(_root, "package");
        await Assert.ThrowsAsync<InvalidDataException>(() => _updates.DownloadAsync(new MemoryStream(invalid), package, manifest, "win-x64", Keys, default));
        Assert.False(File.Exists(package));
    }

    [Fact]
    public async Task Staging_rechecks_disk_digest_and_never_deletes_an_existing_download_target()
    {
        var bytes = Zip(("BackupPlatform.Agent.Host.exe", 0));
        var manifest = Sign(bytes);
        var package = Path.Combine(_root, "package");
        await File.WriteAllTextAsync(package, "preserve");
        await Assert.ThrowsAsync<IOException>(() => _updates.DownloadAsync(new MemoryStream(bytes), package, manifest, "win-x64", Keys, default));
        Assert.Equal("preserve", await File.ReadAllTextAsync(package));
        await Assert.ThrowsAsync<InvalidDataException>(() => _updates.StageAsync(package, Path.Combine(_root, "releases"), manifest, "win-x64", Keys, default));
        Assert.Empty(Directory.GetDirectories(Path.Combine(_root, "releases")));
    }

    [Theory]
    [InlineData("../escape", 0)]
    [InlineData("/absolute", 0)]
    [InlineData("C:/identity", 0)]
    [InlineData("folder\\escape", 0)]
    [InlineData("agent-identity.bin", 0)]
    [InlineData("appsettings.Production.json", 0)]
    [InlineData("config/runtime.json", 0)]
    [InlineData("public.pem", 0)]
    [InlineData("CON.txt", 0)]
    [InlineData("trailing.", 0)]
    [InlineData("linked", 0xA1FF)]
    [InlineData("world-writable", 0x81FF)]
    [InlineData("setuid", 0x89ED)]
    public async Task Unsafe_zip_entries_leave_no_published_release(string name, int mode)
    {
        await AssertRejectedArchiveAsync(Zip(("BackupPlatform.Agent.Host.exe", 0), (name, mode)), "win-x64");
    }

    [Fact]
    public async Task Duplicate_case_insensitive_archive_paths_are_rejected()
    {
        await AssertRejectedArchiveAsync(Zip(("BackupPlatform.Agent.Host.exe", 0), ("BACKUPPLATFORM.AGENT.HOST.EXE", 0)), "win-x64");
    }

    [Fact]
    public async Task Zip_decompression_bomb_is_rejected_by_declared_expanded_size()
    {
        var bytes = Zip(("BackupPlatform.Agent.Host.exe", 0));
        for (var i = 0; i < bytes.Length - 28; i++)
            if (bytes[i] == 0x50 && bytes[i + 1] == 0x4b && bytes[i + 2] == 1 && bytes[i + 3] == 2)
            {
                BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(i + 24, 4), 1_073_741_825);
                break;
            }
        await AssertRejectedArchiveAsync(bytes, "win-x64");
    }

    [Theory]
    [InlineData(TarEntryType.SymbolicLink)]
    [InlineData(TarEntryType.HardLink)]
    public async Task Tar_links_are_rejected(TarEntryType type) => await AssertRejectedArchiveAsync(Tar(type), "linux-x64");

    [Fact]
    public async Task Tar_traversal_and_unsafe_permissions_are_rejected()
    {
        await AssertRejectedArchiveAsync(Tar(name: "../escape"), "linux-x64");
        await AssertRejectedArchiveAsync(Tar(mode: UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.OtherWrite), "linux-x64");
    }

    private async Task AssertRejectedArchiveAsync(byte[] bytes, string platform)
    {
        var package = Path.Combine(_root, "package");
        await File.WriteAllBytesAsync(package, bytes);
        var releases = Path.Combine(_root, "releases");
        await Assert.ThrowsAsync<InvalidDataException>(() => _updates.StageAsync(package, releases, Sign(bytes, platform), platform, Keys, default));
        Assert.Empty(Directory.GetDirectories(releases));
    }

    [Theory]
    [InlineData("linux")]
    [InlineData("osx-x64")]
    [InlineData("win-arm64")]
    public void Unsupported_release_platform_is_not_accepted(string platform)
    {
        Assert.Throws<InvalidDataException>(() => AgentUpdateService.ValidateManifest(Sign([1], platform), platform, Keys));
    }

    [Theory]
    [InlineData("02.0.0")]
    [InlineData("2.0.0-beta")]
    [InlineData("2.0.0 ")]
    [InlineData("../2.0")]
    public void Noncanonical_versions_are_rejected(string version)
    {
        var manifest = Sign([1]);
        manifest = AgentUpdateManifestSigner.Sign(manifest.Payload with { Version = version }, _key, "release");
        Assert.Throws<InvalidDataException>(() => AgentUpdateService.ValidateManifest(manifest, "win-x64", Keys));
    }

    [Fact]
    public void Activation_defers_for_queued_work_and_blocks_new_work_until_restart_or_abort()
    {
        var gate = new AgentUpdateGate();
        Assert.True(gate.TryQueue(() => true));
        Assert.False(gate.TryBeginUpdate());
        gate.Complete();
        Assert.True(gate.TryBeginUpdate());
        Assert.False(gate.TryQueue(() => throw new InvalidOperationException("Must not enqueue during update.")));
        gate.CancelUpdate();
        Assert.True(gate.TryQueue(() => true));
        gate.Complete();
    }

    [Fact]
    public async Task Linux_activation_rolls_back_failed_start_but_keeps_acknowledged_release_and_preserves_state()
    {
        if (!OperatingSystem.IsLinux()) return;
        foreach (var version in new[] { "1.0.0", "2.0.0" })
        {
            Directory.CreateDirectory(Path.Combine(_root, "releases", version));
            File.WriteAllText(Path.Combine(_root, "releases", version, "BackupPlatform.Agent.Host"), "release");
        }
        File.WriteAllText(Path.Combine(_root, "identity"), "identity unchanged");
        File.WriteAllText(Path.Combine(_root, "appsettings.json"), "configuration unchanged");
        File.CreateSymbolicLink(Path.Combine(_root, "current"), "releases/1.0.0");
        var deleted = new System.Collections.Concurrent.ConcurrentQueue<string?>();
        var drained = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var watcher = new FileSystemWatcher(_root) { NotifyFilter = NotifyFilters.FileName };
        watcher.Deleted += (_, change) => deleted.Enqueue(change.Name);
        watcher.Created += (_, change) => { if (change.Name == "events-drained") drained.TrySetResult(); };
        watcher.EnableRaisingEvents = true;
        AgentReleaseActivation.BeginLinux(_root, "2.0.0");
        Assert.Equal("releases/2.0.0", new FileInfo(Path.Combine(_root, "current")).LinkTarget);
        AgentReleaseActivation.ConfirmLinux(_root, "1.0.0");
        AgentReleaseActivation.RollbackLinux(_root);
        Assert.Equal("releases/1.0.0", new FileInfo(Path.Combine(_root, "current")).LinkTarget);
        AgentReleaseActivation.BeginLinux(_root, "2.0.0");
        AgentReleaseActivation.ConfirmLinux(_root, "2.0.0");
        AgentReleaseActivation.RollbackLinux(_root);
        Assert.Equal("releases/2.0.0", new FileInfo(Path.Combine(_root, "current")).LinkTarget);
        Assert.Equal("identity unchanged", File.ReadAllText(Path.Combine(_root, "identity")));
        Assert.Equal("configuration unchanged", File.ReadAllText(Path.Combine(_root, "appsettings.json")));
        File.WriteAllText(Path.Combine(_root, "events-drained"), "");
        await drained.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.DoesNotContain("current", deleted);
        Assert.Empty(Directory.EnumerateFileSystemEntries(_root, ".link-*"));
    }

    [Fact]
    public void Linux_activation_keeps_current_release_when_link_rename_fails()
    {
        if (!OperatingSystem.IsLinux()) return;
        foreach (var version in new[] { "1.0.0", "2.0.0" })
        {
            Directory.CreateDirectory(Path.Combine(_root, "releases", version));
            File.WriteAllText(Path.Combine(_root, "releases", version, "BackupPlatform.Agent.Host"), "release");
        }
        File.CreateSymbolicLink(Path.Combine(_root, "current"), "releases/1.0.0");
        var blocked = Path.Combine(_root, ".previous");
        Directory.CreateDirectory(blocked);
        File.WriteAllText(Path.Combine(blocked, "preserve"), "existing state");

        Assert.Throws<IOException>(() => AgentReleaseActivation.BeginLinux(_root, "2.0.0"));

        Assert.Equal("releases/1.0.0", new FileInfo(Path.Combine(_root, "current")).LinkTarget);
        Assert.Equal("existing state", File.ReadAllText(Path.Combine(blocked, "preserve")));
        Assert.False(File.Exists(Path.Combine(_root, ".pending")));
        Assert.Empty(Directory.EnumerateFileSystemEntries(_root, ".link-*"));
    }

    private sealed class BoundedReadStream(byte[] bytes) : MemoryStream(bytes)
    {
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            Assert.InRange(buffer.Length, 1, 65536);
            return base.ReadAsync(buffer, cancellationToken);
        }
    }
}
