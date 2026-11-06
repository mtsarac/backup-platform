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

/// <summary>
/// Digest of a normalised job creation request. A typed database login enters only as a SHA-256 salted with tenant and key, so the digest never holds a
/// plaintext secret (and the stored job row holds no password-derived value that works without them).
/// </summary>
public static class BackupJobFingerprint
{
    public static BackupJobIdempotency Compute(TenantId tenantId, string key, CreateBackupJobRequest request)
    {
        var login = request.Configuration.DatabaseLogin;
        var loginDigest = login is null ? null : Hex(SHA256.HashData(Encoding.UTF8.GetBytes($"job-login-v1:{tenantId.Value:N}:{key}:{login.UserName.Trim()}\0{login.Password}")));
        var canonical = JsonSerializer.Serialize(new
        {
            Name = request.Name?.Trim(), request.AgentId, Configuration = request.Configuration with { DatabaseLogin = null }, LoginDigest = loginDigest,
            request.RetryPolicy, request.OverlapPolicy, request.Schedule, request.Retention, request.Enabled
        });
        return new(key, Hex(SHA256.HashData(Encoding.UTF8.GetBytes("backup-job-request-v1:" + canonical))));
    }

    private static string Hex(byte[] bytes) => Convert.ToHexStringLower(bytes);
}
