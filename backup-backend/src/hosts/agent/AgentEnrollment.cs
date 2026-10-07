using System.Net.Http.Json;
using System.Text.Json;
using BackupPlatform.Agent.Core.Connectivity;
using BackupPlatform.Agent.Core.Identity;
using BackupPlatform.Contracts.Agents;

namespace BackupPlatform.Agent.Host;

internal sealed class AgentEnrollmentException(string message) : Exception(message);

internal sealed class AgentEnrollment(HttpClient http, IAgentCredentialStore store)
{
    internal static Uri ValidateServer(string? server)
    {
        if (!Uri.TryCreate(server, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps
            || string.IsNullOrEmpty(uri.Host) || uri.UserInfo.Length != 0 || uri.Query.Length != 0 || uri.Fragment.Length != 0)
        {
            throw new AgentEnrollmentException("The server must be an absolute HTTPS address without user information, query or fragment.");
        }
        return new Uri(uri.AbsoluteUri.TrimEnd('/') + "/");
    }

    internal static void ValidateInput(string token, string? name)
    {
        if (!AgentSecretReference.TryParse(token, out _, out _))
        {
            throw new AgentEnrollmentException("The enrollment token is malformed. Copy the complete token from the Add agent wizard.");
        }
        if (name is not null && (string.IsNullOrWhiteSpace(name) || name.Length > 255 || name.Any(char.IsControl)))
        {
            throw new AgentEnrollmentException("The agent name must contain 1–255 characters and no control characters.");
        }
    }

    // The caller holds the secured cross-process enrollment lease before checking the store or contacting the backend.
    internal async Task<AgentLocalIdentity> EnrollAsync(Uri server, string token, string? name, CancellationToken cancellationToken)
    {
        server = ValidateServer(server.AbsoluteUri);
        ValidateInput(token, name);
        if (await store.LoadAsync(cancellationToken).ConfigureAwait(false) is not null)
        {
            throw new AgentEnrollmentException("This store is already enrolled. The existing identity was preserved and the token was not used.");
        }
        var software = AgentSoftwareInfo.Detect(typeof(AgentEnrollment).Assembly);
        var installationId = Guid.NewGuid();
        using var request = new HttpRequestMessage(HttpMethod.Post, new Uri(server, "api/agent/enroll"))
        {
            Content = JsonContent.Create(new AgentEnrollmentRequest
            {
                EnrollmentToken = token,
                InstallationId = installationId,
                HostName = name ?? Environment.MachineName,
                AgentVersion = software.Version,
                ProtocolVersion = AgentProtocol.Current.Version.ToString(),
                Platform = software.Platform
            }, options: JsonSerializerOptions.Web)
        };
        request.Headers.TryAddWithoutValidation(AgentProtocolHeader.Name, AgentProtocol.Current.Version.ToString());
        using var response = await SendAsync(request, cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            var error = await ReadAsync<AgentApiError>(response, cancellationToken).ConfigureAwait(false);
            throw new AgentEnrollmentException(MapError(error?.Error, (int)response.StatusCode));
        }
        var enrolled = await ReadAsync<AgentEnrollmentResponse>(response, cancellationToken).ConfigureAwait(false)
            ?? throw new AgentEnrollmentException("The backend returned an unreadable enrollment response. The token may have been consumed; check the wizard before trying another token.");
        AgentLocalIdentity identity;
        try
        {
            var credential = enrolled.Credential ?? throw new ArgumentException("The enrollment credential is missing.");
            identity = new AgentLocalIdentity(enrolled.AgentId, server, installationId,
                new AgentStoredCredential(credential.CredentialId, credential.Secret, credential.Generation));
        }
        catch (ArgumentException)
        {
            throw new AgentEnrollmentException("The backend returned an invalid identity. Nothing was saved; the token may have been consumed.");
        }
        AgentLocalIdentity persisted;
        try
        {
            await store.SaveAsync(identity, cancellationToken).ConfigureAwait(false);
            persisted = await store.LoadAsync(cancellationToken).ConfigureAwait(false)
                ?? throw new AgentEnrollmentException("The saved identity could not be reloaded. The token has been consumed; repair the secure store before retrying.");
        }
        catch (AgentCredentialStoreException)
        {
            throw new AgentEnrollmentException("The backend accepted enrollment, but the secure identity could not be saved or reloaded. The token was consumed. Check store ownership, permissions and disk space; any stored identity was preserved.");
        }
        await VerifyAsync(persisted, cancellationToken).ConfigureAwait(false);
        return persisted;
    }

    internal async Task VerifyAsync(AgentLocalIdentity identity, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, new Uri(identity.ServerUri, "api/agent/me"));
        request.Headers.TryAddWithoutValidation("Authorization", identity.Current.ToAuthorizationHeaderValue());
        request.Headers.TryAddWithoutValidation(AgentProtocolHeader.Name, AgentProtocol.Current.Version.ToString());
        using var response = await SendAsync(request, cancellationToken).ConfigureAwait(false);
        var verified = response.IsSuccessStatusCode
            ? await ReadAsync<AgentIdentityResponse>(response, cancellationToken).ConfigureAwait(false) : null;
        if (verified is null || verified.AgentId != identity.AgentId || verified.CredentialId != identity.Current.CredentialId
            || verified.CredentialGeneration != identity.Current.Generation || verified.TenantId == Guid.Empty)
        {
            throw new AgentEnrollmentException("The saved identity could not be verified by the backend. It was preserved; check the backend and secure store, then restart the agent. Do not enroll again.");
        }
    }

    private async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        try
        {
            return await http.SendAsync(request, cancellationToken).ConfigureAwait(false);
        }
        catch (HttpRequestException exception)
        {
            var message = exception.HttpRequestError switch
            {
                HttpRequestError.SecureConnectionError => "The backend TLS connection failed. Check that its HTTPS certificate is trusted, valid for this server name and not expired.",
                HttpRequestError.NameResolutionError => "The backend server name could not be resolved. Check the HTTPS address and DNS configuration.",
                HttpRequestError.ConnectionError => "The HTTPS backend could not be reached. Check the server address, network, firewall and backend availability.",
                _ => "The HTTPS backend request failed. Check backend availability and the network connection."
            };
            throw new AgentEnrollmentException(message + " A submitted token may have been consumed; check the wizard before retrying.");
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new AgentEnrollmentException("The backend request timed out. A submitted token may have been consumed; check the wizard before retrying.");
        }
    }

    private static async Task<T?> ReadAsync<T>(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        try
        {
            return await response.Content.ReadFromJsonAsync<T>(JsonSerializerOptions.Web, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is JsonException or NotSupportedException or InvalidOperationException or IOException)
        {
            return default;
        }
    }

    private static string MapError(string? error, int status) => error switch
    {
        "enrollment_token_expired" => "The enrollment token expired. Generate a new token in the Add agent wizard.",
        "enrollment_token_consumed" => "The enrollment token was already used. Check the agent in the wizard or generate a new token for another machine.",
        "invalid_enrollment_token" => "The enrollment token is invalid or revoked. Generate a new token in the Add agent wizard.",
        "enrollment_disabled" => "Enrollment is disabled for this tenant. Ask your administrator to enable it.",
        "protocol_incompatible" or "protocol_missing" => "The backend and agent protocol versions are incompatible. Install a compatible agent release.",
        _ => $"The backend rejected enrollment (HTTP {status}). Check tenant enrollment permissions and use a fresh token."
    };
}
