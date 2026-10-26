#:project ../../backup-backend/src/core/encryption/BackupPlatform.Encryption.csproj
#:property JsonSerializerIsReflectionEnabledByDefault=true
#:property PublishAot=false

// Regenerates the committed bpe v2 test vectors with the .NET reference library.
//   dotnet run scripts/bpe-reference/generate_vectors.cs -- [scripts/bpe-reference/vectors] [--new-keys]
// DEK, artifact id and nonce prefix are derived from the case name, so core and chunk bytes are reproducible; only the RSA-OAEP wraps in the
// header are randomized (the encryptor zeroes the DEK it is given, hence the clone). The RSA keys here are throw-away TEST keys that protect nothing; they are committed on purpose so that any
// implementation can decrypt the vectors. Never reuse them anywhere else.
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using BackupPlatform.Encryption;

var root = args.FirstOrDefault(arg => !arg.StartsWith("--")) ?? "scripts/bpe-reference/vectors";
const int ChunkSize = 64;
Directory.CreateDirectory(Path.Combine(root, "keys"));
Directory.CreateDirectory(Path.Combine(root, "shares"));

RSA LoadOrCreate(string name)
{
    var path = Path.Combine(root, "keys", $"{name}.private.pem");
    var rsa = RSA.Create();
    if (File.Exists(path) && !args.Contains("--new-keys"))
    {
        rsa.ImportFromPem(File.ReadAllText(path));
    }
    else
    {
        rsa.Dispose();
        rsa = RSA.Create(4096);
        File.WriteAllText(path, rsa.ExportPkcs8PrivateKeyPem() + "\n");
        File.WriteAllText(Path.Combine(root, "keys", $"{name}.public.pem"), rsa.ExportSubjectPublicKeyInfoPem());
    }

    return rsa;
}

var keys = new Dictionary<string, RSA>
{
    ["tenant-wrap"] = LoadOrCreate("tenant-wrap"),
    ["platform-recovery"] = LoadOrCreate("platform-recovery")
};

byte[] Derive(string name, string purpose) => SHA256.HashData(Encoding.UTF8.GetBytes($"bpe-vector:{purpose}:{name}"));
byte[] Plaintext(int size) => [.. Enumerable.Range(0, size).Select(i => (byte)(i * 31 + 7))];

var cases = new (string Name, int Size, string[] Recipients)[]
{
    ("empty", 0, ["tenant-wrap"]),
    ("exact-boundary", 2 * ChunkSize, ["tenant-wrap"]),
    ("multi-chunk", 3 * ChunkSize + 8, ["tenant-wrap"]),
    ("two-recipients", 3 * ChunkSize + 8, ["tenant-wrap", "platform-recovery"])
};

var manifestCases = new List<object>();
foreach (var (name, size, recipientNames) in cases)
{
    var plaintext = Plaintext(size);
    var (dek, artifactId, noncePrefix) = (Derive(name, "dek"), Derive(name, "id")[..16], Derive(name, "nonce")[..4]);
    var recipients = recipientNames.Select((recipient, index) =>
        new BpeRecipient(recipient == "tenant-wrap" ? BpeRecipientType.TenantWrap : BpeRecipientType.PlatformRecovery, index + 1, keys[recipient])).ToArray();
    using var artifact = new MemoryStream();
    await BpeEncryptor.EncryptAsync(new MemoryStream(plaintext), artifact, size, recipients, new BpeEncryptOptions(ChunkSize, (byte[])dek.Clone(), artifactId, noncePrefix));
    File.WriteAllBytes(Path.Combine(root, $"{name}.bpe"), artifact.ToArray());
    File.WriteAllBytes(Path.Combine(root, $"{name}.bin"), plaintext);
    manifestCases.Add(new
    {
        name,
        artifact = $"{name}.bpe",
        plaintext = $"{name}.bin",
        plaintext_sha256 = Convert.ToHexStringLower(SHA256.HashData(plaintext)),
        dek = Convert.ToHexStringLower(dek),
        artifact_id = Convert.ToHexStringLower(artifactId),
        nonce_prefix = Convert.ToHexStringLower(noncePrefix),
        chunk_size = ChunkSize,
        source_size = size,
        recipients = recipientNames
    });
}

var recoveryPkcs8 = keys["platform-recovery"].ExportPkcs8PrivateKey();
var shares = ShamirSecretSharing.Split(recoveryPkcs8, 3, 5);
foreach (var file in Directory.GetFiles(Path.Combine(root, "shares")))
{
    File.Delete(file);
}

for (var i = 0; i < shares.Count; i++)
{
    File.WriteAllText(Path.Combine(root, "shares", $"recovery-share-{i + 1}-of-5.bpshare"), ShamirSecretSharing.ToText(shares[i]) + "\n");
}

var manifest = new
{
    format = "bpe-v2",
    keys = keys.ToDictionary(pair => pair.Key, pair => new
    {
        private_pem = $"keys/{pair.Key}.private.pem",
        public_pem = $"keys/{pair.Key}.public.pem",
        key_id = BpeKeyId.FromSpki(pair.Value.ExportSubjectPublicKeyInfo())
    }),
    recovery_shares = new { key = "platform-recovery", threshold = 3, total = 5, dir = "shares" },
    cases = manifestCases
};
File.WriteAllText(Path.Combine(root, "manifest.json"), JsonSerializer.Serialize(manifest, new JsonSerializerOptions { WriteIndented = true }) + "\n");
Console.WriteLine($"Vectors written to {root}");
