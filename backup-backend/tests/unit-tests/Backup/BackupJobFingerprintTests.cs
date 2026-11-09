using BackupPlatform.Application.Backups;
using BackupPlatform.Contracts.Agents;
using BackupPlatform.Contracts.Backups;
using BackupPlatform.Domain.Identity;
using Xunit;

namespace BackupPlatform.UnitTests.Backup;

public sealed class BackupJobFingerprintTests
{
    private static readonly byte[] KeyA = Enumerable.Range(1, 32).Select(i => (byte)i).ToArray();
    private static readonly byte[] KeyB = Enumerable.Range(2, 32).Select(i => (byte)i).ToArray();
    private static readonly TenantId Tenant = TenantId.New();

    private static CreateBackupJobRequest Request(string password = "pw", string name = "Job") => new(name, Guid.Parse("11111111-1111-1111-1111-111111111111"),
        new BackupExecutionConfiguration { Database = "db", Host = "h", IntegratedSecurity = false, DatabaseLogin = new DatabaseLoginInput("sa", password) });

    [Fact]
    public void The_same_key_and_request_give_the_same_digest_and_it_is_64_hex_characters()
    {
        var first = BackupJobFingerprint.Compute(KeyA, Tenant, "k", Request()).Fingerprint;
        Assert.Equal(first, BackupJobFingerprint.Compute(KeyA, Tenant, "k", Request()).Fingerprint);
        Assert.Matches("^[0-9a-f]{64}$", first);
    }

    [Fact]
    public void Another_secret_key_gives_another_digest_so_a_leaked_row_cannot_be_used_to_test_password_guesses()
    {
        var withA = BackupJobFingerprint.Compute(KeyA, Tenant, "k", Request()).Fingerprint;
        Assert.NotEqual(withA, BackupJobFingerprint.Compute(KeyB, Tenant, "k", Request()).Fingerprint);
        // Not a plain digest of the public inputs either: an attacker who knows tenant, key and a guessed password cannot reproduce it without the key.
        var unkeyed = Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes($"job-login-v1:{Tenant.Value:N}:k:sa\0pw")));
        Assert.NotEqual(unkeyed, withA);
    }

    [Fact]
    public void Every_real_difference_changes_the_digest_and_the_tenant_and_the_key_are_part_of_it()
    {
        var baseline = BackupJobFingerprint.Compute(KeyA, Tenant, "k", Request()).Fingerprint;
        Assert.NotEqual(baseline, BackupJobFingerprint.Compute(KeyA, Tenant, "k", Request(password: "other")).Fingerprint);
        Assert.NotEqual(baseline, BackupJobFingerprint.Compute(KeyA, Tenant, "k", Request(name: "Other")).Fingerprint);
        Assert.NotEqual(baseline, BackupJobFingerprint.Compute(KeyA, Tenant, "k2", Request()).Fingerprint);
        Assert.NotEqual(baseline, BackupJobFingerprint.Compute(KeyA, TenantId.New(), "k", Request()).Fingerprint);
        Assert.Equal(baseline, BackupJobFingerprint.Compute(KeyA, Tenant, "k", Request(name: "  Job  ")).Fingerprint);
    }
}
