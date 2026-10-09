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
        {
            return new GoogleDriveOAuthCallbackResult(false, null, null, null, "Google authorization was denied or failed.");
        }

        if (string.IsNullOrWhiteSpace(code) || string.IsNullOrWhiteSpace(state))
        {
            return new GoogleDriveOAuthCallbackResult(false, null, null, null, "Missing authorization code or state.");
        }

        await using var db = PlatformDbContext.CreateForPlatformOperations(dbOptions);
        var now = time.GetUtcNow();
        var claimed = await db.GoogleDriveOAuthStates
            .Where(s => s.State == state && !s.Used && s.ExpiresAt > now)
            .ExecuteUpdateAsync(setters => setters.SetProperty(s => s.Used, true), cancellationToken);
        if (claimed != 1)
        {
            return new GoogleDriveOAuthCallbackResult(false, null, null, null, "Invalid, expired, or previously used authorization state.");
        }
        var oauthState = await db.GoogleDriveOAuthStates.SingleAsync(s => s.State == state, cancellationToken);
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        await LockOwnerAsync(db, oauthState.TenantId, cancellationToken);

        var connection = await db.StorageConnections
            .SingleOrDefaultAsync(c => c.TenantId == oauthState.TenantId && c.Id == oauthState.ConnectionId, cancellationToken);

        if (connection is null || connection.ProviderType != StorageProviderTypes.GoogleDrive || connection.AdministrativelyDisabled)
        {
            await db.SaveChangesAsync(cancellationToken);
            return new GoogleDriveOAuthCallbackResult(false, null, null, null, "Associated storage connection not found.");
        }

        // Token exchange
        using var exchangeReq = new HttpRequestMessage(HttpMethod.Post, TokenEndpoint);
        exchangeReq.Content = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["client_id"] = _platform.ClientId,
            ["client_secret"] = _platform.ClientSecret,
            ["code"] = code,
            ["code_verifier"] = oauthState.CodeVerifier,
            ["grant_type"] = "authorization_code",
            ["redirect_uri"] = oauthState.RedirectUri
        });

        using var exchangeResp = await http.SendAsync(exchangeReq, cancellationToken);
        if (!exchangeResp.IsSuccessStatusCode)
        {
            logger.LogWarning("Google Drive token exchange failed with HTTP status {StatusCode}.", (int)exchangeResp.StatusCode);
            await db.SaveChangesAsync(cancellationToken);
            return new GoogleDriveOAuthCallbackResult(false, oauthState.TenantId.Value, connection.Id, null, "Google OAuth token exchange failed.");
        }

        var tokenData = await exchangeResp.Content.ReadFromJsonAsync<GoogleTokenResponse>(cancellationToken: cancellationToken);
        if (tokenData is null || string.IsNullOrWhiteSpace(tokenData.AccessToken))
        {
            await db.SaveChangesAsync(cancellationToken);
            return new GoogleDriveOAuthCallbackResult(false, oauthState.TenantId.Value, connection.Id, null, "Empty token response received from Google.");
        }

        var refreshToken = tokenData.RefreshToken;

        if (string.IsNullOrWhiteSpace(refreshToken))
        {
            await db.SaveChangesAsync(cancellationToken);
            return new GoogleDriveOAuthCallbackResult(false, oauthState.TenantId.Value, connection.Id, null, "No refresh token provided by Google.");
        }

        // Fetch User Info
        using var userReq = new HttpRequestMessage(HttpMethod.Get, UserInfoEndpoint);
        userReq.Headers.Authorization = new AuthenticationHeaderValue("Bearer", tokenData.AccessToken);
        using var userResp = await http.SendAsync(userReq, cancellationToken);
        var userInfo = userResp.IsSuccessStatusCode
            ? await userResp.Content.ReadFromJsonAsync<GoogleAboutResponse>(cancellationToken: cancellationToken)
            : null;

        var accountEmail = userInfo?.User?.EmailAddress ?? "";
        var accountName = userInfo?.User?.DisplayName ?? "";

        // Attach secret
        var reference = $"sc-{connection.Id:N}-{Guid.NewGuid():N}";
        var secretPayload = System.Text.Json.JsonSerializer.Serialize(new Dictionary<string, string>
        {
            ["refreshToken"] = refreshToken
        });

        await secretStore.SetAsync(new SecretReference(reference), new SecretValue(secretPayload), cancellationToken);
        var oldSecret = connection.AttachSecret(reference, ["refreshToken"], now);

        // Update configuration
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

        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(CancellationToken.None);

        if (oldSecret is not null)
        {
            try { await secretStore.DeleteAsync(new SecretReference(oldSecret), cancellationToken); }
            catch { }
        }

        return new GoogleDriveOAuthCallbackResult(true, oauthState.TenantId.Value, connection.Id, accountEmail, null);
    }

    public async Task DisconnectAsync(TenantId tenantId, Guid connectionId, Guid userId, CancellationToken cancellationToken)
    {
        await using var db = PlatformDbContext.CreateForPlatformOperations(dbOptions);
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        await LockOwnerAsync(db, tenantId, cancellationToken);
        var connection = await db.StorageConnections
            .SingleOrDefaultAsync(c => c.TenantId == tenantId && c.Id == connectionId, cancellationToken);

        if (connection is null || connection.ProviderType != StorageProviderTypes.GoogleDrive)
        {
            throw new InvalidOperationException("Google Drive storage connection not found.");
        }

        var now = time.GetUtcNow();

        // Attempt revoke
        if (connection.SecretReference is { } reference)
        {
            try
            {
                var secretVal = await secretStore.ResolveAsync(new SecretReference(reference), cancellationToken);
                var fields = System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, string>>(secretVal.Reveal());
                if (fields?.GetValueOrDefault("refreshToken") is { } refreshToken)
                {
                    using var revokeReq = new HttpRequestMessage(HttpMethod.Post, RevokeEndpoint);
                    revokeReq.Content = new FormUrlEncodedContent(new Dictionary<string, string> { ["token"] = refreshToken });
                    using var revokeResp = await http.SendAsync(revokeReq, cancellationToken);
                }
            }
            catch
            {
                // Best effort revoke
            }
        }

        var oldRef = connection.DetachSecret(now);
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
        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(CancellationToken.None);
        if (oldRef is not null)
        {
            try { await secretStore.DeleteAsync(new SecretReference(oldRef), cancellationToken); }
            catch { }
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
