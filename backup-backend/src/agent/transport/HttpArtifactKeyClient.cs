using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using BackupPlatform.Agent.Core.Connectivity;
using BackupPlatform.Agent.Core.Identity;
using BackupPlatform.Agent.Core.Restore;
using BackupPlatform.Contracts.Agents;
using BackupPlatform.Contracts.Backups;
using BackupPlatform.Contracts.Restores;

namespace BackupPlatform.Agent.Transport;

/// <summary>
/// Fetches the DEK of a restore attempt's artifact. Authenticated and fenced like the storage relay (lease and restore-operation headers); the request has no body
/// and every failure surfaces as <see cref="ArtifactKeyUnavailableException"/> with a fixed message, so neither a response body nor a key can leak into an exception or log.
/// </summary>
public sealed class HttpArtifactKeyClient(HttpClient http, AgentBackendEndpoint endpoint, IAgentCredentialStore identities) : IArtifactKeyClient
{
    private const int DekLength = 32;

    public async Task<byte[]> UnwrapAsync(Guid attemptId, Guid leaseToken, Guid restoreOperationId, CancellationToken cancellationToken)
    {
        if (attemptId == Guid.Empty || leaseToken == Guid.Empty || restoreOperationId == Guid.Empty)
        {
            throw new ArgumentException("An attempt, lease and restore operation are required.");
        }

        var identity = await identities.LoadAsync(cancellationToken).ConfigureAwait(false)
            ?? throw new ArtifactKeyUnavailableException("The agent identity is unavailable.");
        using var request = new HttpRequestMessage(HttpMethod.Post, new Uri(endpoint.BaseUri, $"api/agent/restore-tasks/{attemptId:D}/artifact-key"));
        request.Headers.TryAddWithoutValidation("Authorization", identity.Current.ToAuthorizationHeaderValue());
        request.Headers.TryAddWithoutValidation(AgentProtocolHeader.Name, AgentProtocol.Current.Version.ToString());
        request.Headers.TryAddWithoutValidation(TaskStorageProtocol.LeaseHeader, leaseToken.ToString("D"));
        request.Headers.TryAddWithoutValidation(TaskStorageProtocol.RestoreHeader, restoreOperationId.ToString("D"));
        try
        {
            using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                throw new ArtifactKeyUnavailableException("The backend did not release the artifact key.");
            }

            var body = await response.Content.ReadFromJsonAsync<ArtifactKeyUnwrapResponse>(JsonSerializerOptions.Web, cancellationToken).ConfigureAwait(false);
            var dek = body is null ? null : Decode(body.DekBase64);
            return dek ?? throw new ArtifactKeyUnavailableException("The backend returned an unusable artifact key.");
        }
        catch (Exception exception) when (exception is HttpRequestException or IOException or JsonException or NotSupportedException)
        {
            throw new ArtifactKeyUnavailableException("The artifact key could not be requested from the backend.");
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new ArtifactKeyUnavailableException("The artifact key request timed out.");
        }
    }

    private static byte[]? Decode(string value)
    {
        Span<byte> buffer = stackalloc byte[DekLength + 8];
        if (!Convert.TryFromBase64String(value, buffer, out var written) || written != DekLength)
        {
            CryptographicOperations.ZeroMemory(buffer);
            return null;
        }

        var dek = buffer[..written].ToArray();
        CryptographicOperations.ZeroMemory(buffer);
        return dek;
    }
}
