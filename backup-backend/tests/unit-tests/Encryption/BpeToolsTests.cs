using System.Security.Cryptography;
using BackupPlatform.Encryption;
using BackupPlatform.Tools;

namespace BackupPlatform.UnitTests.Encryption;

/// <summary>End-to-end runs of the offline tools against real files: ceremony -> artifact on disk -> manual decryption.</summary>
public sealed class BpeToolsTests : IDisposable
{
    private readonly string directory = Directory.CreateTempSubdirectory("bp-tools-").FullName;

    public void Dispose() => Directory.Delete(directory, recursive: true);

    private string Path(string name) => System.IO.Path.Combine(directory, name);

    private static async Task<(int Code, string Out, string Err)> RunAsync(Func<string[], TextWriter, TextWriter, Task<int>> tool, params string[] args)
    {
        using var output = new StringWriter();
        using var error = new StringWriter();
        var code = await tool(args, output, error);
        return (code, output.ToString(), error.ToString());
    }

    private static Task<(int Code, string Out, string Err)> Recovery(params string[] args) => RunAsync(BpRecoveryCli.RunAsync, args);

    private static Task<(int Code, string Out, string Err)> Decrypt(params string[] args) => RunAsync(BpDecryptCli.RunAsync, args);

    private async Task<(string[] Shares, string PublicPem, string ArtifactPath, byte[] Plaintext, string TenantKeyPath)> CeremonyAndArtifactAsync(string subdirectory)
    {
        var ceremonyDir = Path(subdirectory);
        var generated = await Recovery("generate", "--out-dir", ceremonyDir);
        Assert.Equal(0, generated.Code);

        using var recovery = RSA.Create();
        recovery.ImportFromPem(await File.ReadAllTextAsync(System.IO.Path.Combine(ceremonyDir, BpRecoveryCli.PublicKeyFileName)));
        using var tenant = RSA.Create(4096);
        var tenantKeyPath = Path($"{subdirectory}-tenant.pem");
        await File.WriteAllTextAsync(tenantKeyPath, tenant.ExportPkcs8PrivateKeyPem());
        var plaintext = RandomNumberGenerator.GetBytes(3 * (1 << 20) + 17);
        var artifactPath = Path($"{subdirectory}.bpe");
        await using (var artifact = File.Create(artifactPath))
        {
            await BpeEncryptor.EncryptAsync(new MemoryStream(plaintext), artifact, plaintext.Length,
                [new(BpeRecipientType.TenantWrap, 1, tenant), new(BpeRecipientType.PlatformRecovery, 1, recovery)]);
        }

        var shares = Directory.GetFiles(ceremonyDir, "*.bpshare").Order().ToArray();
        return (shares, System.IO.Path.Combine(ceremonyDir, BpRecoveryCli.PublicKeyFileName), artifactPath, plaintext, tenantKeyPath);
    }

    [Fact]
    public async Task GenerateWritesOnlyPublicKeyAndSharesAndAnyThreeSharesDecryptARealArtifact()
    {
        var (shares, publicPem, artifact, plaintext, _) = await CeremonyAndArtifactAsync("rk");

        Assert.Equal(5, shares.Length);
        Assert.Equal(6, Directory.GetFiles(Path("rk")).Length);
        Assert.DoesNotContain(Directory.GetFiles(Path("rk")), file => File.ReadAllText(file).Contains("PRIVATE KEY"));
        Assert.All(shares, share =>
        {
            if (!OperatingSystem.IsWindows())
            {
                Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(share));
            }
        });

        var outPath = Path("plain.bin");
        var result = await Decrypt("--in", artifact, "--out", outPath, "--share", shares[4], "--share", shares[0], "--share", shares[2]);

        Assert.Equal(0, result.Code);
        Assert.Contains("platform-recovery", result.Out);
        Assert.Equal(plaintext, await File.ReadAllBytesAsync(outPath));
        Assert.False(File.Exists(outPath + ".partial"));
        if (!OperatingSystem.IsWindows())
        {
            Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(outPath));
        }
        Assert.Contains("PUBLIC KEY", await File.ReadAllTextAsync(publicPem));
    }

    [Fact]
    public async Task FewerThanThresholdSharesFailAndPublishNothing()
    {
        var (shares, _, artifact, _, _) = await CeremonyAndArtifactAsync("rk");
        var outPath = Path("plain.bin");

        var result = await Decrypt("--in", artifact, "--out", outPath, "--share", shares[0], "--share", shares[1]);

        Assert.Equal(1, result.Code);
        Assert.Contains("3 distinct shares are required", result.Err);
        Assert.False(File.Exists(outPath));
        Assert.False(File.Exists(outPath + ".partial"));
    }

    [Fact]
    public async Task SharesOfDifferentCeremoniesAreRejected()
    {
        var first = await CeremonyAndArtifactAsync("rk1");
        var second = await CeremonyAndArtifactAsync("rk2");

        var result = await Decrypt("--in", first.ArtifactPath, "--out", Path("plain.bin"), "--share", first.Shares[0], "--share", first.Shares[1], "--share", second.Shares[2]);

        Assert.Equal(1, result.Code);
        Assert.Contains("different sets", result.Err);
        Assert.False(File.Exists(Path("plain.bin")));
    }

    [Fact]
    public async Task ATamperedShareFileIsRejected()
    {
        var (shares, _, artifact, _, _) = await CeremonyAndArtifactAsync("rk");
        var tampered = Path("tampered.bpshare");
        var text = (await File.ReadAllTextAsync(shares[0])).Trim();
        await File.WriteAllTextAsync(tampered, text[..^6] + (text[^6] == 'A' ? 'B' : 'A') + text[^5..]);

        var result = await Decrypt("--in", artifact, "--out", Path("plain.bin"), "--share", tampered, "--share", shares[1], "--share", shares[2]);

        Assert.Equal(1, result.Code);
        Assert.False(File.Exists(Path("plain.bin")));
    }

    [Fact]
    public async Task TheTenantWrapKeyAlsoDecryptsAndTheToolNamesTheRecipientUsed()
    {
        var (_, _, artifact, plaintext, tenantKey) = await CeremonyAndArtifactAsync("rk");

        var result = await Decrypt("--in", artifact, "--out", Path("plain.bin"), "--key", tenantKey);

        Assert.Equal(0, result.Code);
        Assert.Contains("tenant-wrap", result.Out);
        Assert.Equal(plaintext, await File.ReadAllBytesAsync(Path("plain.bin")));
    }

    [Fact]
    public async Task AKeyThatIsNotARecipientIsRefusedWithTheRecipientList()
    {
        var (_, _, artifact, _, _) = await CeremonyAndArtifactAsync("rk");
        using var stranger = RSA.Create(4096);
        var strangerPath = Path("stranger.pem");
        await File.WriteAllTextAsync(strangerPath, stranger.ExportPkcs8PrivateKeyPem());

        var result = await Decrypt("--in", artifact, "--out", Path("plain.bin"), "--key", strangerPath);

        Assert.Equal(1, result.Code);
        Assert.Contains("not a recipient", result.Err);
        Assert.Contains("tenant-wrap", result.Err);
        Assert.Contains("platform-recovery", result.Err);
        Assert.False(File.Exists(Path("plain.bin")));
    }

    [Fact]
    public async Task ACorruptedArtifactNeverLeavesPlaintextBehind()
    {
        var (shares, _, artifact, _, _) = await CeremonyAndArtifactAsync("rk");
        var bytes = await File.ReadAllBytesAsync(artifact);
        bytes[^100] ^= 1; // inside the final chunk: earlier chunks were already written to the .partial file
        await File.WriteAllBytesAsync(artifact, bytes);

        var result = await Decrypt("--in", artifact, "--out", Path("plain.bin"), "--share", shares[0], "--share", shares[1], "--share", shares[2]);

        Assert.Equal(1, result.Code);
        Assert.False(File.Exists(Path("plain.bin")));
        Assert.False(File.Exists(Path("plain.bin.partial")));
        Assert.Empty(Directory.GetFiles(directory, "*.partial"));
    }

    [Fact]
    public async Task DecryptionNeverTouchesAnExistingPartialFile()
    {
        var (_, _, artifact, plaintext, tenantKey) = await CeremonyAndArtifactAsync("rk");
        var outPath = Path("plain.bin");
        await File.WriteAllTextAsync(outPath + ".partial", "do not touch");

        var result = await Decrypt("--in", artifact, "--out", outPath, "--key", tenantKey);

        Assert.Equal(0, result.Code);
        Assert.Equal(plaintext, await File.ReadAllBytesAsync(outPath));
        Assert.Equal("do not touch", await File.ReadAllTextAsync(outPath + ".partial"));
    }

    [Fact]
    public async Task VerifyChecksSharesAgainstThePublicKeyWithoutWritingTheKey()
    {
        var good = await CeremonyAndArtifactAsync("rk1");
        var other = await CeremonyAndArtifactAsync("rk2");

        var ok = await Recovery("verify", "--public", good.PublicPem, "--share", good.Shares[1], "--share", good.Shares[3], "--share", good.Shares[4]);
        var wrongKey = await Recovery("verify", "--public", other.PublicPem, "--share", good.Shares[1], "--share", good.Shares[3], "--share", good.Shares[4]);
        var tooFew = await Recovery("verify", "--public", good.PublicPem, "--share", good.Shares[1]);

        Assert.Equal(0, ok.Code);
        Assert.Equal(1, wrongKey.Code);
        Assert.Equal(1, tooFew.Code);
    }

    [Fact]
    public async Task CombineWritesTheKeyOnceAndItDecryptsWithKeyFlag()
    {
        var (shares, _, artifact, plaintext, _) = await CeremonyAndArtifactAsync("rk");
        var keyPath = Path("rk-private.pem");

        var combined = await Recovery("combine", "--out", keyPath, "--share", shares[0], "--share", shares[2], "--share", shares[3]);
        var again = await Recovery("combine", "--out", keyPath, "--share", shares[0], "--share", shares[2], "--share", shares[3]);
        var decrypted = await Decrypt("--in", artifact, "--out", Path("plain.bin"), "--key", keyPath);

        Assert.Equal(0, combined.Code);
        Assert.Contains("WARNING", combined.Err);
        Assert.Equal(1, again.Code);
        Assert.Equal(0, decrypted.Code);
        Assert.Equal(plaintext, await File.ReadAllBytesAsync(Path("plain.bin")));
    }

    [Fact]
    public async Task GenerateNeverOverwritesAnExistingCeremony()
    {
        await Recovery("generate", "--out-dir", Path("rk"));
        var before = await File.ReadAllTextAsync(Path("rk/recovery-share-1-of-5.bpshare"));

        var result = await Recovery("generate", "--out-dir", Path("rk"));

        Assert.Equal(1, result.Code);
        Assert.Equal(before, await File.ReadAllTextAsync(Path("rk/recovery-share-1-of-5.bpshare")));
    }

    [Fact]
    public async Task DefaultCeremonyIsRsa4096ThreeOfFive()
    {
        var result = await Recovery("generate", "--out-dir", Path("rk"));

        Assert.Equal(0, result.Code);
        Assert.Equal(5, Directory.GetFiles(Path("rk"), "*.bpshare").Length);
        Assert.Contains("3-of-5", result.Out);
        using var key = RSA.Create();
        key.ImportFromPem(await File.ReadAllTextAsync(Path("rk/recovery-public.pem")));
        Assert.Equal(4096, key.KeySize);
    }

    [Fact]
    public async Task ConfigurableThresholdIsHonoured()
    {
        await Recovery("generate", "--out-dir", Path("rk"), "--threshold", "2", "--shares", "3");
        var shares = Directory.GetFiles(Path("rk"), "*.bpshare").Order().ToArray();

        Assert.Equal(3, shares.Length);
        Assert.Equal(0, (await Recovery("verify", "--public", Path("rk/recovery-public.pem"), "--share", shares[0], "--share", shares[2])).Code);
        Assert.Equal(1, (await Recovery("verify", "--public", Path("rk/recovery-public.pem"), "--share", shares[0])).Code);
    }

    [Theory]
    [InlineData("--in", "a", "--out", "b")]
    [InlineData("--in", "a", "--out", "b", "--key", "k", "--share", "s")]
    [InlineData("--in", "a", "--key", "k")]
    [InlineData("--bogus")]
    public async Task DecryptRejectsIncompleteOrAmbiguousUsage(params string[] args)
    {
        var result = await Decrypt(args);

        Assert.Equal(2, result.Code);
        Assert.Contains("usage: bp-decrypt", result.Err);
    }

    [Theory]
    [InlineData]
    [InlineData("generate")]
    [InlineData("combine", "--share", "x")]
    [InlineData("rotate")]
    public async Task RecoveryRejectsUnknownOrIncompleteCommands(params string[] args)
    {
        var result = await Recovery(args);

        Assert.Equal(2, result.Code);
        Assert.Contains("bp-recovery generate", result.Err);
    }
}
