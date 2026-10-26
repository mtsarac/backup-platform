using System.Security.Cryptography;
using BackupPlatform.Domain.Audit;
using BackupPlatform.Domain.Identity;
using BackupPlatform.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace BackupPlatform.IntegrationTests;

public sealed class AuditSealIntegrationTests(PostgreSqlFixture fixture) : IClassFixture<PostgreSqlFixture>
{
    [Fact]
    public async Task Persisted_audit_signature_survives_jsonb_normalization_and_detects_tampering()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var options = new DbContextOptionsBuilder<PlatformDbContext>(fixture.Options)
            .AddInterceptors(new AuditSigningInterceptor("test-key", key.ExportPkcs8PrivateKeyPem())).Options;
        var record = AuditEvent.Record(DateTimeOffset.Parse("2026-10-21T10:00:00Z"), UserId.System, AuditActorKind.System,
            "test.sealed", null, "test", Guid.NewGuid().ToString("D"), new Dictionary<string, object?> { ["z"] = 1.25, ["a"] = "value" });
        await using (var writer = PlatformDbContext.CreateForPlatformOperations(options))
        {
            writer.AuditEvents.Add(record);
            await writer.SaveChangesAsync();
        }
        await using var reader = fixture.CreatePlatformContext();
        var persisted = await reader.AuditEvents.SingleAsync(row => row.Id == record.Id);
        var signature = (string)reader.Entry(persisted).Property(AuditSigningInterceptor.SignatureProperty).CurrentValue!;
        Assert.True(AuditSigningInterceptor.Verify(persisted, signature, key.ExportSubjectPublicKeyInfoPem()));
        await reader.Database.ExecuteSqlInterpolatedAsync($"UPDATE audit_events SET \"Action\" = 'test.tampered' WHERE \"Id\" = {record.Id}");
        await reader.Entry(persisted).ReloadAsync();
        Assert.False(AuditSigningInterceptor.Verify(persisted, signature, key.ExportSubjectPublicKeyInfoPem()));
    }
}
