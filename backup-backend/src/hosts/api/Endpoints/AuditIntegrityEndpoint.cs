using BackupPlatform.Api.Security;
using BackupPlatform.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace BackupPlatform.Api.Endpoints;

/// <summary>Platform administrators verify the audit seal across every tenant: a platform operation, not a tenant request.</summary>
internal static class AuditIntegrityEndpoint
{
    internal static IEndpointRouteBuilder MapAuditIntegrityEndpoint(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet("/api/platform/audit/integrity", VerifyAsync).RequireAuthorization(AuthorizationPolicies.PlatformAdmin);
        return endpoints;
    }

    private static async Task<IResult> VerifyAsync(DateTimeOffset? after, int? limit, AuditSealKeys keys,
        DbContextOptions<PlatformDbContext> options, CancellationToken cancellationToken)
    {
        if (limit is < 1 or > 1000) return Results.BadRequest(new { error = "invalid_limit" });
        await using var context = PlatformOperationsDbContext.Create(options);
        var rows = await context.AuditEvents.Where(row => after == null || row.At > after)
            .OrderBy(row => row.At).ThenBy(row => row.Id).Take(limit ?? 100).ToListAsync(cancellationToken);
        var events = rows.Select(row =>
        {
            var keyId = (string?)context.Entry(row).Property(AuditSigningInterceptor.KeyProperty).CurrentValue;
            var signature = (string?)context.Entry(row).Property(AuditSigningInterceptor.SignatureProperty).CurrentValue;
            var status = keyId is null || signature is null ? "Unsealed"
                : !keys.PublicKeys.TryGetValue(keyId, out var pem) ? "UnknownKey"
                : AuditSigningInterceptor.Verify(row, signature, pem) ? "Verified" : "Invalid";
            return new { row.Id, row.At, keyId, signature, status, canonicalPayloadBase64 = Convert.ToBase64String(AuditSigningInterceptor.Canonical(row)) };
        }).ToArray();
        return Results.Ok(new { signingEnabled = keys.Interceptor is not null, publicKeys = keys.PublicKeys, events });
    }
}
