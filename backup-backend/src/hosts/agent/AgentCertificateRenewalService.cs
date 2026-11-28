using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using BackupPlatform.Agent.Core.Connectivity;
using BackupPlatform.Agent.Transport;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace BackupPlatform.Agent.Host;

/// <summary>
/// Renews the agent client certificate in-process at two-thirds of its validity window under the host's held enrollment lock,
/// persists the new PFX atomically, and updates <see cref="AgentClientCertificateProvider"/> so HTTP and push connections switch without a restart.
/// </summary>
internal sealed class AgentCertificateRenewalService(
    AgentClientCertificateProvider certificates,
    AgentBackendEndpoint endpoint,
    HttpClient http,
    AgentBackoff backoff,
    TimeProvider timeProvider,
    ILogger<AgentCertificateRenewalService> logger,
    int rsaKeySizeBits = 3072) : BackgroundService
{
    private static readonly TimeSpan MaxTimerSlice = TimeSpan.FromDays(24);
    private static readonly TimeSpan MinimumPostRenewalDelay = TimeSpan.FromSeconds(5);

    internal static DateTimeOffset GetRenewalTime(DateTimeOffset notBefore, DateTimeOffset notAfter)
    {
        var lifetime = notAfter - notBefore;
        return lifetime <= TimeSpan.Zero ? notBefore : notAfter - TimeSpan.FromTicks(lifetime.Ticks / 3);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!certificates.IsConfigured || string.IsNullOrEmpty(certificates.CertificatePath))
        {
            return;
        }
        var renewedOnce = false;
        while (!stoppingToken.IsCancellationRequested)
        {
            if (certificates.CurrentValidityWindow is not var (notBefore, notAfter, _))
            {
                return;
            }
            var renewAt = GetRenewalTime(notBefore, notAfter);
            await WaitUntilAsync(renewAt, renewedOnce, stoppingToken).ConfigureAwait(false);
            await RenewWithBackoffAsync(stoppingToken).ConfigureAwait(false);
            renewedOnce = true;
        }
    }

    private async Task WaitUntilAsync(DateTimeOffset targetUtc, bool enforceMinimumFloor, CancellationToken stoppingToken)
    {
        var now = timeProvider.GetUtcNow();
        if (enforceMinimumFloor && targetUtc - now < MinimumPostRenewalDelay)
        {
            targetUtc = now + MinimumPostRenewalDelay;
        }
        while (!stoppingToken.IsCancellationRequested)
        {
            var remaining = targetUtc - timeProvider.GetUtcNow();
            if (remaining <= TimeSpan.Zero)
            {
                return;
            }
            var slice = remaining > MaxTimerSlice ? MaxTimerSlice : remaining;
            await Task.Delay(slice, timeProvider, stoppingToken).ConfigureAwait(false);
        }
    }

    private async Task RenewWithBackoffAsync(CancellationToken stoppingToken)
    {
        var attempt = 0;
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await RenewOnceAsync(stoppingToken).ConfigureAwait(false);
                return;
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception)
            {
                var delay = backoff.GetDelay(attempt++);
                if (delay <= TimeSpan.Zero)
                {
                    delay = TimeSpan.FromTicks(Math.Max(TimeSpan.TicksPerMillisecond * 100, backoff.BaseDelay.Ticks / 2));
                }
                logger.LogWarning("Agent client certificate renewal attempt {Attempt} failed ({FailureType}); retrying in {DelaySeconds:F1}s.",
                    attempt, exception.GetType().Name, delay.TotalSeconds);
                await Task.Delay(delay, timeProvider, stoppingToken).ConfigureAwait(false);
            }
        }
    }

    internal async Task RenewOnceAsync(CancellationToken cancellationToken)
    {
        var certificatePath = certificates.CertificatePath
            ?? throw new InvalidOperationException("No client certificate path is configured.");
        var password = AgentMtlsTransport.ResolvePassword();
        if (string.IsNullOrEmpty(password) || password.Length < 12)
        {
            throw new InvalidOperationException("A valid PFX password is required for certificate renewal.");
        }
        using var key = RSA.Create(rsaKeySizeBits);
        var csr = new CertificateRequest("CN=backup-agent", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        using var request = new HttpRequestMessage(HttpMethod.Post, new Uri(endpoint.BaseUri, "api/agent/certificate/enroll"))
        {
            Content = JsonContent.Create(new { signingRequestPem = csr.CreateSigningRequestPem() })
        };
        using var response = await http.SendAsync(request, cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            throw new HttpRequestException($"Certificate renewal returned HTTP {(int)response.StatusCode}.", null, response.StatusCode);
        }
        using var json = await response.Content.ReadFromJsonAsync<JsonDocument>(cancellationToken: cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException("The backend returned an empty certificate response.");
        var pem = json.RootElement.GetProperty("certificatePem").GetString();
        if (string.IsNullOrWhiteSpace(pem))
        {
            throw new InvalidOperationException("The backend returned an empty certificate PEM.");
        }
        using var publicCertificate = X509Certificate2.CreateFromPem(pem);
        using var combined = publicCertificate.CopyWithPrivateKey(key);
        await SavePfxAtomicallyAsync(combined, certificatePath, password, cancellationToken).ConfigureAwait(false);
        var reloaded = AgentMtlsTransport.LoadCertificate(certificatePath, password);
        certificates.Update(reloaded);
        logger.LogInformation("Agent client certificate renewed (Serial={SerialNumber}, ExpiresUtc={ExpiresUtc:O}).",
            reloaded.SerialNumber, reloaded.NotAfter.ToUniversalTime());
    }

    internal static async Task SavePfxAtomicallyAsync(X509Certificate2 certificate, string targetPath, string password, CancellationToken cancellationToken)
    {
        var temporary = targetPath + "." + Guid.NewGuid().ToString("N");
        try
        {
            var options = new FileStreamOptions
            {
                Mode = FileMode.CreateNew,
                Access = FileAccess.Write,
                Share = FileShare.None,
                Options = FileOptions.Asynchronous | FileOptions.WriteThrough
            };
            if (!OperatingSystem.IsWindows())
            {
                options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
            }
            var bytes = certificate.Export(X509ContentType.Pfx, password);
            try
            {
                await using var output = new FileStream(temporary, options);
                await output.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
                await output.FlushAsync(cancellationToken).ConfigureAwait(false);
                output.Flush(true);
            }
            finally
            {
                CryptographicOperations.ZeroMemory(bytes);
            }
            File.Move(temporary, targetPath, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporary))
            {
                File.Delete(temporary);
            }
        }
    }
}
