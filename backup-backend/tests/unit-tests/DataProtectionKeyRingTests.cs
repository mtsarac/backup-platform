using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using BackupPlatform.Api.Security;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;

namespace BackupPlatform.UnitTests;

public sealed class DataProtectionKeyRingTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("dpkeys_").FullName;
    private readonly string _certs = Directory.CreateTempSubdirectory("dpcerts_").FullName;

    public void Dispose()
    {
        Directory.Delete(_dir, recursive: true);
        Directory.Delete(_certs, recursive: true);
    }

    private string NewCertificate(string name)
    {
        using var rsa = RSA.Create(2048);
        var request = new CertificateRequest("CN=" + name, rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        using var cert = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(30));
        var path = Path.Combine(_certs, name + ".pfx");
        File.WriteAllBytes(path, cert.Export(X509ContentType.Pfx, "pw"));
        return path;
    }

    private IDataProtector Provider(string? certificate, string[]? previous = null, string environment = "Development")
    {
        var values = new Dictionary<string, string?> { ["Secrets:DataProtection:KeysDirectory"] = _dir, ["Secrets:DataProtection:CertificatePassword"] = "pw" };
        if (certificate is not null) values["Secrets:DataProtection:CertificatePath"] = certificate;
        for (var i = 0; i < (previous?.Length ?? 0); i++) values[$"Secrets:DataProtection:PreviousCertificatePaths:{i}"] = previous![i];
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(values).Build();
        var services = new ServiceCollection().AddLogging();
        var builder = services.AddDataProtection().PersistKeysToFileSystem(new DirectoryInfo(_dir));
        DataProtectionKeyRing.Configure(builder, configuration, new FakeEnvironment(environment));
        return services.BuildServiceProvider().GetRequiredService<IDataProtectionProvider>().CreateProtector("test");
    }

    private string AllKeyText() => string.Concat(Directory.GetFiles(_dir, "key-*.xml").Select(File.ReadAllText));

    [Fact]
    public void Existing_plain_keys_are_rewrapped_once_and_keep_decrypting_old_data()
    {
        var cert = NewCertificate("a");
        var sealedBefore = Provider(null).Protect("secret");
        Assert.Contains("masterKey", AllKeyText());

        Assert.Equal("secret", Provider(cert).Unprotect(sealedBefore));
        var text = AllKeyText();
        Assert.DoesNotContain("masterKey", text);
        Assert.Contains("<EncryptedKey", text);
        Assert.Equal("secret", Provider(cert).Unprotect(sealedBefore));
    }

    [Fact]
    public void Rotated_certificate_reads_keys_wrapped_by_the_previous_one_and_the_wrong_one_cannot()
    {
        var a = NewCertificate("a");
        var b = NewCertificate("b");
        var sealedData = Provider(a).Protect("secret");
        Assert.Equal("secret", Provider(b, [a]).Unprotect(sealedData));
        Assert.ThrowsAny<CryptographicException>(() => Provider(b).Unprotect(sealedData));
    }

    [Fact]
    public void Production_without_certificate_refuses_to_start()
    {
        Assert.Throws<InvalidOperationException>(() => Provider(null, environment: "Production"));
    }

    private sealed class FakeEnvironment(string name) : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = name;
        public string ApplicationName { get; set; } = "t";
        public string ContentRootPath { get; set; } = "/";
        public Microsoft.Extensions.FileProviders.IFileProvider ContentRootFileProvider { get; set; } = new Microsoft.Extensions.FileProviders.NullFileProvider();
    }
}
