using System.Security.Claims;
using System.Text.Json;
using BackupPlatform.Api.Endpoints;
using BackupPlatform.Api.Security;
using BackupPlatform.Application.Identity;
using BackupPlatform.Application.Tenancy;
using BackupPlatform.Domain.Identity;
using BackupPlatform.Domain.Tenancy;
using BackupPlatform.Identity;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;

namespace BackupPlatform.UnitTests;

public sealed class AuthenticationEndpointsTests
{
    [Fact]
    public async Task SessionProbe_Answers200WithAuthenticatedFalse_WhenSignedOut()
    {
        var userId = UserId.New();
        await using var harness = CreateHarness(userId, "Correct-password-123!");

        var (status, body) = await harness.InvokeAsync("GET", "/api/auth/session");

        Assert.Equal(StatusCodes.Status200OK, status);
        Assert.False(body.GetProperty("authenticated").GetBoolean());
        Assert.False(body.TryGetProperty("restricted", out _));
    }

    [Theory]
    [InlineData(SessionAuthenticationState.SecondFactorPending)]
    [InlineData(SessionAuthenticationState.PasswordChangeRequired)]
    public async Task SessionProbe_Answers200WithRestrictedTrue_WhenSessionIsNotFullyAuthenticated(SessionAuthenticationState state)
    {
        var userId = UserId.New();
        await using var harness = CreateHarness(userId, "Correct-password-123!");

        var (status, body) = await harness.InvokeAsync("GET", "/api/auth/session", userId, Guid.NewGuid(), state);

        Assert.Equal(StatusCodes.Status200OK, status);
        Assert.False(body.GetProperty("authenticated").GetBoolean());
        Assert.True(body.GetProperty("restricted").GetBoolean());
    }

    [Fact]
    public async Task SessionProbe_Answers200WithProfile_WhenFullyAuthenticated()
    {
        var userId = UserId.New();
        await using var harness = CreateHarness(userId, "Correct-password-123!");

        var (status, body) = await harness.InvokeAsync("GET", "/api/auth/session", userId, Guid.NewGuid(), SessionAuthenticationState.FullyAuthenticated);

        Assert.Equal(StatusCodes.Status200OK, status);
        Assert.True(body.GetProperty("authenticated").GetBoolean());
        var user = body.GetProperty("user");
        Assert.Equal(userId.Value, user.GetProperty("userId").GetGuid());
        Assert.Equal("Test User", user.GetProperty("displayName").GetString());
        Assert.Equal("UserAdmin", user.GetProperty("role").GetString());
        Assert.False(user.GetProperty("isPlatformAdmin").GetBoolean());
    }

    [Fact]
    public async Task RegisterAndRemovePasskey_WithWrongPassword_Return400InvalidPassword_Not401()
    {
        var userId = UserId.New();
        var existingPasskeyId = Guid.NewGuid();
        await using var harness = CreateHarness(userId, "Correct-password-123!", existingPasskeyId);
        var sessionId = Guid.NewGuid();

        var (regStatus, regBody) = await harness.InvokeAsync(
            "POST",
            "/api/auth/passkeys/register",
            userId,
            sessionId,
            SessionAuthenticationState.FullyAuthenticated,
            new RegisterPasskeyRequest
            {
                ChallengeId = Guid.NewGuid(),
                Name = "Laptop Key",
                Response = new Fido2NetLib.AuthenticatorAttestationRawResponse
                {
                    Id = "AQID",
                    RawId = [1, 2, 3],
                    Type = Fido2NetLib.Objects.PublicKeyCredentialType.PublicKey,
                    Response = new Fido2NetLib.AuthenticatorAttestationRawResponse.AttestationResponse
                    {
                        AttestationObject = [1, 2, 3],
                        ClientDataJson = [1, 2, 3]
                    }
                },
                CurrentPassword = "Wrong-password!"
            });

        Assert.Equal(StatusCodes.Status400BadRequest, regStatus);
        Assert.Equal("invalid_password", regBody.GetProperty("error").GetString());

        var (delStatus, delBody) = await harness.InvokeAsync(
            "DELETE",
            $"/api/auth/passkeys/{existingPasskeyId}",
            userId,
            sessionId,
            SessionAuthenticationState.FullyAuthenticated,
            new { currentPassword = "Wrong-password!" },
            new RouteValueDictionary { ["id"] = existingPasskeyId.ToString("D") });

        Assert.Equal(StatusCodes.Status400BadRequest, delStatus);
        Assert.Equal("invalid_password", delBody.GetProperty("error").GetString());
    }

    [Fact]
    public async Task ChangePassword_ReturnsAvailableSecondFactorMethods_DistinguishingPasskeyOnlyFromTotp()
    {
        var userId = UserId.New();
        await using var passkeyOnlyHarness = CreateHarness(userId, "Current-password-123!", Guid.NewGuid(), hasTotp: false);
        var (status1, body1) = await passkeyOnlyHarness.InvokeAsync(
            "POST",
            "/api/auth/change-password",
            userId,
            Guid.NewGuid(),
            SessionAuthenticationState.PasswordChangeRequired,
            new { currentPassword = "Current-password-123!", newPassword = "Replacement-password-456!" });

        Assert.Equal(StatusCodes.Status200OK, status1);
        Assert.True(body1.GetProperty("secondFactorRequired").GetBoolean());
        var methods1 = body1.GetProperty("methods").EnumerateArray().Select(e => e.GetString()!).ToArray();
        Assert.Equal(["passkey", "recovery"], methods1);

        await using var bothHarness = CreateHarness(userId, "Current-password-123!", Guid.NewGuid(), hasTotp: true);
        var (status2, body2) = await bothHarness.InvokeAsync(
            "POST",
            "/api/auth/change-password",
            userId,
            Guid.NewGuid(),
            SessionAuthenticationState.PasswordChangeRequired,
            new { currentPassword = "Current-password-123!", newPassword = "Replacement-password-456!" });

        Assert.Equal(StatusCodes.Status200OK, status2);
        var methods2 = body2.GetProperty("methods").EnumerateArray().Select(e => e.GetString()!).ToArray();
        Assert.Equal(["totp", "passkey", "recovery"], methods2);

        await using var totpOnlyHarness = CreateHarness(userId, "Current-password-123!", existingPasskeyId: null, hasTotp: true);
        var (sfStatus, sfBody) = await totpOnlyHarness.InvokeAsync(
            "POST",
            "/api/auth/passkeys/second-factor/options",
            userId,
            Guid.NewGuid(),
            SessionAuthenticationState.SecondFactorPending);

        Assert.Equal(StatusCodes.Status400BadRequest, sfStatus);
        Assert.Equal("passkey_not_enrolled", sfBody.GetProperty("error").GetString());
    }

    private static EndpointHarness CreateHarness(UserId userId, string password, Guid? existingPasskeyId = null, bool hasTotp = false)
    {
        var clock = new FakeTimeProvider(new DateTimeOffset(2026, 11, 24, 12, 0, 0, TimeSpan.Zero));
        var hasher = new PasswordHashService();
        var store = new InMemoryAuthStore(new PasswordCredential(userId, "alice", hasher.Hash(userId, password)), existingPasskeyId, hasTotp);
        var accessor = new HttpContextAccessor();

        var builder = WebApplication.CreateBuilder();
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Authentication:Passkeys:RelyingPartyId"] = "localhost",
            ["Authentication:Passkeys:Origins:0"] = "https://localhost:5173"
        });
        builder.Services.AddSingleton<TimeProvider>(clock);
        builder.Services.AddSingleton<IHttpContextAccessor>(accessor);
        builder.Services.AddSingleton<PasswordHashService>(hasher);
        builder.Services.AddSingleton<IIdentityStore>(store);
        builder.Services.AddSingleton<ILoginThrottle>(new NoopThrottle());
        builder.Services.AddScoped<ICurrentUser, HttpCurrentUser>();
        builder.Services.AddSingleton<IUserAccessStore>(new SingleUserAccessStore(new UserProfile(userId, "Test User", UserRole.UserAdmin)));
        builder.Services.AddScoped<AuthenticationService>();
        builder.Services.AddScoped<PasskeyService>();

        var app = builder.Build();
        app.MapAuthenticationEndpoints();
        return new EndpointHarness(app, accessor);
    }

    private sealed class EndpointHarness(WebApplication app, HttpContextAccessor accessor) : IAsyncDisposable
    {
        private static readonly JsonSerializerOptions WebJson = new(JsonSerializerDefaults.Web);
        public async Task<(int StatusCode, JsonElement Body)> InvokeAsync(
            string method,
            string path,
            UserId? userId = null,
            Guid? sessionId = null,
            SessionAuthenticationState? sessionState = null,
            object? jsonBody = null,
            RouteValueDictionary? routeValues = null)
        {
            var endpoints = ((IEndpointRouteBuilder)app).DataSources.SelectMany(ds => ds.Endpoints).OfType<RouteEndpoint>();
            var endpoint = endpoints.Single(ep =>
            {
                var methods = ep.Metadata.GetMetadata<HttpMethodMetadata>()?.HttpMethods;
                if (methods is not null && !methods.Contains(method, StringComparer.OrdinalIgnoreCase)) return false;
                var raw = ep.RoutePattern.RawText ?? "";
                if (string.Equals(raw, path, StringComparison.OrdinalIgnoreCase)) return true;
                return routeValues is not null && raw.StartsWith("/api/auth/passkeys/{id", StringComparison.OrdinalIgnoreCase) && path.StartsWith("/api/auth/passkeys/", StringComparison.OrdinalIgnoreCase);
            });

            await using var scope = app.Services.CreateAsyncScope();
            var context = new DefaultHttpContext { RequestServices = scope.ServiceProvider };
            context.Request.Method = method;
            context.Request.Path = path;
            if (routeValues is not null) context.Request.RouteValues = routeValues;
            if (jsonBody is not null)
            {
                var bytes = JsonSerializer.SerializeToUtf8Bytes(jsonBody, jsonBody.GetType(), WebJson);
                context.Request.ContentType = "application/json";
                context.Request.ContentLength = bytes.Length;
                context.Request.Body = new MemoryStream(bytes);
                context.Features.Set<Microsoft.AspNetCore.Http.Features.IHttpRequestBodyDetectionFeature>(new PresentBodyFeature());
            }
            if (userId is not null && sessionId is not null)
            {
                context.User = new ClaimsPrincipal(new ClaimsIdentity(
                [
                    new Claim(ClaimTypes.NameIdentifier, userId.Value.ToString("D")),
                    new Claim(BrowserAuthentication.SessionClaimType, sessionId.Value.ToString("D"))
                ], BrowserAuthentication.Scheme));
            }
            if (sessionState is { } state)
            {
                context.Features.Set(new BrowserSessionState(state));
            }
            context.Response.Body = new MemoryStream();
            accessor.HttpContext = context;

            await endpoint.RequestDelegate!(context);
            context.Response.Body.Position = 0;
            if (context.Response.Body.Length == 0)
            {
                using var empty = JsonDocument.Parse("{}");
                return (context.Response.StatusCode, empty.RootElement.Clone());
            }
            using var doc = await JsonDocument.ParseAsync(context.Response.Body);
            return (context.Response.StatusCode, doc.RootElement.Clone());
        }

        public ValueTask DisposeAsync() => app.DisposeAsync();

        private sealed class PresentBodyFeature : Microsoft.AspNetCore.Http.Features.IHttpRequestBodyDetectionFeature
        {
            public bool CanHaveBody => true;
        }
    }

    private sealed class NoopThrottle : ILoginThrottle
    {
        public Task<TimeSpan?> RetryAfterAsync(string kind, string? account, string? ipAddress, CancellationToken cancellationToken) => Task.FromResult<TimeSpan?>(null);
        public Task FailedAsync(string kind, string? account, string? ipAddress, CancellationToken cancellationToken) => Task.CompletedTask;
        public Task SucceededAsync(string kind, string? account, string? ipAddress, CancellationToken cancellationToken) => Task.CompletedTask;
    }

    private sealed class SingleUserAccessStore(UserProfile profile) : IUserAccessStore
    {
        public Task<UserProfile?> GetUserAsync(UserId userId, CancellationToken cancellationToken) =>
            Task.FromResult(userId == profile.UserId ? profile : null);
        public Task<IReadOnlyList<TenantAccess>> ListTenantsAsync(UserId userId, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<TenantAccess>>([]);
        public Task<TenantAccess?> GetMembershipAsync(UserId userId, TenantId tenantId, CancellationToken cancellationToken) =>
            Task.FromResult<TenantAccess?>(null);
        public Task<bool> IsPlatformAdminAsync(UserId userId, CancellationToken cancellationToken) =>
            Task.FromResult(profile.Role == UserRole.PlatformAdmin);
    }

    private sealed class InMemoryAuthStore(PasswordCredential credential, Guid? passkeyId, bool hasTotp) : IIdentityStore
    {
        private readonly PasskeyCredential? _passkey = passkeyId is { } id
            ? new PasskeyCredential(id, credential.UserId, "Existing Key", [1, 2, 3], [4, 5, 6], 0, null, DateTimeOffset.UtcNow)
            : null;
        private readonly TotpCredential? _totp = hasTotp
            ? new TotpCredential(credential.UserId, "encrypted", DateTimeOffset.UtcNow, null)
            : null;

        public Task<PasswordCredential?> FindCredentialAsync(string normalizedIdentifier, CancellationToken cancellationToken) =>
            Task.FromResult(credential.NormalizedLoginIdentifier == normalizedIdentifier ? credential : null);
        public Task<PasswordCredential?> FindCredentialByUserIdAsync(UserId userId, CancellationToken cancellationToken) =>
            Task.FromResult(credential.UserId == userId ? credential : null);
        public Task UpdatePasswordHashAsync(UserId userId, string passwordHash, CancellationToken cancellationToken) => Task.CompletedTask;
        public Task<bool> ChangePasswordAsync(UserId userId, string newPasswordHash, DateTimeOffset now, CancellationToken cancellationToken) => Task.FromResult(true);
        public Task RecordLoginFailureAsync(UserId? userId, DateTimeOffset failedAt, CancellationToken cancellationToken) => Task.CompletedTask;
        public Task RecordRepeatedLoginFailuresAsync(UserId userId, DateTimeOffset now, CancellationToken cancellationToken) => Task.CompletedTask;
        public Task CreateSessionAsync(UserSession session, DateTimeOffset now, CancellationToken cancellationToken) => Task.CompletedTask;
        public Task<SessionAuthenticationState?> ValidateSessionAsync(Guid sessionId, UserId userId, DateTimeOffset now, CancellationToken cancellationToken) => Task.FromResult<SessionAuthenticationState?>(SessionAuthenticationState.FullyAuthenticated);
        public Task SetSessionStateAsync(Guid sessionId, UserId userId, SessionAuthenticationState state, CancellationToken cancellationToken) => Task.CompletedTask;
        public Task<IReadOnlyList<UserSession>> ListSessionsAsync(UserId userId, DateTimeOffset now, CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<UserSession>>([]);
        public Task<bool> EndSessionAsync(Guid sessionId, UserId userId, DateTimeOffset endedAt, CancellationToken cancellationToken) => Task.FromResult(true);
        public Task EndAllSessionsAsync(UserId userId, DateTimeOffset endedAt, CancellationToken cancellationToken) => Task.CompletedTask;
        public Task<bool> HasSecondFactorAsync(UserId userId, CancellationToken cancellationToken) => Task.FromResult(_passkey is not null || _totp is not null);
        public Task<TotpCredential?> FindTotpCredentialAsync(UserId userId, CancellationToken cancellationToken) => Task.FromResult(_totp);
        public Task<TotpEnrollmentOutcome> SaveTotpCredentialAsync(TotpCredential totp, IReadOnlyList<string> recoveryCodeHashes, DateTimeOffset now, CancellationToken cancellationToken) => Task.FromResult(TotpEnrollmentOutcome.EnrolledWithRecoveryCodes);
        public Task<bool> RemoveTotpCredentialAsync(UserId userId, DateTimeOffset now, CancellationToken cancellationToken) => Task.FromResult(false);
        public Task<bool> TryUseTotpStepAsync(UserId userId, long step, CancellationToken cancellationToken) => Task.FromResult(false);
        public Task SaveRecoveryCodesAsync(UserId userId, IEnumerable<string> codeHashes, DateTimeOffset now, CancellationToken cancellationToken) => Task.CompletedTask;
        public Task<bool> TryRedeemRecoveryCodeAsync(UserId userId, string codeHash, DateTimeOffset now, CancellationToken cancellationToken) => Task.FromResult(false);
        public Task<int> GetRemainingRecoveryCodesCountAsync(UserId userId, CancellationToken cancellationToken) => Task.FromResult(0);
        public Task<bool> HasPasskeysAsync(UserId userId, CancellationToken cancellationToken) => Task.FromResult(_passkey is not null);
        public Task<IReadOnlyList<PasskeyCredential>> ListPasskeysAsync(UserId userId, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<PasskeyCredential>>(_passkey is null ? [] : [_passkey]);
        public Task<PasskeyCredential?> FindPasskeyByCredentialIdAsync(byte[] credentialId, CancellationToken cancellationToken) => Task.FromResult<PasskeyCredential?>(null);
        public Task<PasskeyCredential?> FindPasskeyByIdAsync(Guid id, UserId userId, CancellationToken cancellationToken) =>
            Task.FromResult(_passkey is not null && _passkey.Id == id && _passkey.UserId == userId ? _passkey : null);
        public Task<bool> AddPasskeyAsync(PasskeyCredential passkey, IReadOnlyList<string> recoveryCodeHashes, DateTimeOffset now, CancellationToken cancellationToken) => Task.FromResult(true);
        public Task<bool> RemovePasskeyAsync(Guid id, UserId userId, DateTimeOffset now, CancellationToken cancellationToken) => Task.FromResult(true);
        public Task<bool> UpdatePasskeyUsageAsync(Guid id, uint newCounter, DateTimeOffset usedAt, CancellationToken cancellationToken) => Task.FromResult(true);
        public Task SaveChallengeAsync(PasskeyChallenge challenge, CancellationToken cancellationToken) => Task.CompletedTask;
        public Task<PasskeyChallenge?> ConsumeChallengeAsync(Guid challengeId, string purpose, DateTimeOffset now, CancellationToken cancellationToken) => Task.FromResult<PasskeyChallenge?>(null);
        public Task<bool> IsUserDisabledAsync(UserId userId, CancellationToken cancellationToken) => Task.FromResult(false);
        public Task<string?> GetUserDisplayNameAsync(UserId userId, CancellationToken cancellationToken) => Task.FromResult<string?>("Test User");
        public Task IssueEmailTokenAsync(AccountEmailToken token, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<UserId?> FindUsableEmailTokenAsync(string tokenHash, AccountEmailPurpose purpose, DateTimeOffset now, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<bool> ConfirmEmailAsync(string tokenHash, DateTimeOffset now, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<bool> CompletePasswordResetAsync(string tokenHash, string newPasswordHash, DateTimeOffset now, CancellationToken cancellationToken) => throw new NotSupportedException();
    }
}
