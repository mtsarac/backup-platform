using BackupPlatform.Domain.Backups;
using BackupPlatform.Domain.Identity;

namespace BackupPlatform.UnitTests;

/// <summary>Builders that drive a <see cref="BackupRun"/> through its public domain API only.</summary>
internal static class BackupRunTestSupport
{
    public static readonly DateTimeOffset Now = new(2026, 10, 5, 0, 0, 0, TimeSpan.Zero);
    public static readonly TimeSpan Lease = TimeSpan.FromMinutes(2);
    private static readonly string SourceHash = new('a', 64);
    private static readonly string ArtifactHash = new('b', 64);

    public static BackupConfiguration Configuration() => new("mssql", "Database", "localhost", null, true, null,
        true, false, "default", "", true, "Preferred", "Preferred")
        { Destinations = [new BackupDestination("local", null, true) { StorageExecutionLocation = BackupStorageExecutionLocation.Backend }] };

    /// <summary>A run with a deterministic policy: 10s initial delay, doubling, no jitter, 10 minute cap.</summary>
    public static BackupRetryPolicy Policy(int maxAttempts = 3, BackupBackoffStrategy backoff = BackupBackoffStrategy.Exponential, double jitter = 0) => new()
    {
        MaxAttempts = maxAttempts,
        InitialDelay = TimeSpan.FromSeconds(10),
        MaxDelay = TimeSpan.FromMinutes(10),
        Backoff = backoff,
        Jitter = jitter
    };

    public static BackupRun NewRun(BackupRetryPolicy? policy = null, BackupOverlapPolicy overlap = BackupOverlapPolicy.Skip, DateTimeOffset? at = null)
    {
        var created = at ?? Now;
        var run = new BackupRun(Guid.NewGuid(), TenantId.New(), Guid.NewGuid(), AgentId.New(), Configuration(), "key", created, policy ?? Policy(), overlap);
        run.QueueFirstAttempt(Guid.NewGuid(), created);
        return run;
    }

    /// <summary>Claims and starts the run's current (Pending) attempt at <paramref name="at"/>; returns the lease token.</summary>
    public static Guid Begin(BackupRun run, DateTimeOffset at)
    {
        var token = Guid.NewGuid();
        run.Claim(token, at, Lease);
        run.Start(token, at, "1.0", "1.0", "Linux");
        return token;
    }

    /// <summary>Runs the current attempt to a Failed outcome with <paramref name="code"/> at <paramref name="at"/>.</summary>
    public static Guid Fail(BackupRun run, string code, DateTimeOffset at, double jitter = 0)
    {
        var token = Begin(run, at);
        run.Complete(token, at, new BackupCompletion(BackupStatus.Failed, code, null), jitter);
        return token;
    }

    public static void Succeed(BackupRun run, Guid token, DateTimeOffset at)
    {
        var artifact = new BackupArtifact(Guid.NewGuid(), run.TenantId, run.CurrentAttempt.Id, 100, SourceHash, ArtifactHash, "sha256", at, "{}", "{}");
        artifact.AddReplica(new BackupReplica(Guid.NewGuid(), artifact.TenantId, artifact.Id, "local", "default", "backup-key", 100, ArtifactHash,
            "sha256", "Verified", "ReadBack", at, at, executionLocation: BackupStorageExecutionLocation.Backend));
        run.Complete(token, at, new BackupCompletion(BackupStatus.Succeeded, null, artifact));
    }
}

