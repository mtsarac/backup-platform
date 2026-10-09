using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Serialization;
using BackupPlatform.Domain.Audit;
using BackupPlatform.Domain.Identity;
using BackupPlatform.Domain.Storage;
using BackupPlatform.Domain.Tenancy;
using BackupPlatform.Infrastructure.Persistence;
using BackupPlatform.SecretProviders.Abstractions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace BackupPlatform.Api.Backups;

public sealed class GoogleDriveOAuthService(
    HttpClient http,
    IOptions<GoogleDrivePlatformOptions> platformOptions,
    DbContextOptions<PlatformDbContext> dbOptions,
    ISecretStore secretStore,
    TimeProvider time,
    ILogger<GoogleDriveOAuthService> logger)
{
    private const string GoogleAuthBaseUrl = "https://accounts.google.com/o/oauth2/v2/auth";
    private const string TokenEndpoint = "https://oauth2.googleapis.com/token";
    private const string RevokeEndpoint = "https://oauth2.googleapis.com/revoke";
    private const string UserInfoEndpoint = "https://www.googleapis.com/drive/v3/about?fields=user";
    public const string DefaultScope = "https://www.googleapis.com/auth/drive.file";

    private readonly GoogleDrivePlatformOptions _platform = platformOptions.Value;

    public async Task<string> CreateAuthorizationUrlAsync(
        TenantId tenantId, Guid connectionId, Guid userId, CancellationToken cancellationToken)
    {
        await using var db = PlatformDbContext.CreateForPlatformOperations(dbOptions);
        var connection = await db.StorageConnections
            .SingleOrDefaultAsync(c => c.TenantId == tenantId && c.Id == connectionId, cancellationToken);

        if (connection is null || connection.ProviderType != StorageProviderTypes.GoogleDrive)
        {
            throw new InvalidOperationException("Google Drive storage connection not found.");
        }

        if (connection.AdministrativelyDisabled)
        {
            throw new InvalidOperationException("This connection is administratively disabled.");
        }

        var redirectUri = ConfiguredRedirectUri();

        var codeVerifier = GenerateRandomString(64);
        var codeChallenge = Base64UrlEncode(SHA256.HashData(Encoding.ASCII.GetBytes(codeVerifier)));
        var state = GenerateRandomString(32);

        var now = time.GetUtcNow();
        var expiresAt = now.AddMinutes(10);

        db.GoogleDriveOAuthStates.Add(new GoogleDriveOAuthState(
            state, tenantId, connectionId, userId, codeVerifier, redirectUri, now, expiresAt));

        await db.SaveChangesAsync(cancellationToken);

        var urlBuilder = new StringBuilder(GoogleAuthBaseUrl);
        urlBuilder.Append($"?client_id={Uri.EscapeDataString(_platform.ClientId)}");
        urlBuilder.Append($"&redirect_uri={Uri.EscapeDataString(redirectUri)}");
        urlBuilder.Append("&response_type=code");
        urlBuilder.Append($"&scope={Uri.EscapeDataString(DefaultScope)}");
        urlBuilder.Append("&access_type=offline");
        urlBuilder.Append("&prompt=consent");
        urlBuilder.Append($"&state={Uri.EscapeDataString(state)}");
        urlBuilder.Append($"&code_challenge={Uri.EscapeDataString(codeChallenge)}");
        urlBuilder.Append("&code_challenge_method=S256");

        return urlBuilder.ToString();
    }

    public async Task<GoogleDriveOAuthCallbackResult> ProcessCallbackAsync(
        string code, string state, string? error, CancellationToken cancellationToken)
    {
        if (!string.IsNullOrEmpty(error))
            return new(false, null, null, null, "Google authorization was denied or failed.");
        if (string.IsNullOrWhiteSpace(code) || string.IsNullOrWhiteSpace(state))
            return new(false, null, null, null, "Missing authorization code or state.");

        await using var db = PlatformDbContext.CreateForPlatformOperations(dbOptions);
        var now = time.GetUtcNow();
        var claimed = await db.GoogleDriveOAuthStates
            .Where(s => s.State == state && !s.Used && s.ExpiresAt > now)
            .ExecuteUpdateAsync(setters => setters.SetProperty(s => s.Used, true), cancellationToken);
        if (claimed != 1)
            return new(false, null, null, null, "Invalid, expired, or previously used authorization state.");
        var oauthState = await db.GoogleDriveOAuthStates.SingleAsync(s => s.State == state, cancellationToken);
        StorageConnection? connection;
        using (var readTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
        {
            readTimeout.CancelAfter(_platform.RequestTimeout);
            await using var readTransaction = await db.Database.BeginTransactionAsync(readTimeout.Token);
            await LockOwnerAsync(db, oauthState.TenantId, readTimeout.Token);
            connection = await db.StorageConnections.SingleOrDefaultAsync(
                c => c.TenantId == oauthState.TenantId && c.Id == oauthState.ConnectionId, readTimeout.Token);
            if (connection is null || connection.ProviderType != StorageProviderTypes.GoogleDrive || connection.AdministrativelyDisabled)
                return new(false, null, null, null, "Associated storage connection not found.");
            await readTransaction.CommitAsync(readTimeout.Token);
        }
        var expectedReference = connection.SecretReference;
        var expectedSecretTime = connection.SecretUpdatedAt;
        var expectedRevocation = connection.Configuration.GetValueOrDefault("reauthorizationRequired");
        var expectedStateTime = connection.StateChangedAt;

        GoogleTokenResponse? tokenData;
        GoogleAboutResponse? userInfo;
        try
        {
            (tokenData, userInfo) = await ExchangeCodeAsync(code, oauthState, cancellationToken);
        }
        catch (Exception exception) when (exception is HttpRequestException or System.Text.Json.JsonException
            || exception is OperationCanceledException && !cancellationToken.IsCancellationRequested)
        {
            return new(false, oauthState.TenantId.Value, connection.Id, null, "Google OAuth token exchange failed.");
        }
        if (tokenData is null || string.IsNullOrWhiteSpace(tokenData.AccessToken))
            return new(false, oauthState.TenantId.Value, connection.Id, null, "Empty token response received from Google.");
        if (string.IsNullOrWhiteSpace(tokenData.RefreshToken))
            return new(false, oauthState.TenantId.Value, connection.Id, null, "No refresh token provided by Google.");

        var accountEmail = userInfo?.User?.EmailAddress ?? "";
        var accountName = userInfo?.User?.DisplayName ?? "";
        var reference = $"sc-{connection.Id:N}-{Guid.NewGuid():N}";
        using var persistTimeout = new CancellationTokenSource(_platform.RequestTimeout);
        try
        {
            var payload = System.Text.Json.JsonSerializer.Serialize(new Dictionary<string, string> { ["refreshToken"] = tokenData.RefreshToken });
            await secretStore.SetAsync(new SecretReference(reference), new SecretValue(payload), persistTimeout.Token);
            db.ChangeTracker.Clear();
            await using var transaction = await db.Database.BeginTransactionAsync(persistTimeout.Token);
            await LockOwnerAsync(db, oauthState.TenantId, persistTimeout.Token);
            connection = await db.StorageConnections.SingleOrDefaultAsync(
                c => c.TenantId == oauthState.TenantId && c.Id == oauthState.ConnectionId, persistTimeout.Token);
            if (connection is null || connection.ProviderType != StorageProviderTypes.GoogleDrive || connection.AdministrativelyDisabled
                || connection.SecretReference != expectedReference || connection.SecretUpdatedAt != expectedSecretTime
                || connection.StateChangedAt != expectedStateTime
                || connection.Configuration.GetValueOrDefault("reauthorizationRequired") != expectedRevocation)
            {
                await transaction.RollbackAsync(persistTimeout.Token);
                await DiscardSecretAsync(dbOptions, secretStore, oauthState.TenantId, reference, _platform.RequestTimeout);
                return new(false, oauthState.TenantId.Value, oauthState.ConnectionId, null, "The storage connection changed during authorization. Start authorization again.");
            }
            now = time.GetUtcNow();
            var oldSecret = connection.AttachSecret(reference, ["refreshToken"], now);
            var config = new Dictionary<string, string>(connection.Configuration, StringComparer.Ordinal)
            {
                ["reauthorizationRequired"] = "false"
            };
            if (!string.IsNullOrWhiteSpace(accountEmail)) config["accountEmail"] = accountEmail;
            if (!string.IsNullOrWhiteSpace(accountName)) config["accountName"] = accountName;
            connection.Reconfigure(config, now);
            db.AuditEvents.Add(AuditEvent.Record(
                now, new UserId(oauthState.UserId), AuditActorKind.TenantAdministrator,
                "storage_connection.oauth_connected", oauthState.TenantId,
                BackupPlatform.Storage.StorageConnectionAuditActions.TargetType, connection.Id.ToString("D"),
                new Dictionary<string, object?> { ["accountEmail"] = accountEmail, ["accountName"] = accountName }));
            await db.SaveChangesAsync(persistTimeout.Token);
            await transaction.CommitAsync(persistTimeout.Token);
            if (oldSecret is not null)
                await DiscardSecretAsync(dbOptions, secretStore, oauthState.TenantId, oldSecret, _platform.RequestTimeout);
            return new(true, oauthState.TenantId.Value, connection.Id, accountEmail, null);
        }
        catch
        {
            await DiscardSecretAsync(dbOptions, secretStore, oauthState.TenantId, reference, _platform.RequestTimeout);
            throw;
        }
    }

    public async Task DisconnectAsync(TenantId tenantId, Guid connectionId, Guid userId, CancellationToken cancellationToken)
    {
        string? refreshToken = null;
        string? oldReference;
        using (var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
        {
            timeout.CancelAfter(_platform.RequestTimeout);
            await using var db = PlatformDbContext.CreateForPlatformOperations(dbOptions);
            await using var transaction = await db.Database.BeginTransactionAsync(timeout.Token);
            await LockOwnerAsync(db, tenantId, timeout.Token);
            var connection = await db.StorageConnections.SingleOrDefaultAsync(
                c => c.TenantId == tenantId && c.Id == connectionId, timeout.Token);
            if (connection is null || connection.ProviderType != StorageProviderTypes.GoogleDrive)
                throw new InvalidOperationException("Google Drive storage connection not found.");
            if (connection.SecretReference is { } reference)
            {
                try
                {
                    var secret = await secretStore.ResolveAsync(new SecretReference(reference), timeout.Token);
                    refreshToken = System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, string>>(secret.Reveal())?.GetValueOrDefault("refreshToken");
                }
                catch (OperationCanceledException) { throw; }
                catch { /* Unavailable credentials must not prevent a local disconnect. */ }
            }
            var now = time.GetUtcNow();
            oldReference = connection.DetachSecret(now);
            var config = new Dictionary<string, string>(connection.Configuration, StringComparer.Ordinal)
            {
                ["reauthorizationRequired"] = "true"
            };
            config.Remove("accountEmail");
            config.Remove("accountName");
            connection.Reconfigure(config, now);
            db.AuditEvents.Add(AuditEvent.Record(
                now, new UserId(userId), AuditActorKind.TenantAdministrator,
                "storage_connection.oauth_disconnected", tenantId,
                BackupPlatform.Storage.StorageConnectionAuditActions.TargetType, connection.Id.ToString("D"),
                new Dictionary<string, object?>()));
            await db.SaveChangesAsync(timeout.Token);
            using var commitTimeout = new CancellationTokenSource(_platform.RequestTimeout);
            await transaction.CommitAsync(commitTimeout.Token);
        }
        try
        {
            if (!string.IsNullOrWhiteSpace(refreshToken))
                await RevokeBestEffortAsync(refreshToken, cancellationToken);
        }
        finally
        {
            if (oldReference is not null)
                await DiscardSecretAsync(dbOptions, secretStore, tenantId, oldReference, _platform.RequestTimeout);
        }
    }

    private async Task<(GoogleTokenResponse? Token, GoogleAboutResponse? User)> ExchangeCodeAsync(
        string code, GoogleDriveOAuthState state, CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(_platform.RequestTimeout);
        // Authorization codes are single-use; a lost response is not safely retryable.
        using var request = new HttpRequestMessage(HttpMethod.Post, TokenEndpoint)
        {
            Content = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["client_id"] = _platform.ClientId,
                ["client_secret"] = _platform.ClientSecret,
                ["code"] = code,
                ["code_verifier"] = state.CodeVerifier,
                ["grant_type"] = "authorization_code",
                ["redirect_uri"] = state.RedirectUri
            })
        };
        using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
        if (!response.IsSuccessStatusCode)
        {
            logger.LogWarning("Google Drive token exchange failed with HTTP status {StatusCode}.", (int)response.StatusCode);
            return (null, null);
        }
        var token = await response.Content.ReadFromJsonAsync<GoogleTokenResponse>(cancellationToken: timeout.Token);
        if (string.IsNullOrWhiteSpace(token?.AccessToken) || string.IsNullOrWhiteSpace(token.RefreshToken))
            return (token, null);
        using var userRequest = new HttpRequestMessage(HttpMethod.Get, UserInfoEndpoint);
        userRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token.AccessToken);
        using var userResponse = await http.SendAsync(userRequest, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
        var user = userResponse.IsSuccessStatusCode
            ? await userResponse.Content.ReadFromJsonAsync<GoogleAboutResponse>(cancellationToken: timeout.Token) : null;
        return (token, user);
    }

    private async Task RevokeBestEffortAsync(string refreshToken, CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(_platform.RequestTimeout);
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, RevokeEndpoint)
            {
                Content = new FormUrlEncodedContent(new Dictionary<string, string> { ["token"] = refreshToken })
            };
            using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception exception) when (exception is HttpRequestException or OperationCanceledException)
        {
            // Local revocation is already committed; remote revocation is best-effort.
        }
    }

    internal static async Task DiscardSecretAsync(
        DbContextOptions<PlatformDbContext> options, ISecretStore secrets, TenantId tenantId, string reference, TimeSpan operationTimeout)
    {
        using var timeout = new CancellationTokenSource(operationTimeout);
        try
        {
            await using var db = PlatformDbContext.CreateForPlatformOperations(options);
            await using var transaction = await db.Database.BeginTransactionAsync(timeout.Token);
            await LockOwnerAsync(db, tenantId, timeout.Token);
            if (!await db.StorageConnections.AnyAsync(row => row.SecretReference == reference, timeout.Token))
                await secrets.DeleteAsync(new SecretReference(reference), timeout.Token);
            await transaction.CommitAsync(timeout.Token);
        }
        catch
        {
            // A database outage leaves an encrypted orphan rather than deleting potentially live credentials.
        }
    }

    internal static Task<int> LockOwnerAsync(PlatformDbContext db, TenantId tenantId, CancellationToken cancellationToken) =>
        db.Database.ExecuteSqlInterpolatedAsync(
            $"SELECT \"Id\" FROM tenants WHERE \"Id\" = {tenantId.Value} FOR NO KEY UPDATE", cancellationToken);

    // The redirect URI is exact-match configuration (it must equal the one registered with Google); never derived from request headers.
    private string ConfiguredRedirectUri() =>
        Uri.TryCreate(_platform.RedirectUri, UriKind.Absolute, out var uri) && uri.Scheme is "https" or "http" && string.IsNullOrEmpty(uri.Fragment)
            ? uri.AbsoluteUri
            : throw new InvalidOperationException("GoogleDrive:RedirectUri is not configured.");

    private static string GenerateRandomString(int bytesCount)
    {
        var bytes = RandomNumberGenerator.GetBytes(bytesCount);
        return Base64UrlEncode(bytes);
    }

    private static string Base64UrlEncode(byte[] bytes) =>
        Convert.ToBase64String(bytes).Replace("+", "-").Replace("/", "_").TrimEnd('=');

    private sealed class GoogleTokenResponse
    {
        [JsonPropertyName("access_token")]
        public string? AccessToken { get; set; }

        [JsonPropertyName("refresh_token")]
        public string? RefreshToken { get; set; }

        [JsonPropertyName("expires_in")]
        public int ExpiresIn { get; set; }
    }

    private sealed class GoogleAboutResponse
    {
        [JsonPropertyName("user")]
        public GoogleUser? User { get; set; }
    }

    private sealed class GoogleUser
    {
        [JsonPropertyName("emailAddress")]
        public string? EmailAddress { get; set; }

        [JsonPropertyName("displayName")]
        public string? DisplayName { get; set; }
    }
}

public sealed record GoogleDriveOAuthCallbackResult(
    bool Success,
    Guid? TenantId,
    Guid? ConnectionId,
    string? AccountEmail,
    string? ErrorMessage);
