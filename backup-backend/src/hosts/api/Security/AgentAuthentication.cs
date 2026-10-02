using System.Security.Claims;
using System.Text.Encodings.Web;
using BackupPlatform.Agents.Credentials;
using BackupPlatform.Application.Agents;
using BackupPlatform.Contracts.Agents;
using BackupPlatform.Domain.Identity;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Options;

namespace BackupPlatform.Api.Security;

/// <summary>
/// Agent-only authentication boundary: <c>Authorization: BackupAgent &lt;credential-id&gt;.&lt;secret&gt;</c> over TLS.
/// </summary>
/// <remarks>
/// Entirely separate from <see cref="BrowserAuthentication"/>: it is never the default scheme, only agent endpoints
/// name it, it ignores cookies, and its claim types do not overlap the user claims, so an agent principal can never
/// satisfy a user check (and vice versa). Every request re-validates the credential against the database, so
/// revocation and rotation take effect immediately. Failures are opaque to the caller and log only the failure kind.
/// </remarks>
public static class AgentAuthentication
{
    public const string Scheme = "BackupPlatform.Agent";
    public const string AgentIdClaimType = "backup:agent";
    public const string TenantIdClaimType = "backup:agent-tenant";
    public const string CredentialIdClaimType = "backup:agent-credential";
    public const string GenerationClaimType = "backup:agent-generation";

    internal static ClaimsPrincipal CreatePrincipal(AgentIdentity identity) => new(new ClaimsIdentity(
    [
        new Claim(AgentIdClaimType, identity.AgentId.Value.ToString("D")),
        new Claim(TenantIdClaimType, identity.TenantId.Value.ToString("D")),
        new Claim(CredentialIdClaimType, identity.CredentialId.ToString("D")),
        new Claim(GenerationClaimType, identity.CredentialGeneration.ToString(System.Globalization.CultureInfo.InvariantCulture))
    ], Scheme));

    public static AgentIdentity? TryGetIdentity(ClaimsPrincipal? principal)
    {
        if (principal?.Identities.FirstOrDefault(identity => identity.AuthenticationType == Scheme && identity.IsAuthenticated) is not { } claims)
        {
            return null;
        }
        if (!TryGetGuid(claims, AgentIdClaimType, out var agentId)
            || !TryGetGuid(claims, TenantIdClaimType, out var tenantId)
            || !TryGetGuid(claims, CredentialIdClaimType, out var credentialId)
            || claims.FindAll(GenerationClaimType).SingleOrDefault() is not { } generationClaim
            || !int.TryParse(generationClaim.Value, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var generation))
        {
            return null;
        }
        return new AgentIdentity(new AgentId(agentId), new TenantId(tenantId), credentialId, generation);
    }

    private static bool TryGetGuid(ClaimsIdentity identity, string type, out Guid value)
    {
        value = Guid.Empty;
        var claim = identity.FindAll(type).SingleOrDefault();
        return claim is not null && Guid.TryParse(claim.Value, out value) && value != Guid.Empty;
    }
}

/// <summary>Endpoint metadata: the endpoint also accepts a pending rotation credential (rotation confirmation only).</summary>
public sealed class AllowPendingAgentCredential { }

/// <summary>Endpoint metadata: protected by agent or enrollment-token credentials, so browser antiforgery does not apply.</summary>
public sealed class AgentApiEndpoint { }

public sealed class AgentAuthenticationOptions : AuthenticationSchemeOptions { }

public sealed class AgentAuthenticationHandler(
    IOptionsMonitor<AgentAuthenticationOptions> options, ILoggerFactory logger, UrlEncoder encoder, AgentCredentialService credentials,
    AgentAuthFailureLimiter failureLimiter)
    : AuthenticationHandler<AgentAuthenticationOptions>(options, logger, encoder)
{
    private const string RateLimitedItem = "BackupPlatform.Agent.RateLimited";

    protected override async Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        var headers = Request.Headers.Authorization;
        if (headers.Count == 0)
        {
            return AuthenticateResult.NoResult();
        }
        if (headers.Count != 1)
        {
            return AuthenticateResult.Fail("Ambiguous authorization header.");
        }
        var header = headers[0];
        if (header is null || !header.StartsWith(AgentAuthorizationScheme.Name, StringComparison.OrdinalIgnoreCase))
        {
            return AuthenticateResult.NoResult();
        }
        // The key is the network client, never anything derived from the presented credential.
        var client = AgentClientKey.From(Context.Connection.RemoteIpAddress);
        if (failureLimiter.IsBlocked(client))
        {
            Context.Items[RateLimitedItem] = failureLimiter.RetryAfter;
            Logger.LogWarning("Agent authentication refused: too many recent failures from {Client}.", client);
            return AuthenticateResult.Fail("Too many failed agent authentications.");
        }
        if (!Request.IsHttps)
        {
            return AuthenticateResult.Fail("Agent credentials require TLS.");
        }
        if (!AgentAuthorizationScheme.TryParseHeaderValue(header, out var credentialId, out var secret))
        {
            failureLimiter.RecordFailure(client);
            return AuthenticateResult.Fail("Malformed agent credential.");
        }
        var allowPending = Context.GetEndpoint()?.Metadata.GetMetadata<AllowPendingAgentCredential>() is not null;
        var result = await credentials.AuthenticateAsync(credentialId, secret, allowPending, Context.RequestAborted);
        if (result.Identity is null)
        {
            failureLimiter.RecordFailure(client);
            Logger.LogInformation("Agent authentication rejected: {Failure}.", result.Failure);
            return AuthenticateResult.Fail("Agent authentication failed.");
        }
        return AuthenticateResult.Success(new AuthenticationTicket(AgentAuthentication.CreatePrincipal(result.Identity), Scheme.Name));
    }

    protected override async Task HandleChallengeAsync(AuthenticationProperties properties)
    {
        if (Context.Items.TryGetValue(RateLimitedItem, out var retryAfter) && retryAfter is TimeSpan delay)
        {
            await AgentRateLimiting.WriteRateLimitedAsync(Context, delay, Context.RequestAborted);
            return;
        }
        Response.StatusCode = StatusCodes.Status401Unauthorized;
        Response.Headers.WWWAuthenticate = AgentAuthorizationScheme.Name;
    }

    protected override Task HandleForbiddenAsync(AuthenticationProperties properties)
    {
        Response.StatusCode = StatusCodes.Status403Forbidden;
        return Task.CompletedTask;
    }
}

public sealed class HttpCurrentAgent : ICurrentAgent
{
    public HttpCurrentAgent(IHttpContextAccessor httpContextAccessor) =>
        Identity = AgentAuthentication.TryGetIdentity(httpContextAccessor.HttpContext?.User);

    public AgentIdentity? Identity { get; }
}
