using System.Security.Cryptography;
using BackupPlatform.Encryption;

// Offline break-glass decryptor: needs only the .bpe file and a PEM private key (no backend, no network, no database).
if (args.Length != 6 || args[0] != "--in" || args[2] != "--key" || args[4] != "--out")
{
    Console.Error.WriteLine("usage: bp-decrypt --in <artifact.bpe> --key <private-key.pem> --out <plaintext>");
    return 2;
}

var (inPath, keyPath, outPath) = (args[1], args[3], args[5]);
var temp = outPath + ".partial";
try
{
    using var rsa = RSA.Create();
    rsa.ImportFromPem(await File.ReadAllTextAsync(keyPath));
    await using (var input = File.OpenRead(inPath))
    await using (var output = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None))
    {
        await BpeDecryptor.DecryptAsync(input, output, rsa);
    }

    // Only a fully authenticated stream is published under the requested name.
    File.Move(temp, outPath, overwrite: true);
    return 0;
}
catch (Exception exception) when (exception is BpeFormatException or IOException or CryptographicException or ArgumentException)
{
    File.Delete(temp);
    Console.Error.WriteLine($"bp-decrypt: {exception.Message}");
    return 1;
}
