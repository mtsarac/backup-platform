using System.Security.Cryptography;
using BackupPlatform.Encryption;

namespace BackupPlatform.Tools;

/// <summary>
/// Offline break-glass decryptor: needs only the .bpe file and either a PEM private key or at least <c>threshold</c> Recovery Key share files
/// (no backend, no network, no database). The recipient is selected by the key id recorded in the artifact header, so the same tool opens
/// tenant-wrap, platform-recovery and tenant-recovery artifacts.
/// </summary>
internal static class BpDecryptCli
{
    private const string Usage =
        "usage: bp-decrypt --in <artifact.bpe> --out <plaintext> (--key <private-key.pem> | --share <file> [--share <file> ...])";

    private static readonly HashSet<string> Options = ["--in", "--out", "--key", "--share"];

    public static async Task<int> RunAsync(string[] args, TextWriter output, TextWriter error)
    {
        var parsed = CliArguments.Parse(args, Options, out var parseError);
        var inPath = parsed?.Single("--in");
        var outPath = parsed?.Single("--out");
        var keyPath = parsed?.Single("--key");
        var sharePaths = parsed?.All("--share") ?? [];
        var exactlyOneKeySource = parsed is not null && (parsed.Has("--key") ? keyPath is not null && sharePaths.Count == 0 : sharePaths.Count > 0);
        if (parsed is null || inPath is null || outPath is null || !exactlyOneKeySource)
        {
            await error.WriteLineAsync(parseError is null ? Usage : $"bp-decrypt: {parseError}\n{Usage}");
            return 2;
        }

        var temp = outPath + "." + Guid.NewGuid().ToString("N") + ".partial";
        try
        {
            using var rsa = keyPath is not null ? ImportKey(await File.ReadAllTextAsync(keyPath)) : await BpeRecoveryKey.FromShareFilesAsync(sharePaths);
            var keyId = BpeKeyId.FromSpki(rsa.ExportSubjectPublicKeyInfo());
            IReadOnlyList<BpeRecipientEntry> recipients;
            await using (var header = File.OpenRead(inPath))
            {
                recipients = await BpeDecryptor.ReadRecipientsAsync(header);
            }

            var match = recipients.FirstOrDefault(recipient => recipient.KeyId == keyId);
            if (match is null)
            {
                await error.WriteLineAsync($"bp-decrypt: the supplied key (key_id {keyId}) is not a recipient of this artifact. Recipients: "
                    + string.Join(", ", recipients.Select(recipient => $"{recipient.Type.WireName()} v{recipient.KeyVersion} key_id {recipient.KeyId}")));
                return 1;
            }

            var fileOptions = new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write, Share = FileShare.None };
            if (!OperatingSystem.IsWindows())
            {
                fileOptions.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
            }

            await using (var input = File.OpenRead(inPath))
            await using (var plaintext = new FileStream(temp, fileOptions))
            {
                await BpeDecryptor.DecryptAsync(input, plaintext, rsa);
            }

            // Only a fully authenticated stream is published under the requested name.
            File.Move(temp, outPath, overwrite: true);
            await output.WriteLineAsync($"decrypted with {match.Type.WireName()} recipient (key version {match.KeyVersion}, key_id {match.KeyId})");
            return 0;
        }
        catch (Exception exception) when (exception is BpeFormatException or BpeShareException or IOException or UnauthorizedAccessException or CryptographicException or ArgumentException)
        {
            File.Delete(temp);
            await error.WriteLineAsync($"bp-decrypt: {exception.Message}");
            return 1;
        }
    }

    private static RSA ImportKey(string pem)
    {
        var rsa = RSA.Create();
        rsa.ImportFromPem(pem);
        return rsa;
    }
}
