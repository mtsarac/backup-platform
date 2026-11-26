using System.Security.Cryptography.X509Certificates;
using System.Xml.Linq;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.DataProtection.XmlEncryption;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace BackupPlatform.Api.Security;

/// <summary>
/// Encrypts the Data Protection key ring at rest with an X.509 certificate (<c>Secrets:DataProtection:CertificatePath</c>, optional
/// <c>CertificatePassword</c>). <c>PreviousCertificatePaths</c> are accepted for decryption only, so a certificate can be rotated.
/// Keys written before a certificate was configured are rewrapped once, in place, when the key ring is first opened.
/// </summary>
public static class DataProtectionKeyRing
{
    private static readonly XNamespace Ns = "http://schemas.asp.net/2015/03/dataProtection";
    private const string Section = "Secrets:DataProtection";

    public static void Configure(IDataProtectionBuilder builder, IConfiguration configuration, IHostEnvironment environment)
    {
        var path = configuration[$"{Section}:CertificatePath"];
        if (string.IsNullOrWhiteSpace(path))
        {
            if (environment.IsProduction())
                throw new InvalidOperationException($"{Section}:CertificatePath is required in Production: the Data Protection key ring must be encrypted with a certificate.");
            return;
        }

        var password = configuration[$"{Section}:CertificatePassword"];
        var current = Load(path, password);
        var previous = (configuration.GetSection($"{Section}:PreviousCertificatePaths").Get<string[]>() ?? [])
            .Select(p => Load(p, password)).ToArray();
        builder.ProtectKeysWithCertificate(current).UnprotectKeysWithAnyCertificate([current, .. previous]);

        var directory = configuration[$"{Section}:KeysDirectory"];
        if (!string.IsNullOrWhiteSpace(directory) && Directory.Exists(directory))
            Rewrap(new DirectoryInfo(directory), current, NullLoggerFactory.Instance);
    }

    private static X509Certificate2 Load(string path, string? password) =>
        X509CertificateLoader.LoadPkcs12FromFile(path, string.IsNullOrEmpty(password) ? null : password, X509KeyStorageFlags.EphemeralKeySet);

    /// <summary>Encrypts every key file that still holds its master key in clear text. Returns how many files were rewritten; already encrypted files are left untouched.</summary>
    public static int Rewrap(DirectoryInfo directory, X509Certificate2 certificate, ILoggerFactory loggerFactory)
    {
        var encryptor = new CertificateXmlEncryptor(certificate, loggerFactory);
        var rewritten = 0;
        foreach (var file in directory.EnumerateFiles("key-*.xml"))
        {
            var document = XDocument.Load(file.FullName);
            var plain = document.Descendants().Where(e => e.Attributes().Any(a => a.Name.LocalName == "requiresEncryption" && a.Value == "true")).ToList();
            if (plain.Count == 0) continue;
            foreach (var element in plain)
            {
                var info = encryptor.Encrypt(element);
                element.ReplaceWith(new XElement(Ns + "encryptedSecret",
                    new XAttribute("decryptorType", info.DecryptorType.AssemblyQualifiedName!), info.EncryptedElement));
            }
            var temp = file.FullName + ".tmp";
            document.Save(temp);
            File.Move(temp, file.FullName, overwrite: true);
            rewritten++;
        }
        return rewritten;
    }
}
