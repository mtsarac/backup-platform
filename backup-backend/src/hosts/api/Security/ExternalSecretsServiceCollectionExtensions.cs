using BackupPlatform.SecretProviders.Abstractions;
using BackupPlatform.SecretProviders.DataProtection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace BackupPlatform.Api.Security;

/// <summary>Selects external ciphertext storage while preserving the platform's name-bound secret encryption boundary.</summary>
public static class ExternalSecretsServiceCollectionExtensions
{
    public static IServiceCollection AddExternalSecrets(this IServiceCollection services, IConfiguration configuration)
    {
        var section = configuration.GetSection("Secrets");
        var provider = section["Provider"] ?? "Database";
        if (provider == "Database") return services;
        if (provider is not ("Vault" or "AwsSecretsManager")) throw new InvalidOperationException("Secrets:Provider is invalid.");
        services.AddHttpClient("external-secrets", client => { client.Timeout = TimeSpan.FromSeconds(30); client.MaxResponseContentBufferSize = 1024 * 1024; })
            .ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler { AllowAutoRedirect = false });
        services.Replace(ServiceDescriptor.Singleton<ISecretCiphertextRepository>(sp =>
        {
            var client = sp.GetRequiredService<IHttpClientFactory>().CreateClient("external-secrets");
            if (provider == "Vault")
                return new VaultCiphertextRepository(client, new Uri(section["Vault:Endpoint"] ?? throw new InvalidOperationException("Vault endpoint is required.")),
                    section["Vault:Mount"] ?? "secret", section["Vault:Prefix"] ?? "backup-platform", () => Environment.GetEnvironmentVariable("VAULT_TOKEN"));
            return new AwsSecretsCiphertextRepository(client, section["Aws:Region"] ?? throw new InvalidOperationException("AWS region is required."),
                section["Aws:Prefix"] ?? "backup-platform/", () =>
                {
                    var access = Environment.GetEnvironmentVariable("AWS_ACCESS_KEY_ID");
                    var secret = Environment.GetEnvironmentVariable("AWS_SECRET_ACCESS_KEY");
                    return access is null || secret is null ? null : new AwsSecretCredentials(access, secret, Environment.GetEnvironmentVariable("AWS_SESSION_TOKEN"));
                }, sp.GetRequiredService<TimeProvider>());
        }));
        return services;
    }
}
