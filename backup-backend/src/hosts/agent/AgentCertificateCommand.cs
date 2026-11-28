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
            Console.Error.WriteLine("Usage: certificate enroll|rotate [credential-store-directory]. Stop the service first.");
            return 1;
        }
        try
        {
            var password = AgentMtlsPasswordFile.ResolveOrCreate();
            if (password.Length < 12) throw new InvalidOperationException("The PFX password must have at least 12 characters.");
            var directory = AgentCredentialStoreFactory.ResolveDirectory(args.Length == 3 ? args[2] : null);
            using var lease = AgentEnrollmentLease.Acquire(directory);
            var store = AgentCredentialStoreFactory.Create(directory);
            var identity = await store.LoadAsync(cancellationToken).ConfigureAwait(false) ?? throw new InvalidOperationException("Enroll the agent before requesting a certificate.");
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
                var issued = await response.Content.ReadFromJsonAsync<IssuedAgentCredential>(cancellationToken: cancellationToken).ConfigureAwait(false) ?? throw new InvalidOperationException("The backend returned an invalid rotation response.");
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
            var temporary = target + "." + Guid.NewGuid().ToString("N");
            try
            {
                var options = new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write, Options = FileOptions.Asynchronous | FileOptions.WriteThrough };
                if (!OperatingSystem.IsWindows()) options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
                var bytes = certificate.Export(X509ContentType.Pfx, password);
                try
                {
                    await using var output = new FileStream(temporary, options);
                    await output.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
                    await output.FlushAsync(cancellationToken).ConfigureAwait(false);
                    output.Flush(true);
                }
                finally { CryptographicOperations.ZeroMemory(bytes); }
                File.Move(temporary, target, overwrite: true);
            }
            finally { if (File.Exists(temporary)) File.Delete(temporary); }
            if (identity.Pending is not null)
            {
                using var confirmHandler = new HttpClientHandler { AllowAutoRedirect = false };
                confirmHandler.ClientCertificates.Add(certificate);
                using var confirmClient = new HttpClient(confirmHandler) { BaseAddress = identity.ServerUri, Timeout = TimeSpan.FromSeconds(30) };
                using var confirmed = await confirmClient.PostAsync("/api/agent/credential/confirm", null, cancellationToken).ConfigureAwait(false);
                Ensure(confirmed);
                await store.SaveAsync(identity.WithPendingPromoted(), cancellationToken).ConfigureAwait(false);
            }
            Console.WriteLine("Client certificate saved. Configure AGENT_MTLS_CERTIFICATE_PATH to the credential store's client-certificate.pfx and restart the service.");
            return 0;
        }
        catch (Exception exception) when (exception is InvalidOperationException or ArgumentException or IOException or UnauthorizedAccessException or CryptographicException or HttpRequestException or JsonException or AgentCredentialStoreException or AgentEnrollmentException or OperationCanceledException)
        {
            Console.Error.WriteLine("Certificate enrollment did not complete. Check issuer configuration, TLS trust, store permissions and backend availability; rerun the command before restarting the service.");
            return 1;
        }
    }

    private static void Ensure(HttpResponseMessage response)
    {
        if (!response.IsSuccessStatusCode) throw new InvalidOperationException("The backend rejected certificate enrollment.");
    }
}
