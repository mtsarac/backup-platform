using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using BackupPlatform.StorageProviders.Abstractions;

namespace BackupPlatform.StorageProviders.GoogleDrive;

public sealed class GoogleDriveTokenManager
{
    private const string TokenEndpoint = "https://oauth2.googleapis.com/token";
    private const string RevokeEndpoint = "https://oauth2.googleapis.com/revoke";

    private readonly HttpClient _http;
    private readonly GoogleDriveStorageOptions _options;
    private readonly Func<CancellationToken, ValueTask<GoogleDriveCredential>> _credentialFactory;
    private readonly Func<string, CancellationToken, Task>? _onTokenRotated;
    private readonly Func<Func<CancellationToken, ValueTask<string>>, CancellationToken, ValueTask<string>>? _refreshCoordinator;
    private readonly Func<CancellationToken, Task>? _onReauthorizationRequired;
    private readonly SemaphoreSlim _refreshLock = new(1, 1);

    private string? _cachedAccessToken;
    private DateTimeOffset _accessTokenExpiresAt = DateTimeOffset.MinValue;

    public GoogleDriveTokenManager(
        HttpClient http,
        GoogleDriveStorageOptions options,
        Func<CancellationToken, ValueTask<GoogleDriveCredential>> credentialFactory,
        Func<string, CancellationToken, Task>? onTokenRotated = null,
        Func<Func<CancellationToken, ValueTask<string>>, CancellationToken, ValueTask<string>>? refreshCoordinator = null,
        Func<CancellationToken, Task>? onReauthorizationRequired = null)
    {
        _http = http ?? throw new ArgumentNullException(nameof(http));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _credentialFactory = credentialFactory ?? throw new ArgumentNullException(nameof(credentialFactory));
        _onTokenRotated = onTokenRotated;
        _refreshCoordinator = refreshCoordinator;
        _onReauthorizationRequired = onReauthorizationRequired;
    }

    public async ValueTask<string> GetAccessTokenAsync(CancellationToken cancellationToken)
    {
        if (_cachedAccessToken is not null && DateTimeOffset.UtcNow < _accessTokenExpiresAt)
        {
            return _cachedAccessToken;
        }

        await _refreshLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_cachedAccessToken is not null && DateTimeOffset.UtcNow < _accessTokenExpiresAt)
            {
                return _cachedAccessToken;
            }

            var accessToken = _refreshCoordinator is null
                ? await RefreshAsync(cancellationToken).ConfigureAwait(false)
                : await _refreshCoordinator(RefreshAsync, cancellationToken).ConfigureAwait(false);
            _cachedAccessToken = accessToken;
            return accessToken;
        }
        finally
        {
            _refreshLock.Release();
        }
    }

    private async ValueTask<string> RefreshAsync(CancellationToken cancellationToken)
    {
        var credential = await _credentialFactory(cancellationToken).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(credential.RefreshToken))
        {
            if (_onReauthorizationRequired is not null)
                await _onReauthorizationRequired(CancellationToken.None).ConfigureAwait(false);
            throw new StorageException(StorageFailureKind.AuthenticationFailed, "Google Drive refresh token is missing. Reauthorization required.");
        }

        var (accessToken, newRefreshToken, expiresIn) = await RefreshTokenWithRetryAsync(credential.RefreshToken, cancellationToken).ConfigureAwait(false);
        if (!string.IsNullOrWhiteSpace(newRefreshToken) && !string.Equals(newRefreshToken, credential.RefreshToken, StringComparison.Ordinal))
        {
            if (_onTokenRotated is null)
                throw new StorageException(StorageFailureKind.AuthenticationFailed, "Google Drive refresh token rotation could not be persisted.");
            // Rotation has already happened at Google; caller cancellation must not discard its replacement.
            await _onTokenRotated(newRefreshToken, CancellationToken.None).ConfigureAwait(false);
        }

        _accessTokenExpiresAt = DateTimeOffset.UtcNow.AddSeconds(Math.Max(0, expiresIn - 60));
        return accessToken;
    }

    public async Task RevokeTokenAsync(string token, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(token);
        try
        {
            using var content = new FormUrlEncodedContent(new Dictionary<string, string> { ["token"] = token });
            using var response = await _http.PostAsync(RevokeEndpoint, content, cancellationToken).ConfigureAwait(false);
            // Revocation endpoint returns 200 on success
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            // Revocation is best-effort
        }
    }

    private async Task<(string AccessToken, string? NewRefreshToken, int ExpiresIn)> RefreshTokenWithRetryAsync(
        string refreshToken, CancellationToken cancellationToken)
    {
        var attempts = 0;
        const int maxAttempts = 3;

        while (true)
        {
            attempts++;
            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Post, TokenEndpoint);
                request.Content = new FormUrlEncodedContent(new Dictionary<string, string>
                {
                    ["client_id"] = _options.ClientId,
                    ["client_secret"] = _options.ClientSecret,
                    ["refresh_token"] = refreshToken,
                    ["grant_type"] = "refresh_token"
                });

                using var response = await _http.SendAsync(request, cancellationToken).ConfigureAwait(false);
                if (response.IsSuccessStatusCode)
                {
                    var payload = await response.Content.ReadFromJsonAsync<GoogleTokenResponse>(cancellationToken: cancellationToken).ConfigureAwait(false);
                    if (payload is null || string.IsNullOrWhiteSpace(payload.AccessToken))
                    {
                        throw new StorageException(StorageFailureKind.AuthenticationFailed, "Invalid token response from Google.");
                    }
                    return (payload.AccessToken, payload.RefreshToken, payload.ExpiresIn > 0 ? payload.ExpiresIn : 3600);
                }

                if (response.StatusCode == HttpStatusCode.BadRequest)
                {
                    string? error;
                    try
                    {
                        var payload = await response.Content.ReadFromJsonAsync<GoogleTokenError>(cancellationToken: cancellationToken).ConfigureAwait(false);
                        error = payload?.Error;
                    }
                    catch (JsonException)
                    {
                        throw new StorageException(StorageFailureKind.AuthenticationFailed, "Google Drive token request failed.");
                    }
                    if (string.Equals(error, "invalid_grant", StringComparison.Ordinal))
                    {
                        if (_onReauthorizationRequired is not null)
                            await _onReauthorizationRequired(CancellationToken.None).ConfigureAwait(false);
                        throw new StorageException(StorageFailureKind.AuthenticationFailed, "Google Drive authorization expired or was revoked. Reauthorization required.");
                    }
                    throw new StorageException(StorageFailureKind.AuthenticationFailed, "Google Drive token request failed.");
                }

                if (response.StatusCode == HttpStatusCode.Unauthorized || response.StatusCode == HttpStatusCode.Forbidden)
                {
                    throw new StorageException(StorageFailureKind.AccessDenied, "Google Drive token request was denied.");
                }

                if ((int)response.StatusCode >= 500 || response.StatusCode == (HttpStatusCode)429)
                {
                    if (attempts < maxAttempts)
                    {
                        await Task.Delay(TimeSpan.FromMilliseconds(200 * attempts), cancellationToken).ConfigureAwait(false);
                        continue;
                    }
                    throw new StorageException(StorageFailureKind.NetworkUnavailable, "Google Drive token service temporarily unavailable.");
                }

                throw new StorageException(StorageFailureKind.Io, $"Google Drive token request failed with status {response.StatusCode}.");
            }
            catch (HttpRequestException) when (attempts < maxAttempts)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(200 * attempts), cancellationToken).ConfigureAwait(false);
            }
            catch (HttpRequestException ex)
            {
                throw new StorageException(StorageFailureKind.NetworkUnavailable, "Failed to connect to Google token endpoint.", ex);
            }
        }
    }

    private sealed class GoogleTokenError
    {
        [JsonPropertyName("error")]
        public string? Error { get; set; }
    }

    private sealed class GoogleTokenResponse
    {
        [JsonPropertyName("access_token")]
        public string? AccessToken { get; set; }

        [JsonPropertyName("refresh_token")]
        public string? RefreshToken { get; set; }

        [JsonPropertyName("expires_in")]
        public int ExpiresIn { get; set; }

        [JsonPropertyName("token_type")]
        public string? TokenType { get; set; }
    }
}
