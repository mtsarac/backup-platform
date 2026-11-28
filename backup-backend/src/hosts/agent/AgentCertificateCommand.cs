using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using BackupPlatform.Agent.Core.Identity;
using BackupPlatform.Agent.Transport;
using BackupPlatform.Contracts.Agents;

namespace BackupPlatform.Agent.Host;

/// <summary>Enrolls or rotates a client key locally; only the CSR and public certificate cross the network.</summary>
public static class AgentCertificateCommand
{
    public static async Task<int> RunAsync(string[] args, CancellationToken cancellationToken)
    {
        if (args.Length is < 2 or > 3 || args[1] is not ("enroll" or "rotate"))
        {
            Console.Error.WriteLine(AgentText.Get(AgentMessage.CertificateUsage));
            return 1;
        }
        try
        {
            var password = AgentMtlsPasswordFile.ResolveOrCreate();
            if (password.Length < 12) throw new InvalidOperationException(AgentText.Get(AgentMessage.CertificatePasswordRequired));
            var directory = AgentCredentialStoreFactory.ResolveDirectory(args.Length == 3 ? args[2] : null);
            using var lease = AgentEnrollmentLease.Acquire(directory);
            var store = AgentCredentialStoreFactory.Create(directory);
            var identity = await store.LoadAsync(cancellationToken).ConfigureAwait(false) ?? throw new InvalidOperationException(AgentText.Get(AgentMessage.CertificateEnrollFirst));
            // Bearer-only client: a certificate on the request would win over the bearer and identify the active generation, not the pending one.
            using var handler = new SocketsHttpHandler { AllowAutoRedirect = false };
            using var http = new HttpClient(handler) { BaseAddress = identity.ServerUri, Timeout = TimeSpan.FromSeconds(30) };
            if (args[1] == "rotate" && identity.Pending is null)
            {
                // A server that requires client certificates refuses a bearer-only rotation: present the current certificate when one is configured.
                var configured = Environment.GetEnvironmentVariable("AGENT_MTLS_CERTIFICATE_PATH");
                using var currentCertificate = configured is not null && File.Exists(configured) ? AgentMtlsTransport.LoadCertificate() : null;
                using var rotateHandler = new SocketsHttpHandler { AllowAutoRedirect = false };
                if (currentCertificate is not null) rotateHandler.SslOptions.ClientCertificates = [currentCertificate];
                using var rotateHttp = new HttpClient(rotateHandler) { BaseAddress = identity.ServerUri, Timeout = TimeSpan.FromSeconds(30) };
                using var rotate = new HttpRequestMessage(HttpMethod.Post, "/api/agent/credential/rotate");
                rotate.Headers.TryAddWithoutValidation("Authorization", identity.Current.ToAuthorizationHeaderValue());
                using var response = await rotateHttp.SendAsync(rotate, cancellationToken).ConfigureAwait(false);
                Ensure(response);
                var issued = await response.Content.ReadFromJsonAsync<IssuedAgentCredential>(cancellationToken: cancellationToken).ConfigureAwait(false) ?? throw new InvalidOperationException(AgentText.Get(AgentMessage.CertificateRotationInvalid));
                identity = identity.WithPending(new AgentStoredCredential(issued.CredentialId, issued.Secret, issued.Generation));
                await store.SaveAsync(identity, cancellationToken).ConfigureAwait(false);
            }
            var credential = identity.Pending ?? identity.Current;
            using var key = RSA.Create(3072);
            var request = new CertificateRequest("CN=backup-agent", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
            using var enroll = new HttpRequestMessage(HttpMethod.Post, "/api/agent/certificate/enroll") { Content = JsonContent.Create(new { signingRequestPem = request.CreateSigningRequestPem() }) };
            enroll.Headers.TryAddWithoutValidation("Authorization", credential.ToAuthorizationHeaderValue());
            using var enrolled = await http.SendAsync(enroll, cancellationToken).ConfigureAwait(false);
            Ensure(enrolled);
            using var json = await enrolled.Content.ReadFromJsonAsync<JsonDocument>(cancellationToken: cancellationToken).ConfigureAwait(false);
            using var publicCertificate = X509Certificate2.CreateFromPem(json!.RootElement.GetProperty("certificatePem").GetString()!);
            using var certificate = publicCertificate.CopyWithPrivateKey(key);
            var target = Path.Combine(directory, "client-certificate.pfx");
            await AgentCertificateRenewalService.SavePfxAtomicallyAsync(certificate, target, password, cancellationToken).ConfigureAwait(false);
            if (identity.Pending is not null)
            {
                using var savedCertificate = AgentMtlsTransport.LoadCertificate(target, password);
                using var confirmHandler = new SocketsHttpHandler { AllowAutoRedirect = false };
                confirmHandler.SslOptions.ClientCertificates = [savedCertificate];
                using var confirmClient = new HttpClient(confirmHandler) { BaseAddress = identity.ServerUri, Timeout = TimeSpan.FromSeconds(30) };
                using var confirmed = await confirmClient.PostAsync("/api/agent/credential/confirm", null, cancellationToken).ConfigureAwait(false);
                Ensure(confirmed);
                await store.SaveAsync(identity.WithPendingPromoted(), cancellationToken).ConfigureAwait(false);
            }
            Console.WriteLine(AgentText.Get(AgentMessage.CertificateSaved));
            return 0;
        }
        catch (Exception exception) when (exception is InvalidOperationException or ArgumentException or IOException or UnauthorizedAccessException or CryptographicException or HttpRequestException or JsonException or AgentCredentialStoreException or AgentEnrollmentException or OperationCanceledException)
        {
            Console.Error.WriteLine(AgentText.Get(AgentMessage.CertificateFailed));
            return 1;
        }
    }

    private static void Ensure(HttpResponseMessage response)
    {
        if (!response.IsSuccessStatusCode) throw new InvalidOperationException(AgentText.Get(AgentMessage.CertificateRejected));
    }
}
