using BackupPlatform.Api.Security;
using BackupPlatform.Application.Backups;
using BackupPlatform.Application.Identity;
using BackupPlatform.Application.Platform;
using BackupPlatform.Application.Tenancy;
using BackupPlatform.Domain.Identity;

namespace BackupPlatform.Api.Endpoints;

public sealed class TenantRecoveryKeyRequest
{
    public string? PublicKeyPem { get; init; }
}

/// <summary>
/// Artifact encryption key administration. Wrap key rotation and retirement are PlatformAdmin-only (the tenant never handles its wrap key); the tenant's own
/// recovery recipient is managed by its Owners and Admins. Nothing here ever returns key material: only ids, versions, states and counts.
/// </summary>
public static class ArtifactEncryptionEndpoints
{
    public static IEndpointRouteBuilder MapArtifactEncryptionEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var platform = endpoints.MapGroup("/api/platform/tenants/{tenantId:guid}/wrap-keys").RequireAuthorization(AuthorizationPolicies.PlatformAdmin);
        platform.MapGet("", ListWrapKeysAsync);
        platform.MapPost("/rotate", RotateWrapKeyAsync);
        platform.MapPost("/{version:int}/retire", RetireWrapKeyAsync);

        var tenant = endpoints.MapGroup("/api/tenants/{tenantId:guid}/encryption").WithMetadata(new TenantScopedEndpoint())
            .RequireAuthorization(AuthorizationPolicies.TenantAdministrator);
        tenant.MapGet("", GetTenantEncryptionAsync);
        tenant.MapPut("/recovery-key", SetRecoveryKeyAsync);
        tenant.MapDelete("/recovery-key", RemoveRecoveryKeyAsync);
        return endpoints;
    }

    private static object ToResponse(TenantWrapKeyView key) => new { version = key.Version, keyId = key.KeyId, state = key.State.ToString(), createdAt = key.CreatedAt, artifactCount = key.ArtifactCount };

    private static IResult TenantNotFound() => Results.NotFound(new { error = "tenant_not_found" });

    private static async Task<IResult> ListWrapKeysAsync(Guid tenantId, IPlatformAdministrationStore tenants, ITenantWrapKeyStore keys, CancellationToken cancellationToken)
    {
        if (tenantId == Guid.Empty || await tenants.GetTenantAsync(new TenantId(tenantId), cancellationToken) is null) return TenantNotFound();
        return Results.Ok(new { items = (await keys.ListAsync(new TenantId(tenantId), cancellationToken)).Select(ToResponse).ToList() });
    }

    private static async Task<IResult> RotateWrapKeyAsync(Guid tenantId, ICurrentUser user, IPlatformAdministrationStore tenants, ITenantWrapKeyStore keys, CancellationToken cancellationToken)
    {
        if (tenantId == Guid.Empty || await tenants.GetTenantAsync(new TenantId(tenantId), cancellationToken) is null) return TenantNotFound();
        var rotation = await keys.RotateAsync(new TenantId(tenantId), user.UserId!, cancellationToken);
        return Results.Json(new { key = ToResponse(rotation.Key), previousKey = rotation.PreviousKey is null ? null : ToResponse(rotation.PreviousKey) }, statusCode: StatusCodes.Status201Created);
    }

    private static async Task<IResult> RetireWrapKeyAsync(Guid tenantId, int version, ICurrentUser user, IPlatformAdministrationStore tenants, ITenantWrapKeyStore keys,
        CancellationToken cancellationToken)
    {
        if (tenantId == Guid.Empty || await tenants.GetTenantAsync(new TenantId(tenantId), cancellationToken) is null) return TenantNotFound();
        var result = await keys.RetireAsync(new TenantId(tenantId), version, user.UserId!, cancellationToken);
        return result.Outcome switch
        {
            TenantWrapKeyRetireOutcome.Retired => Results.Ok(new { key = ToResponse(result.Key!) }),
            TenantWrapKeyRetireOutcome.NotFound => Results.NotFound(new { error = "key_not_found" }),
            TenantWrapKeyRetireOutcome.ActiveKey => Results.Conflict(new { error = "cannot_retire_active_key" }),
            _ => Results.Conflict(new { error = "already_retired" })
        };
    }

    private static async Task<IResult> GetTenantEncryptionAsync(ITenantContext tenant, ITenantWrapKeyStore keys, ITenantRecoveryKeyStore recovery, ArtifactRecoveryOptions platform,
        CancellationToken cancellationToken) => Results.Ok(await ViewAsync(tenant.TenantId!, keys, recovery, platform, cancellationToken));

    private static async Task<IResult> SetRecoveryKeyAsync(TenantRecoveryKeyRequest? request, ITenantContext tenant, ICurrentUser user, ITenantWrapKeyStore keys, ITenantRecoveryKeyStore recovery,
        ArtifactRecoveryOptions platform, CancellationToken cancellationToken)
    {
        try
        {
            await recovery.SetAsync(tenant.TenantId!, user.UserId!, request?.PublicKeyPem ?? string.Empty, cancellationToken);
        }
        catch (InvalidRecoveryKeyException exception)
        {
            return Results.BadRequest(new { error = "invalid_recovery_key", code = exception.Code });
        }
        return Results.Ok(await ViewAsync(tenant.TenantId!, keys, recovery, platform, cancellationToken));
    }

    private static async Task<IResult> RemoveRecoveryKeyAsync(ITenantContext tenant, ICurrentUser user, ITenantRecoveryKeyStore recovery, CancellationToken cancellationToken)
    {
        await recovery.RemoveAsync(tenant.TenantId!, user.UserId!, cancellationToken);
        return Results.NoContent();
    }

    private static async Task<object> ViewAsync(TenantId tenantId, ITenantWrapKeyStore keys, ITenantRecoveryKeyStore recovery, ArtifactRecoveryOptions platform, CancellationToken cancellationToken)
    {
        var tenantKey = await recovery.GetAsync(tenantId, cancellationToken);
        var active = (await keys.ListAsync(tenantId, cancellationToken)).FirstOrDefault(key => key.State == Domain.Backups.TenantWrapKeyState.Active);
        return new
        {
            platformRecovery = new { configured = platform.PlatformRecovery is not null, keyId = platform.PlatformRecovery?.KeyId, keyVersion = platform.PlatformRecovery?.Version },
            tenantRecovery = tenantKey is null ? null : new { keyId = tenantKey.KeyId, keyVersion = tenantKey.Version, createdAt = tenantKey.CreatedAt },
            wrapKey = active is null ? null : new { version = active.Version, keyId = active.KeyId, state = active.State.ToString() }
        };
    }
}
