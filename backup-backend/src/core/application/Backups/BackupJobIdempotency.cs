using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using BackupPlatform.Contracts.Backups;
using BackupPlatform.Domain.Identity;

namespace BackupPlatform.Application.Backups;

/// <summary>The Idempotency-Key of a job creation and the digest of the request it was sent with.</summary>
public sealed record BackupJobIdempotency(string Key, string Fingerprint);

public enum BackupJobCreateOutcome { Created, Replayed, Conflict, NotFound }

/// <summary><see cref="Job"/> is the created job, or the EXISTING job on a replay or a conflict (never leaked to a conflicting caller).</summary>
public sealed record BackupJobCreation(BackupJobCreateOutcome Outcome, BackupJobView? Job);

/// <summary>The secret that keys job-creation fingerprints. One value for every API node and every restart (it lives in the shared database, protected by Data Protection).</summary>
public interface IIdempotencyFingerprintKey
{
    ValueTask<byte[]> GetAsync(CancellationToken cancellationToken);
}

/// <summary>
/// Keyed digest (HMAC-SHA256) of a normalised job creation request, a typed database login included. The key is a platform secret, so a leaked job row
/// lets nobody test password guesses offline; without the key the digest is just 64 opaque hex characters. The Idempotency-Key is part of the input, the
/// tenant too, so one tenant's digest never equals another's.
/// </summary>
public static class BackupJobFingerprint
{
    public static BackupJobIdempotency Compute(byte[] key, TenantId tenantId, string idempotencyKey, CreateBackupJobRequest request)
    {
        var login = request.Configuration.DatabaseLogin;
        var canonical = JsonSerializer.Serialize(new
        {
            Name = request.Name?.Trim(), request.AgentId, Configuration = request.Configuration with { DatabaseLogin = null },
            Login = login is null ? null : new { UserName = login.UserName.Trim(), login.Password },
            request.RetryPolicy, request.OverlapPolicy, request.Schedule, request.Retention, request.Enabled
        });
        var input = Encoding.UTF8.GetBytes($"backup-job-request-v2:{tenantId.Value:N}:{idempotencyKey}:{canonical}");
        return new(idempotencyKey, Convert.ToHexStringLower(HMACSHA256.HashData(key, input)));
    }
}
