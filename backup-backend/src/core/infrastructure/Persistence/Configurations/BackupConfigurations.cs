using System.Text.Json;
using BackupPlatform.Domain.Agents;
using BackupPlatform.Domain.Backups;
using BackupPlatform.Domain.Identity;
using BackupPlatform.Domain.Tenancy;
using BackupPlatform.Infrastructure.Persistence.Conversions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace BackupPlatform.Infrastructure.Persistence.Configurations;

internal static class BackupMapping
{
    internal static void Base<T>(EntityTypeBuilder<T> builder, string table) where T : class, ITenantOwned
    {
        builder.ToTable(table, constraints =>
        {
            constraints.HasCheckConstraint($"ck_{table}_id", "\"Id\" <> '00000000-0000-0000-0000-000000000000'::uuid");
            constraints.HasCheckConstraint($"ck_{table}_tenant", "\"TenantId\" <> '00000000-0000-0000-0000-000000000000'::uuid");
        });
        builder.HasKey("Id");
        builder.Property<Guid>("Id").ValueGeneratedNever();
        builder.Property(entity => entity.TenantId).HasConversion<TenantIdConverter>().IsRequired().IsConcurrencyToken();
        builder.HasAlternateKey("Id", "TenantId");
        builder.HasOne<Tenant>().WithMany().HasForeignKey(entity => entity.TenantId).OnDelete(DeleteBehavior.Restrict);
        builder.HasIndex(entity => entity.TenantId);
        builder.Property<uint>("xmin").IsRowVersion();
    }
    internal static void Configuration<T>(EntityTypeBuilder<T> builder) where T : class =>
        builder.Property<BackupConfiguration>("Configuration").HasConversion(
            value => JsonSerializer.Serialize(value, (JsonSerializerOptions?)null),
            value => JsonSerializer.Deserialize<BackupConfiguration>(value, (JsonSerializerOptions?)null)!.Validate()).HasColumnType("jsonb").IsRequired();
    internal static void Agent<T>(EntityTypeBuilder<T> builder) where T : class =>
        builder.Property<AgentId>("AgentId").HasConversion<AgentIdConverter>().IsRequired();
    internal static void Status<T>(EntityTypeBuilder<T> builder, string table, int maximum) where T : class
    {
        builder.Property<BackupStatus>("Status").HasConversion<int>();
        builder.ToTable(table, constraints => constraints.HasCheckConstraint($"ck_{table}_status", $"\"Status\" BETWEEN 1 AND {maximum}"));
    }
    internal static void RetryPolicy<T>(EntityTypeBuilder<T> builder, System.Linq.Expressions.Expression<Func<T, BackupRetryPolicy?>> property) where T : class
    {
        builder.ComplexProperty(property, policy =>
        {
            policy.Property(value => value.MaxAttempts).HasColumnName("RetryMaxAttempts");
            policy.Property(value => value.InitialDelay).HasColumnName("RetryInitialDelay");
            policy.Property(value => value.MaxDelay).HasColumnName("RetryMaxDelay");
            policy.Property(value => value.Backoff).HasColumnName("RetryBackoff").HasConversion<int>();
            policy.Property(value => value.Jitter).HasColumnName("RetryJitter");
        });
        var table = typeof(T) == typeof(BackupJob) ? "backup_jobs" : "backup_runs";
        builder.ToTable(table, constraints => constraints.HasCheckConstraint($"ck_{table}_retry_policy",
            "\"RetryMaxAttempts\" BETWEEN 1 AND 10 AND \"RetryInitialDelay\" >= interval '1 second' AND \"RetryMaxDelay\" >= \"RetryInitialDelay\" " +
            "AND \"RetryMaxDelay\" <= interval '24 hours' AND \"RetryBackoff\" IN (1,2) AND \"RetryJitter\" BETWEEN 0 AND 1"));
    }
}

internal sealed class BackupJobConfiguration : IEntityTypeConfiguration<BackupJob>
{
    public void Configure(EntityTypeBuilder<BackupJob> builder)
    {
        BackupMapping.Base(builder, "backup_jobs");
        BackupMapping.Agent(builder);
        BackupMapping.Configuration(builder);
        builder.Property(job => job.Name).HasMaxLength(200).IsRequired();
        BackupMapping.RetryPolicy(builder, job => job.RetryPolicy);
        builder.Property(job => job.OverlapPolicy).HasConversion<string>().HasMaxLength(16).IsRequired();
        builder.ToTable("backup_jobs", table =>
            table.HasCheckConstraint("ck_backup_job_policy", "\"BackupType\" = 'Full' AND \"OverlapPolicy\" IN ('Skip','Queue')"));
        builder.HasOne<Agent>().WithMany().HasForeignKey(job => new { job.AgentId, job.TenantId })
            .HasPrincipalKey(agent => new { agent.Id, agent.TenantId }).OnDelete(DeleteBehavior.Restrict);
        builder.HasIndex(job => new { job.TenantId, job.AgentId });
    }
}

internal sealed class BackupRunConfiguration : IEntityTypeConfiguration<BackupRun>
{
    public void Configure(EntityTypeBuilder<BackupRun> builder)
    {
        BackupMapping.Base(builder, "backup_runs");
        BackupMapping.Agent(builder);
        BackupMapping.Configuration(builder);
        BackupMapping.Status(builder, "backup_runs", 8);
        BackupMapping.RetryPolicy(builder, run => run.RetryPolicy);
        builder.ToTable("backup_runs", table =>
        {
            table.HasCheckConstraint("ck_backup_runs_attempt_number", "\"CurrentAttemptNumber\" >= 1");
            table.HasCheckConstraint("ck_backup_runs_attempt_budget", "\"CurrentAttemptNumber\" <= \"RetryMaxAttempts\"");
            table.HasCheckConstraint("ck_backup_runs_type", "\"BackupType\" = 'Full' AND \"TriggerType\" = 'Manual'");
            table.HasCheckConstraint("ck_backup_runs_terminal", "(\"Status\" IN (4,5,6,7)) = (\"CompletedAt\" IS NOT NULL)");
            table.HasCheckConstraint("ck_backup_runs_retry_schedule", "(\"Status\" = 8) = (\"NextAttemptAt\" IS NOT NULL)");
            table.HasCheckConstraint("ck_backup_runs_retry_state", "\"RetryState\" BETWEEN 0 AND 3 AND ((\"Status\" = 8) = (\"RetryState\" = 1))");
            table.HasCheckConstraint("ck_backup_runs_overlap", "\"OverlapPolicy\" IN ('Skip','Queue')");
        });
        builder.Property(run => run.IdempotencyKey).HasMaxLength(128).IsRequired();
        builder.Property(run => run.TerminalReason).HasMaxLength(128);
        builder.Property(run => run.OverlapPolicy).HasConversion<string>().HasMaxLength(16).IsRequired();
        builder.Property(run => run.RetryState).HasConversion<int>();
        builder.Ignore(run => run.CurrentAttempt);
        builder.Ignore(run => run.IsTerminal);
        builder.Ignore(run => run.IsActive);
        builder.HasOne<BackupJob>().WithMany().HasForeignKey(run => new { run.JobId, run.TenantId })
            .HasPrincipalKey(job => new { job.Id, job.TenantId }).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<Agent>().WithMany().HasForeignKey(run => new { run.AgentId, run.TenantId })
            .HasPrincipalKey(agent => new { agent.Id, agent.TenantId }).OnDelete(DeleteBehavior.Restrict);
        builder.HasMany(run => run.Attempts).WithOne().HasForeignKey(attempt => new { attempt.RunId, attempt.TenantId })
            .HasPrincipalKey(run => new { run.Id, run.TenantId }).OnDelete(DeleteBehavior.Restrict);
        builder.Navigation(run => run.Attempts).HasField("_attempts").UsePropertyAccessMode(PropertyAccessMode.Field);
        builder.HasMany(run => run.Facts).WithOne().HasForeignKey(fact => new { fact.RunId, fact.TenantId })
            .HasPrincipalKey(run => new { run.Id, run.TenantId }).OnDelete(DeleteBehavior.Restrict);
        builder.Navigation(run => run.Facts).HasField("_facts").UsePropertyAccessMode(PropertyAccessMode.Field);
        builder.HasIndex(run => new { run.TenantId, run.JobId, run.IdempotencyKey }).IsUnique();
        builder.HasIndex(run => new { run.TenantId, run.AgentId, run.Status, run.CreatedAt });
        builder.HasIndex(run => new { run.TenantId, run.CreatedAt });
        builder.HasIndex(run => new { run.TenantId, run.Status, run.CreatedAt });
        builder.HasIndex(run => new { run.TenantId, run.JobId, run.CreatedAt });
        // Backstop for the Skip overlap policy: at most one active run per job whatever the application does.
        builder.HasIndex(run => new { run.TenantId, run.JobId }).IsUnique().HasFilter("\"OverlapPolicy\" = 'Skip' AND \"Status\" IN (1,2,3,8)");
        // The retry worker's only scan: due RetryPending runs, oldest due first.
        builder.HasIndex(run => new { run.NextAttemptAt, run.Id }).HasFilter("\"Status\" = 8");
    }
}

internal sealed class BackupAttemptConfiguration : IEntityTypeConfiguration<BackupRunAttempt>
{
    public void Configure(EntityTypeBuilder<BackupRunAttempt> builder)
    {
        BackupMapping.Base(builder, "backup_run_attempts");
        BackupMapping.Agent(builder);
        BackupMapping.Status(builder, "backup_run_attempts", 7);
        builder.ToTable("backup_run_attempts", table =>
        {
            table.HasCheckConstraint("ck_backup_attempt_number", "\"Number\" >= 1");
            table.HasCheckConstraint("ck_backup_attempt_lease", "\"Status\" NOT IN (2,3) OR (\"LeaseToken\" IS NOT NULL AND \"LeaseToken\" <> '00000000-0000-0000-0000-000000000000'::uuid AND \"LeaseExpiresAt\" IS NOT NULL)");
            table.HasCheckConstraint("ck_backup_attempt_progress", "\"ProgressPercent\" IS NULL OR (\"ProgressPercent\" >= 0 AND \"ProgressPercent\" <= 100)");
            table.HasCheckConstraint("ck_backup_attempt_terminal", "(\"Status\" IN (4,5,6,7)) = (\"CompletedAt\" IS NOT NULL)");
            table.HasCheckConstraint("ck_backup_attempt_started", "\"Status\" <> 3 OR \"StartedAt\" IS NOT NULL");
        });
        builder.Ignore(attempt => attempt.IsTerminal);
        builder.Property(attempt => attempt.SoftwareVersion).HasMaxLength(64);
        builder.Property(attempt => attempt.ProtocolVersion).HasMaxLength(32);
        builder.Property(attempt => attempt.Platform).HasMaxLength(32);
        builder.Property(attempt => attempt.ProgressStage).HasMaxLength(64);
        builder.Property(attempt => attempt.TerminalReason).HasMaxLength(128);
        builder.HasOne<Agent>().WithMany().HasForeignKey(attempt => new { attempt.AgentId, attempt.TenantId })
            .HasPrincipalKey(agent => new { agent.Id, agent.TenantId }).OnDelete(DeleteBehavior.Restrict);
        builder.HasMany(attempt => attempt.Artifacts).WithOne().HasForeignKey(artifact => new { artifact.AttemptId, artifact.TenantId })
            .HasPrincipalKey(attempt => new { attempt.Id, attempt.TenantId }).OnDelete(DeleteBehavior.Restrict);
        builder.Navigation(attempt => attempt.Artifacts).HasField("_artifacts").UsePropertyAccessMode(PropertyAccessMode.Field);
        builder.HasIndex(attempt => new { attempt.TenantId, attempt.RunId, attempt.Number }).IsUnique();
        // Concurrency is enforced by counting these rows under a lock (agent, tenant and global limits), not by a unique index.
        builder.HasIndex(attempt => new { attempt.TenantId, attempt.AgentId, attempt.Status });
        builder.HasIndex(attempt => attempt.LeaseToken).IsUnique().HasFilter("\"LeaseToken\" IS NOT NULL");
        builder.HasIndex(attempt => new { attempt.Status, attempt.LeaseExpiresAt });
        builder.HasIndex(attempt => new { attempt.TenantId, attempt.AgentId, attempt.EligibleAt }).HasFilter("\"Status\" = 1");
    }
}

internal sealed class BackupArtifactConfiguration : IEntityTypeConfiguration<BackupArtifact>
{
    public void Configure(EntityTypeBuilder<BackupArtifact> builder)
    {
        BackupMapping.Base(builder, "backup_artifacts");
        builder.ToTable("backup_artifacts", table =>
        {
            table.HasCheckConstraint("ck_backup_artifact_size", "\"SizeBytes\" > 0");
            table.HasCheckConstraint("ck_backup_artifact_type", "\"BackupType\" = 'Full'");
            table.HasCheckConstraint("ck_backup_artifact_checksums", "\"ChecksumAlgorithm\" = 'sha256' AND \"SourceChecksum\" ~ '^[0-9a-f]{64}$' AND \"ArtifactChecksum\" ~ '^[0-9a-f]{64}$'");
        });
        builder.Property(artifact => artifact.SourceChecksum).HasMaxLength(64).IsRequired();
        builder.Property(artifact => artifact.ArtifactChecksum).HasMaxLength(64).IsRequired();
        builder.Property(artifact => artifact.ChecksumAlgorithm).HasMaxLength(16).IsRequired();
        builder.Property(artifact => artifact.SourceMetadataJson).HasMaxLength(16384).IsRequired();
        builder.Property(artifact => artifact.AppliedOptionsJson).HasMaxLength(16384).IsRequired();
        builder.Property<bool>("_sealed").HasColumnName("Sealed");
        builder.HasIndex(artifact => new { artifact.TenantId, artifact.AttemptId }).IsUnique();
        builder.HasMany(artifact => artifact.Replicas).WithOne()
            .HasForeignKey(replica => new { replica.ArtifactId, replica.TenantId, replica.SizeBytes, replica.Checksum, replica.ChecksumAlgorithm })
            .HasPrincipalKey(artifact => new { artifact.Id, artifact.TenantId, artifact.SizeBytes, artifact.ArtifactChecksum, artifact.ChecksumAlgorithm })
            .OnDelete(DeleteBehavior.Restrict);
        builder.Navigation(artifact => artifact.Replicas).HasField("_replicas").UsePropertyAccessMode(PropertyAccessMode.Field);
    }
}

internal sealed class BackupReplicaConfiguration : IEntityTypeConfiguration<BackupReplica>
{
    public void Configure(EntityTypeBuilder<BackupReplica> builder)
    {
        BackupMapping.Base(builder, "backup_replicas");
        builder.ToTable("backup_replicas", table =>
        {
            table.HasCheckConstraint("ck_backup_replica_size", "\"SizeBytes\" > 0");
            table.HasCheckConstraint("ck_backup_replica_status", "\"Status\" = 'Available'");
            table.HasCheckConstraint("ck_backup_replica_checksum", "\"ChecksumAlgorithm\" = 'sha256' AND \"Checksum\" ~ '^[0-9a-f]{64}$'");
            table.HasCheckConstraint("ck_backup_replica_verification", "\"VerificationState\" IN ('Verified','Unverified','Failed') AND ((\"VerificationState\" = 'Verified') = (\"VerifiedAt\" IS NOT NULL)) AND (\"VerifiedAt\" IS NULL OR \"VerifiedAt\" >= \"CreatedAt\")");
        });
        builder.Property(replica => replica.StorageProvider).HasMaxLength(128).IsRequired();
        builder.Property(replica => replica.StorageTarget).HasMaxLength(512).IsRequired();
        builder.Property(replica => replica.Key).HasMaxLength(1024).IsRequired();
        builder.Property(replica => replica.Checksum).HasMaxLength(64).IsRequired();
        builder.Property(replica => replica.ChecksumAlgorithm).HasMaxLength(16).IsRequired();
        builder.Property(replica => replica.VerificationState).HasMaxLength(32).IsRequired();
        builder.Property(replica => replica.VerificationMethod).HasMaxLength(64).IsRequired();
        builder.HasIndex(replica => new { replica.TenantId, replica.ArtifactId, replica.StorageProvider, replica.StorageTarget, replica.Key }).IsUnique();
    }
}

internal sealed class BackupFactConfiguration : IEntityTypeConfiguration<BackupLifecycleFact>
{
    public void Configure(EntityTypeBuilder<BackupLifecycleFact> builder)
    {
        BackupMapping.Base(builder, "backup_lifecycle_facts");
        builder.Property(fact => fact.Kind).HasMaxLength(64).IsRequired();
        builder.HasOne<BackupRunAttempt>().WithMany().HasForeignKey(fact => new { fact.AttemptId, fact.TenantId })
            .HasPrincipalKey(attempt => new { attempt.Id, attempt.TenantId }).OnDelete(DeleteBehavior.Restrict);
        builder.HasIndex(fact => new { fact.TenantId, fact.RunId, fact.At });
    }
}
