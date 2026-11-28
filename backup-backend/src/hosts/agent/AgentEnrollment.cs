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
            throw new AgentEnrollmentException(AgentText.Get(AgentMessage.ServerInvalid));
        }
        return new Uri(uri.AbsoluteUri.TrimEnd('/') + "/");
    }

    internal static void ValidateInput(string token, string? name)
    {
        if (!AgentSecretReference.TryParse(token, out _, out _))
        {
            throw new AgentEnrollmentException(AgentText.Get(AgentMessage.TokenMalformed));
        }
        if (name is not null && (string.IsNullOrWhiteSpace(name) || name.Length > 255 || name.Any(char.IsControl)))
        {
            throw new AgentEnrollmentException(AgentText.Get(AgentMessage.NameInvalid));
        }
    }

    // The caller holds the secured cross-process enrollment lease before checking the store or contacting the backend.
    internal async Task<AgentLocalIdentity> EnrollAsync(Uri server, string token, string? name, CancellationToken cancellationToken)
    {
        server = ValidateServer(server.AbsoluteUri);
        ValidateInput(token, name);
        if (await store.LoadAsync(cancellationToken).ConfigureAwait(false) is not null)
        {
            throw new AgentEnrollmentException(AgentText.Get(AgentMessage.AlreadyEnrolled));
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
            ?? throw new AgentEnrollmentException(AgentText.Get(AgentMessage.UnreadableEnrollmentResponse));
        AgentLocalIdentity identity;
        try
        {
            var credential = enrolled.Credential ?? throw new ArgumentException("The enrollment credential is missing.");
            identity = new AgentLocalIdentity(enrolled.AgentId, server, installationId,
                new AgentStoredCredential(credential.CredentialId, credential.Secret, credential.Generation));
        }
        catch (ArgumentException)
        {
            throw new AgentEnrollmentException(AgentText.Get(AgentMessage.InvalidIdentity));
        }
        AgentLocalIdentity persisted;
        try
        {
            await store.SaveAsync(identity, cancellationToken).ConfigureAwait(false);
            persisted = await store.LoadAsync(cancellationToken).ConfigureAwait(false)
                ?? throw new AgentEnrollmentException(AgentText.Get(AgentMessage.IdentityReloadFailed));
        }
        catch (AgentCredentialStoreException)
        {
            throw new AgentEnrollmentException(AgentText.Get(AgentMessage.IdentitySaveFailed));
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
            throw new AgentEnrollmentException(AgentText.Get(AgentMessage.IdentityVerifyFailed));
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
                HttpRequestError.SecureConnectionError => AgentText.Get(AgentMessage.TlsFailed),
                HttpRequestError.NameResolutionError => AgentText.Get(AgentMessage.NameResolutionFailed),
                HttpRequestError.ConnectionError => AgentText.Get(AgentMessage.ConnectionFailed),
                _ => AgentText.Get(AgentMessage.RequestFailed)
            };
            throw new AgentEnrollmentException(message + " " + AgentText.Get(AgentMessage.TokenMayBeConsumed));
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new AgentEnrollmentException(AgentText.Get(AgentMessage.RequestTimedOut));
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
        "enrollment_token_expired" => AgentText.Get(AgentMessage.TokenExpired),
        "enrollment_token_consumed" => AgentText.Get(AgentMessage.TokenConsumed),
        "invalid_enrollment_token" => AgentText.Get(AgentMessage.TokenInvalid),
        "enrollment_disabled" => AgentText.Get(AgentMessage.EnrollmentDisabled),
        "protocol_incompatible" or "protocol_missing" => AgentText.Get(AgentMessage.ProtocolIncompatible),
        _ => AgentText.Format(AgentMessage.BackendRejected, status)
    };
}
