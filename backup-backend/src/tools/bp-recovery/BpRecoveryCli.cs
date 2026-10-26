using System.Security.Cryptography;
using BackupPlatform.Encryption;

namespace BackupPlatform.Tools;

/// <summary>
/// Recovery Key (RK) ceremony tool. Runs offline: <c>generate</c> creates the RSA keypair and writes only the public PEM plus one file per
/// Shamir share (the private key is never written in one piece); <c>verify</c> proves a set of shares against the public PEM without
/// materialising the key on disk; <c>combine</c> writes the private PEM for a manual break-glass session and must be used on an isolated machine.
/// </summary>
internal static class BpRecoveryCli
{
    public const string PublicKeyFileName = "recovery-public.pem";
    private const string Usage =
        "usage:\n"
        + "  bp-recovery generate --out-dir <dir> [--threshold 3] [--shares 5] [--bits 4096]\n"
        + "  bp-recovery verify --public <recovery-public.pem> --share <file> [--share <file> ...]\n"
        + "  bp-recovery combine --out <private-key.pem> --share <file> [--share <file> ...]";

    private static readonly HashSet<string> Options = ["--out-dir", "--threshold", "--shares", "--bits", "--public", "--share", "--out"];

    public static async Task<int> RunAsync(string[] args, TextWriter output, TextWriter error)
    {
        string? parseError = null;
        var parsed = args.Length > 0 ? CliArguments.Parse(args[1..], Options, out parseError) : null;
        if (parsed is null)
        {
            await error.WriteLineAsync(args.Length > 0 && parseError is not null ? $"bp-recovery: {parseError}\n{Usage}" : Usage);
            return 2;
        }

        try
        {
            return args[0] switch
            {
                "generate" when parsed.Single("--out-dir") is { } dir => await GenerateAsync(dir, parsed, output),
                "verify" when parsed.Single("--public") is { } publicPath && parsed.All("--share").Count > 0 => await VerifyAsync(publicPath, parsed.All("--share"), output),
                "combine" when parsed.Single("--out") is { } outPath && parsed.All("--share").Count > 0 => await CombineAsync(outPath, parsed.All("--share"), output, error),
                _ => await UsageAsync(error)
            };
        }
        catch (Exception exception) when (exception is BpeShareException or IOException or CryptographicException or ArgumentException or FormatException)
        {
            await error.WriteLineAsync($"bp-recovery: {exception.Message}");
            return 1;
        }
    }

    private static async Task<int> UsageAsync(TextWriter error)
    {
        await error.WriteLineAsync(Usage);
        return 2;
    }

    private static async Task<int> GenerateAsync(string dir, CliArguments parsed, TextWriter output)
    {
        var threshold = ParseInt(parsed.Single("--threshold"), BpeRecoveryKey.DefaultThreshold);
        var total = ParseInt(parsed.Single("--shares"), BpeRecoveryKey.DefaultShares);
        var ceremony = BpeRecoveryKey.Generate(ParseInt(parsed.Single("--bits"), BpeRecoveryKey.DefaultBits), threshold, total);
        Directory.CreateDirectory(dir);
        var written = new List<string>();
        try
        {
            for (var i = 0; i < ceremony.Shares.Count; i++)
            {
                var path = Path.Combine(dir, $"recovery-share-{i + 1}-of-{total}.bpshare");
                await WriteNewAsync(path, ShamirSecretSharing.ToText(ceremony.Shares[i]) + "\n", ownerOnly: true);
                written.Add(path);
            }

            var publicPath = Path.Combine(dir, PublicKeyFileName);
            await WriteNewAsync(publicPath, ceremony.PublicKeyPem, ownerOnly: false);
            written.Add(publicPath);
        }
        catch
        {
            written.ForEach(File.Delete);
            throw;
        }

        await output.WriteLineAsync($"Recovery key {ceremony.KeyId}: {threshold}-of-{total} shares written to {dir}.");
        await output.WriteLineAsync($"Configure Encryption:PlatformRecoveryPublicKeyPem with {PublicKeyFileName}. Hand each share to a different custodian and remove them from this machine.");
        return 0;
    }

    private static async Task<int> VerifyAsync(string publicPath, IReadOnlyList<string> sharePaths, TextWriter output)
    {
        using var expected = RSA.Create();
        expected.ImportFromPem(await File.ReadAllTextAsync(publicPath));
        using var reconstructed = await BpeRecoveryKey.FromShareFilesAsync(sharePaths);
        var (expectedId, actualId) = (BpeKeyId.FromSpki(expected.ExportSubjectPublicKeyInfo()), BpeKeyId.FromSpki(reconstructed.ExportSubjectPublicKeyInfo()));
        if (expectedId != actualId)
        {
            throw new BpeShareException($"The shares reconstruct key {actualId}, not the key {expectedId} in {publicPath}.");
        }

        await output.WriteLineAsync($"OK: {sharePaths.Count} shares reconstruct recovery key {actualId}.");
        return 0;
    }

    private static async Task<int> CombineAsync(string outPath, IReadOnlyList<string> sharePaths, TextWriter output, TextWriter error)
    {
        using var rsa = await BpeRecoveryKey.FromShareFilesAsync(sharePaths);
        await WriteNewAsync(outPath, rsa.ExportPkcs8PrivateKeyPem() + "\n", ownerOnly: true);
        await error.WriteLineAsync($"WARNING: {outPath} is the complete recovery private key. Use it only on an isolated machine and shred it afterwards.");
        await output.WriteLineAsync($"Recovery key {BpeKeyId.FromSpki(rsa.ExportSubjectPublicKeyInfo())} written to {outPath}.");
        return 0;
    }

    private static int ParseInt(string? value, int fallback) =>
        value is null ? fallback : int.TryParse(value, out var parsed) ? parsed : throw new ArgumentException($"'{value}' is not a number.");

    /// <summary>Never overwrites: replacing a custodian's share or a key file by accident is unrecoverable.</summary>
    private static async Task WriteNewAsync(string path, string content, bool ownerOnly)
    {
        var options = new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write, Share = FileShare.None };
        // Windows has no POSIX mode bits; the operator must protect the output directory with an ACL there.
        if (ownerOnly && !OperatingSystem.IsWindows())
        {
            options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
        }

        await using var stream = new FileStream(path, options);
        await using var writer = new StreamWriter(stream);
        await writer.WriteAsync(content);
    }
}
