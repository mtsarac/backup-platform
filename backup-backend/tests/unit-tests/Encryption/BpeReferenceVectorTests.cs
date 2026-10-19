using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using BackupPlatform.Encryption;

namespace BackupPlatform.UnitTests.Encryption;

/// <summary>
/// The committed vectors under scripts/bpe-reference/vectors are the cross-implementation contract of the bpe v2 format: this library must keep
/// reproducing and opening them, and the independent Python reference decryptor must open them byte-exact.
/// </summary>
public sealed class BpeReferenceVectorTests
{
    private static readonly string ReferenceDirectory = FindReferenceDirectory();
    private static readonly string VectorsDirectory = Path.Combine(ReferenceDirectory, "vectors");
    private static readonly JsonElement Manifest = JsonDocument.Parse(File.ReadAllText(Path.Combine(VectorsDirectory, "manifest.json"))).RootElement;

    private static string FindReferenceDirectory()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            var candidate = Path.Combine(directory.FullName, "scripts", "bpe-reference");
            if (Directory.Exists(candidate))
            {
                return candidate;
            }
        }

        throw new DirectoryNotFoundException("scripts/bpe-reference was not found above the test output directory.");
    }

    private static string[] Names() => [.. Manifest.GetProperty("cases").EnumerateArray().Select(vector => vector.GetProperty("name").GetString()!)];

    public static TheoryData<string> CaseNames() => [.. Names()];

    private static JsonElement Case(string name) => Manifest.GetProperty("cases").EnumerateArray().Single(vector => vector.GetProperty("name").GetString() == name);

    private static RSA LoadKey(string recipient)
    {
        var rsa = RSA.Create();
        rsa.ImportFromPem(File.ReadAllText(Path.Combine(VectorsDirectory, Manifest.GetProperty("keys").GetProperty(recipient).GetProperty("private_pem").GetString()!)));
        return rsa;
    }

    private static byte[] Hex(JsonElement element) => Convert.FromHexString(element.GetString()!);

    [Fact]
    public void ManifestCoversTheRequiredCases()
    {
        Assert.Equal(["empty", "exact-boundary", "multi-chunk", "two-recipients"], Names());
        Assert.Equal(0, Case("empty").GetProperty("source_size").GetInt32());
        Assert.Equal(0, Case("exact-boundary").GetProperty("source_size").GetInt32() % Case("exact-boundary").GetProperty("chunk_size").GetInt32());
        Assert.True(Case("multi-chunk").GetProperty("source_size").GetInt32() > 2 * Case("multi-chunk").GetProperty("chunk_size").GetInt32());
        Assert.Equal(2, Case("two-recipients").GetProperty("recipients").GetArrayLength());
    }

    [Theory]
    [MemberData(nameof(CaseNames))]
    public async Task LibraryOpensEveryCommittedVectorWithEveryRecipientKey(string name)
    {
        var vector = Case(name);
        var expected = await File.ReadAllBytesAsync(Path.Combine(VectorsDirectory, vector.GetProperty("plaintext").GetString()!));
        var artifact = await File.ReadAllBytesAsync(Path.Combine(VectorsDirectory, vector.GetProperty("artifact").GetString()!));

        Assert.Equal(vector.GetProperty("plaintext_sha256").GetString(), Convert.ToHexStringLower(SHA256.HashData(expected)));
        foreach (var recipient in vector.GetProperty("recipients").EnumerateArray())
        {
            using var key = LoadKey(recipient.GetString()!);
            using var output = new MemoryStream();
            await BpeDecryptor.DecryptAsync(new MemoryStream(artifact), output, key);
            Assert.Equal(expected, output.ToArray());
        }
    }

    [Theory]
    [MemberData(nameof(CaseNames))]
    public async Task LibraryStillReproducesTheCommittedCoreAndChunkBytes(string name)
    {
        var vector = Case(name);
        var plaintext = await File.ReadAllBytesAsync(Path.Combine(VectorsDirectory, vector.GetProperty("plaintext").GetString()!));
        var committed = await File.ReadAllBytesAsync(Path.Combine(VectorsDirectory, vector.GetProperty("artifact").GetString()!));
        var recipients = vector.GetProperty("recipients").EnumerateArray().Select((recipient, index) =>
        {
            var type = recipient.GetString() == "tenant-wrap" ? BpeRecipientType.TenantWrap : BpeRecipientType.PlatformRecovery;
            return new BpeRecipient(type, index + 1, LoadKey(recipient.GetString()!));
        }).ToArray();

        using var rebuilt = new MemoryStream();
        await BpeEncryptor.EncryptAsync(new MemoryStream(plaintext), rebuilt, plaintext.Length, recipients,
            new BpeEncryptOptions(vector.GetProperty("chunk_size").GetInt32(), Hex(vector.GetProperty("dek")), Hex(vector.GetProperty("artifact_id")), Hex(vector.GetProperty("nonce_prefix"))));
        var fresh = rebuilt.ToArray();

        // RSA-OAEP wraps (hence recipients_mac and, through JSON escaping of '+', even the header length) are randomized; core and every chunk byte are not.
        static int HeaderEnd(byte[] artifact) => 12 + (int)System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(artifact.AsSpan(8));
        Assert.Equal(committed[..8], fresh[..8]); // magic + version; header_len varies
        Assert.Equal(committed[12..(12 + BpeFormat.CoreLength)], fresh[12..(12 + BpeFormat.CoreLength)]);
        Assert.Equal(committed[HeaderEnd(committed)..], fresh[HeaderEnd(fresh)..]);
    }

    [Fact]
    public async Task SharesOfTheCommittedRecoveryKeyReconstructItAndOpenTheRecoveryVector()
    {
        var shares = Directory.GetFiles(Path.Combine(VectorsDirectory, Manifest.GetProperty("recovery_shares").GetProperty("dir").GetString()!), "*.bpshare").Order().ToArray();
        using var recovery = await BpeRecoveryKey.FromShareFilesAsync([shares[3], shares[0], shares[1]]);
        var vector = Case("two-recipients");
        using var output = new MemoryStream();

        await BpeDecryptor.DecryptAsync(
            new MemoryStream(await File.ReadAllBytesAsync(Path.Combine(VectorsDirectory, "two-recipients.bpe"))), output, recovery);

        Assert.Equal(await File.ReadAllBytesAsync(Path.Combine(VectorsDirectory, vector.GetProperty("plaintext").GetString()!)), output.ToArray());
    }

    [PythonFact]
    public async Task PythonReferenceSuiteDecryptsEveryVectorByteExact()
    {
        var run = await PythonFactAttribute.RunAsync(ReferenceDirectory, "-W", "error", "-m", "unittest", "discover", "-s", ".");

        Assert.True(run.ExitCode == 0, run.Output);
    }

    [PythonFact]
    public async Task PythonOpensAFreshlyEncryptedArtifactWithTheKeyAndWithReconstructedShares()
    {
        using var directory = new TempDirectory();
        var ceremony = BpeRecoveryKey.Generate(4096, 3, 5);
        using var recovery = RSA.Create();
        recovery.ImportFromPem(ceremony.PublicKeyPem);
        using var tenant = RSA.Create(4096);
        var plaintext = RandomNumberGenerator.GetBytes(2 * (1 << 20) + 5);
        var artifact = directory.File("fresh.bpe");
        await using (var stream = File.Create(artifact))
        {
            await BpeEncryptor.EncryptAsync(new MemoryStream(plaintext), stream, plaintext.Length,
                [new(BpeRecipientType.TenantWrap, 1, tenant), new(BpeRecipientType.PlatformRecovery, 1, recovery)]);
        }

        var tenantPem = directory.File("tenant.pem");
        await File.WriteAllTextAsync(tenantPem, tenant.ExportPkcs8PrivateKeyPem());
        var withKey = await PythonFactAttribute.RunAsync(ReferenceDirectory, "bpe_decrypt.py", "--in", artifact, "--out", directory.File("a.bin"), "--key", tenantPem);
        var shareArgs = ceremony.Shares.Skip(1).Take(3).SelectMany((share, index) =>
        {
            var path = directory.File($"share-{index}.bpshare");
            File.WriteAllText(path, ShamirSecretSharing.ToText(share));
            return new[] { "--share", path };
        });
        var withShares = await PythonFactAttribute.RunAsync(ReferenceDirectory, ["bpe_decrypt.py", "--in", artifact, "--out", directory.File("b.bin"), .. shareArgs]);

        Assert.True(withKey.ExitCode == 0, withKey.Output);
        Assert.True(withShares.ExitCode == 0, withShares.Output);
        Assert.Equal(plaintext, await File.ReadAllBytesAsync(directory.File("a.bin")));
        Assert.Equal(plaintext, await File.ReadAllBytesAsync(directory.File("b.bin")));
    }

    private sealed class TempDirectory : IDisposable
    {
        private readonly string path = Directory.CreateTempSubdirectory("bpe-ref-").FullName;

        public string File(string name) => Path.Combine(path, name);

        public void Dispose() => Directory.Delete(path, recursive: true);
    }
}

/// <summary>
/// Runs only when a Python 3 interpreter with the <c>cryptography</c> package is available (override the interpreter with BPE_PYTHON);
/// otherwise the skip reason names what is missing, so a green run never silently omits the cross-implementation check.
/// </summary>
public sealed class PythonFactAttribute : FactAttribute
{
    private static readonly Lazy<(string? Interpreter, string Reason)> Probe = new(FindInterpreter);

    public PythonFactAttribute()
    {
        if (Probe.Value.Interpreter is null)
        {
            Skip = Probe.Value.Reason;
        }
    }

    internal static async Task<(int ExitCode, string Output)> RunAsync(string workingDirectory, params string[] arguments)
    {
        var start = new ProcessStartInfo(Probe.Value.Interpreter!) { WorkingDirectory = workingDirectory, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var argument in arguments)
        {
            start.ArgumentList.Add(argument);
        }

        using var process = Process.Start(start)!;
        var output = process.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        return (process.ExitCode, await output + await error);
    }

    private static (string? Interpreter, string Reason) FindInterpreter()
    {
        foreach (var candidate in new[] { Environment.GetEnvironmentVariable("BPE_PYTHON"), "python3", "python" }.OfType<string>())
        {
            try
            {
                using var process = Process.Start(new ProcessStartInfo(candidate, ["-c", "import cryptography"]) { RedirectStandardError = true, RedirectStandardOutput = true })!;
                process.WaitForExit();
                if (process.ExitCode == 0)
                {
                    return (candidate, string.Empty);
                }
            }
            catch (System.ComponentModel.Win32Exception)
            {
                // Candidate is not installed; try the next one.
            }
        }

        return (null, "Requires Python 3 with the 'cryptography' package (set BPE_PYTHON to an interpreter that has it).");
    }
}
