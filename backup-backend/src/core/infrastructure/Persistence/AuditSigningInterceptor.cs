using System.Security.Cryptography;
using System.Text.Json;
using BackupPlatform.Domain.Audit;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace BackupPlatform.Infrastructure.Persistence;

/// <summary>Signs the complete immutable event before its transaction commits; private keys never enter PostgreSQL.</summary>
public sealed class AuditSigningInterceptor(string keyId, string privateKeyPem) : SaveChangesInterceptor
{
    public const string KeyProperty = "SealKeyId";
    public const string SignatureProperty = "SealSignature";

    public override InterceptionResult<int> SavingChanges(DbContextEventData eventData, InterceptionResult<int> result)
    {
        Seal(eventData.Context);
        return result;
    }

    public override ValueTask<InterceptionResult<int>> SavingChangesAsync(DbContextEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Seal(eventData.Context);
        return ValueTask.FromResult(result);
    }

    private void Seal(DbContext? context)
    {
        if (context is null) return;
        ECDsa? signer = null;
        try
        {
            foreach (var entry in context.ChangeTracker.Entries<AuditEvent>())
            {
                if (entry.State != EntityState.Added) continue;
                if (signer is null)
                {
                    signer = ECDsa.Create();
                    signer.ImportFromPem(privateKeyPem);
                }
                entry.Property(KeyProperty).CurrentValue = keyId;
                entry.Property(SignatureProperty).CurrentValue = Convert.ToBase64String(signer.SignData(Canonical(entry.Entity), HashAlgorithmName.SHA256));
            }
        }
        finally { signer?.Dispose(); }
    }

    public static byte[] Canonical(AuditEvent record)
    {
        using var details = JsonDocument.Parse(record.DetailsJson);
        return JsonSerializer.SerializeToUtf8Bytes(new
        {
            record.Id, AtMicroseconds = (record.At.UtcTicks - DateTimeOffset.UnixEpoch.UtcTicks) / 10,
            ActorUserId = record.ActorUserId.Value, record.ActorKind, record.Action,
            TenantId = record.TenantId?.Value, record.TargetType, record.TargetId, record.SupportSessionId,
            Details = details.RootElement.EnumerateObject().OrderBy(property => property.Name, StringComparer.Ordinal).Select(property => new
            {
                property.Name, property.Value.ValueKind,
                Value = property.Value.ValueKind switch
                {
                    JsonValueKind.Number => property.Value.GetDecimal().ToString("G29", System.Globalization.CultureInfo.InvariantCulture),
                    JsonValueKind.String => property.Value.GetString(),
                    _ => property.Value.GetRawText()
                }
            })
        });
    }

    public static bool Verify(AuditEvent record, string signature, string publicKeyPem)
    {
        try
        {
            using var verifier = ECDsa.Create();
            verifier.ImportFromPem(publicKeyPem);
            return verifier.VerifyData(Canonical(record), Convert.FromBase64String(signature), HashAlgorithmName.SHA256);
        }
        catch (Exception exception) when (exception is ArgumentException or CryptographicException or FormatException) { return false; }
    }
}
