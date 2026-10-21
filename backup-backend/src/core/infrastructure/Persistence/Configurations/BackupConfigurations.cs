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
    internal static void RetentionPolicy<T>(EntityTypeBuilder<T> builder, System.Linq.Expressions.Expression<Func<T, BackupRetentionPolicy?>> property) where T : class
    {
        builder.ComplexProperty(property, policy =>
        {
            policy.Property(value => value.Enabled).HasColumnName("RetentionEnabled");
            policy.Property(value => value.KeepFor).HasColumnName("RetentionKeepFor");
            policy.Property(value => value.DeleteGracePeriod).HasColumnName("RetentionDeleteGrace");
        });
        var table = typeof(T) == typeof(BackupJob) ? "backup_jobs" : "backup_runs";
        builder.ToTable(table, constraints => constraints.HasCheckConstraint($"ck_{table}_retention_policy",
            "\"RetentionKeepFor\" BETWEEN interval '1 second' AND interval '3650 days' AND \"RetentionDeleteGrace\" BETWEEN interval '1 second' AND interval '365 days'"));
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
        BackupMapping.RetentionPolicy(builder, job => job.RetentionPolicy);
        builder.Property(job => job.OverlapPolicy).HasConversion<string>().HasMaxLength(16).IsRequired();
        builder.ComplexProperty(job => job.Schedule, schedule =>
        {
            schedule.Property(value => value.Enabled).HasColumnName("ScheduleEnabled");
            schedule.Property(value => value.CronExpression).HasColumnName("ScheduleCron").HasMaxLength(128);
            schedule.Property(value => value.TimeZoneId).HasColumnName("ScheduleTimeZone").HasMaxLength(64);
            schedule.Property(value => value.MissedRunGracePeriod).HasColumnName("ScheduleMissedRunGrace");
            schedule.Property(value => value.EffectiveFrom).HasColumnName("ScheduleEffectiveFrom");
        });
        builder.Ignore(job => job.IsScheduled);
        builder.ToTable("backup_jobs", table =>
        {
            table.HasCheckConstraint("ck_backup_job_policy", "\"BackupType\" = 'Full' AND \"OverlapPolicy\" IN ('Skip','Queue')");
            // The schedule is all-or-nothing and its grace period is bounded (1 minute .. 7 days).
            table.HasCheckConstraint("ck_backup_jobs_schedule", "(\"ScheduleEnabled\" IS NULL AND \"ScheduleCron\" IS NULL AND \"ScheduleTimeZone\" IS NULL AND \"ScheduleMissedRunGrace\" IS NULL AND \"ScheduleEffectiveFrom\" IS NULL) " +
                "OR (\"ScheduleEnabled\" IS NOT NULL AND \"ScheduleCron\" IS NOT NULL AND \"ScheduleTimeZone\" IS NOT NULL AND \"ScheduleMissedRunGrace\" IS NOT NULL AND \"ScheduleEffectiveFrom\" IS NOT NULL " +
                "AND \"ScheduleMissedRunGrace\" BETWEEN interval '1 minute' AND interval '7 days')");
        });
        builder.HasOne<Agent>().WithMany().HasForeignKey(job => new { job.AgentId, job.TenantId })
            .HasPrincipalKey(agent => new { agent.Id, agent.TenantId }).OnDelete(DeleteBehavior.Restrict);
        builder.HasIndex(job => new { job.TenantId, job.AgentId });
        // The sweep and the reconciliation only ever look at jobs that are switched on.
        builder.HasIndex("TenantId", "Id").HasFilter("\"Enabled\" AND \"ScheduleEnabled\"").HasDatabaseName("ix_backup_jobs_scheduled");
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
        BackupMapping.RetentionPolicy(builder, run => run.RetentionPolicy);
        builder.ToTable("backup_runs", table =>
        {
            table.HasCheckConstraint("ck_backup_runs_attempt_number", "\"CurrentAttemptNumber\" >= 1");
            table.HasCheckConstraint("ck_backup_runs_attempt_budget", "\"CurrentAttemptNumber\" <= \"RetryMaxAttempts\"");
            table.HasCheckConstraint("ck_backup_runs_type", "\"BackupType\" = 'Full'");
            table.HasCheckConstraint("ck_backup_runs_trigger", "(\"TriggerType\" = 'Manual' AND \"ScheduledOccurrence\" IS NULL AND \"ClaimDeadline\" IS NULL) " +
                "OR (\"TriggerType\" = 'Scheduled' AND \"ScheduledOccurrence\" IS NOT NULL AND \"ClaimDeadline\" IS NOT NULL AND \"ClaimDeadline\" >= \"ScheduledOccurrence\")");
            table.HasCheckConstraint("ck_backup_runs_terminal", "(\"Status\" IN (4,5,6,7)) = (\"CompletedAt\" IS NOT NULL)");
            table.HasCheckConstraint("ck_backup_runs_retry_schedule", "(\"Status\" = 8) = (\"NextAttemptAt\" IS NOT NULL)");
            table.HasCheckConstraint("ck_backup_runs_retry_state", "\"RetryState\" BETWEEN 0 AND 3 AND ((\"Status\" = 8) = (\"RetryState\" = 1))");
            table.HasCheckConstraint("ck_backup_runs_overlap", "\"OverlapPolicy\" IN ('Skip','Queue')");
        });
        builder.Property(run => run.IdempotencyKey).HasMaxLength(128).IsRequired();
        builder.Property(run => run.TriggerType).HasMaxLength(16).IsRequired();
        builder.Property(run => run.TerminalReason).HasMaxLength(128);
        builder.Property(run => run.OverlapPolicy).HasConversion<string>().HasMaxLength(16).IsRequired();
        builder.Property(run => run.RetryState).HasConversion<int>();
        builder.Ignore(run => run.IsSizeAnomaly);
        builder.Property(run => run.SizeAnomalyStatus).HasMaxLength(32);
        builder.Property(run => run.SizeAnomalyDeviationRatio);
        builder.Property(run => run.SizeAnomalyBaselineBytes);
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
        // One run per scheduled instant of a job, whichever node, wake-up refire or reconciliation tries to create it.
        builder.HasIndex(run => new { run.TenantId, run.JobId, run.ScheduledOccurrence }).IsUnique().HasFilter("\"ScheduledOccurrence\" IS NOT NULL");
        // The unclaimed-run sweep: pending scheduled runs, earliest deadline first.
        builder.HasIndex(run => new { run.ClaimDeadline, run.Id }).HasFilter("\"Status\" = 1 AND \"ClaimDeadline\" IS NOT NULL");
        builder.HasIndex(run => new { run.TenantId, run.SizeAnomalyStatus, run.CompletedAt });
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
        builder.HasMany(attempt => attempt.DestinationOutcomes).WithOne().HasForeignKey(outcome => new { outcome.AttemptId, outcome.TenantId })
            .HasPrincipalKey(attempt => new { attempt.Id, attempt.TenantId }).OnDelete(DeleteBehavior.Restrict);
        builder.Navigation(attempt => attempt.DestinationOutcomes).HasField("_outcomes").UsePropertyAccessMode(PropertyAccessMode.Field);
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
            // Retention is fixed when the artifact is accepted: an expiration needs the acceptance it was counted from, and the grace period is bounded.
            table.HasCheckConstraint("ck_backup_artifact_retention", "\"RetentionGracePeriod\" BETWEEN interval '1 second' AND interval '365 days' " +
                "AND (\"RetentionExpiresAt\" IS NULL OR (\"AcceptedAt\" IS NOT NULL AND \"RetentionExpiresAt\" > \"AcceptedAt\"))");
            table.HasCheckConstraint("ck_backup_artifact_encryption", "(\"Format\" = 'v1-plain' AND \"EncryptionKeyId\" IS NULL AND \"EncryptionKeyVersion\" IS NULL AND \"EncryptionWrappedDekBase64\" IS NULL AND \"EncryptionRecipientsJson\" IS NULL) OR " +
                "(\"Format\" = 'v2-bpe' AND \"EncryptionKeyId\" IS NOT NULL AND \"EncryptionKeyVersion\" >= 1 AND \"EncryptionWrappedDekBase64\" IS NOT NULL)");
        });
        builder.Property(artifact => artifact.SourceChecksum).HasMaxLength(64).IsRequired();
        builder.Property(artifact => artifact.ArtifactChecksum).HasMaxLength(64).IsRequired();
        builder.Property(artifact => artifact.ChecksumAlgorithm).HasMaxLength(16).IsRequired();
        builder.Property(artifact => artifact.SourceMetadataJson).HasMaxLength(16384).IsRequired();
        builder.Property(artifact => artifact.AppliedOptionsJson).HasMaxLength(16384).IsRequired();
        builder.Property(artifact => artifact.Format).HasMaxLength(16).HasDefaultValue(BackupArtifact.PlainFormat).IsRequired();
        builder.Property(artifact => artifact.EncryptionKeyId).HasMaxLength(32);
        builder.Property(artifact => artifact.EncryptionWrappedDekBase64).HasMaxLength(4096);
        builder.Property(artifact => artifact.EncryptionRecipientsJson).HasMaxLength(2048);
        builder.Property<bool>("_sealed").HasColumnName("Sealed");
        builder.Property(artifact => artifact.RetentionGracePeriod).HasDefaultValue(TimeSpan.FromHours(24));
        builder.Ignore(artifact => artifact.State);
        // The retention scan's only query: expired artifacts nobody pinned and retention has not handled yet, earliest expiry first.
        builder.HasIndex(artifact => artifact.RetentionExpiresAt).HasDatabaseName("ix_backup_artifacts_retention_due")
            .HasFilter("\"RetentionExpiresAt\" IS NOT NULL AND \"RetentionPinnedAt\" IS NULL AND \"RetentionAppliedAt\" IS NULL");
        builder.HasIndex(artifact => new { artifact.TenantId, artifact.AttemptId }).IsUnique();
        builder.HasMany(artifact => artifact.Replicas).WithOne()
            .HasForeignKey(replica => new { replica.ArtifactId, replica.TenantId, replica.SizeBytes, replica.Checksum, replica.ChecksumAlgorithm })
            .HasPrincipalKey(artifact => new { artifact.Id, artifact.TenantId, artifact.SizeBytes, artifact.ArtifactChecksum, artifact.ChecksumAlgorithm })
            .OnDelete(DeleteBehavior.Restrict);
        builder.Navigation(artifact => artifact.Replicas).HasField("_replicas").UsePropertyAccessMode(PropertyAccessMode.Field);
    }
}

internal sealed class TenantWrapKeyConfiguration : IEntityTypeConfiguration<TenantWrapKey>
{
    public void Configure(EntityTypeBuilder<TenantWrapKey> builder)
    {
        BackupMapping.Base(builder, "tenant_wrap_keys");
        builder.ToTable("tenant_wrap_keys", table =>
        {
            table.HasCheckConstraint("ck_tenant_wrap_keys_version", "\"Version\" >= 1");
            table.HasCheckConstraint("ck_tenant_wrap_keys_state", "\"State\" IN ('Active','DecryptOnly','Retired')");
        });
        builder.Property(key => key.KeyId).HasMaxLength(32).IsRequired();
        builder.Property(key => key.SecretReference).HasMaxLength(128).IsRequired();
        builder.Property(key => key.PublicKeySpki).IsRequired();
        builder.Property(key => key.State).HasConversion<string>().HasMaxLength(16).IsRequired();
        builder.HasIndex(key => new { key.TenantId, key.Version }).IsUnique();
        // At most one key seals new artifacts per tenant, whichever node rotates.
        builder.HasIndex(key => key.TenantId).IsUnique().HasDatabaseName("ux_tenant_wrap_keys_active").HasFilter("\"State\" = 'Active'");
        builder.HasIndex(key => key.KeyId).IsUnique();
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
            table.HasCheckConstraint("ck_backup_replica_status", "\"Status\" IN ('Available','DeletePending','Deleting','DeleteFailed','Deleted','Quarantined')");
            table.HasCheckConstraint("ck_backup_replica_executor",
                "(\"ExecutionLocation\" = 'Backend' AND \"OwningAgentId\" IS NULL AND \"DeleteOperationId\" IS NULL AND \"DeleteCompletionToken\" IS NULL) OR " +
                "(\"ExecutionLocation\" = 'Agent' AND \"StorageProvider\" = 'local' AND \"OwningAgentId\" IS NOT NULL AND \"OwningAgentId\" <> '00000000-0000-0000-0000-000000000000'::uuid)");
            table.HasCheckConstraint("ck_backup_replica_completion",
                "(\"DeleteCompletionToken\" IS NULL AND \"DeleteCompletionAbsenceVerified\" IS NULL AND \"DeleteCompletionFailureCode\" IS NULL) OR " +
                "(\"ExecutionLocation\" = 'Agent' AND \"DeleteOperationId\" IS NOT NULL AND \"DeleteCompletionToken\" IS NOT NULL AND \"DeleteCompletionAbsenceVerified\" IS NOT NULL AND " +
                "((\"Status\" = 'Deleted' AND \"DeleteCompletionAbsenceVerified\" AND \"DeleteCompletionFailureCode\" IS NULL) OR " +
                "(\"Status\" = 'DeleteFailed' AND NOT \"DeleteCompletionAbsenceVerified\" AND \"DeleteCompletionFailureCode\" = \"DeleteFailureCode\")))");
            table.HasCheckConstraint("ck_backup_replica_lifecycle",
                "(\"DeleteReason\" IS NULL OR \"DeleteReason\" IN ('RetentionExpired','Manual')) AND (\"DeleteRequestedByUserId\" IS NULL OR \"DeleteReason\" = 'Manual') AND (" +
                "(\"Status\" IN ('Available','Quarantined') AND \"DeleteRequestedAt\" IS NULL AND \"DeleteReason\" IS NULL AND \"DeleteAfter\" IS NULL AND \"DeleteLeaseToken\" IS NULL AND \"DeleteLeaseOwner\" IS NULL AND \"DeleteLeaseExpiresAt\" IS NULL " +
                "AND \"DeleteNextAttemptAt\" IS NULL AND \"DeleteFailureCode\" IS NULL AND \"DeletedAt\" IS NULL AND \"DeleteAttempts\" = 0) " +
                "OR (\"Status\" = 'DeletePending' AND \"DeleteRequestedAt\" IS NOT NULL AND \"DeleteReason\" IS NOT NULL AND \"DeleteAfter\" IS NOT NULL AND \"DeleteAfter\" >= \"DeleteRequestedAt\" AND \"DeleteLeaseToken\" IS NULL AND \"DeleteLeaseOwner\" IS NULL " +
                "AND \"DeleteLeaseExpiresAt\" IS NULL AND \"DeleteNextAttemptAt\" IS NULL AND \"DeleteFailureCode\" IS NULL AND \"DeletedAt\" IS NULL AND \"DeleteAttempts\" = 0) " +
                "OR (\"Status\" = 'Deleting' AND \"DeleteRequestedAt\" IS NOT NULL AND \"DeleteReason\" IS NOT NULL AND \"DeleteAfter\" IS NOT NULL AND \"DeletedAt\" IS NULL AND (" +
                "(\"DeleteLeaseToken\" IS NOT NULL AND \"DeleteLeaseOwner\" IS NOT NULL AND \"DeleteLeaseExpiresAt\" IS NOT NULL AND \"DeleteNextAttemptAt\" IS NULL AND \"DeleteAttempts\" >= 1 " +
                "AND (\"ExecutionLocation\" = 'Backend' OR (\"DeleteOperationId\" IS NOT NULL AND \"DeleteFailureCode\" IS NULL))) OR " +
                "(\"ExecutionLocation\" = 'Agent' AND \"DeleteOperationId\" IS NOT NULL AND \"DeleteLeaseToken\" IS NULL AND \"DeleteLeaseOwner\" IS NULL AND \"DeleteLeaseExpiresAt\" IS NULL " +
                "AND \"DeleteNextAttemptAt\" IS NOT NULL AND \"DeleteFailureCode\" = 'agent_unavailable' AND \"DeleteAttempts\" >= 0))) " +
                "OR (\"Status\" = 'DeleteFailed' AND \"DeleteRequestedAt\" IS NOT NULL AND \"DeleteReason\" IS NOT NULL AND \"DeleteAfter\" IS NOT NULL AND \"DeleteLeaseToken\" IS NULL AND \"DeleteLeaseOwner\" IS NULL " +
                "AND \"DeleteLeaseExpiresAt\" IS NULL AND (\"ExecutionLocation\" = 'Agent' OR \"DeleteNextAttemptAt\" IS NOT NULL) AND \"DeleteFailureCode\" IS NOT NULL AND \"DeletedAt\" IS NULL AND \"DeleteAttempts\" >= 1) " +
                "OR (\"Status\" = 'Deleted' AND \"DeleteRequestedAt\" IS NOT NULL AND \"DeleteReason\" IS NOT NULL AND \"DeletedAt\" IS NOT NULL AND \"DeleteLeaseToken\" IS NULL AND \"DeleteLeaseOwner\" IS NULL " +
                "AND \"DeleteLeaseExpiresAt\" IS NULL AND \"DeleteNextAttemptAt\" IS NULL AND \"DeleteFailureCode\" IS NULL AND \"DeleteAttempts\" >= 1))");
            table.HasCheckConstraint("ck_backup_replica_checksum", "\"ChecksumAlgorithm\" = 'sha256' AND \"Checksum\" ~ '^[0-9a-f]{64}$'");
            table.HasCheckConstraint("ck_backup_replica_verification", "\"VerificationState\" IN ('Verified','Unverified','Failed') AND ((\"VerificationState\" = 'Verified') = (\"VerifiedAt\" IS NOT NULL)) AND (\"VerifiedAt\" IS NULL OR \"VerifiedAt\" >= \"CreatedAt\")");
        });
        builder.Property(replica => replica.StorageProvider).HasMaxLength(128).IsRequired();
        builder.Property(replica => replica.StorageConnectionId).IsRequired();
        // The destination a copy lives on. Composite so it can only name a connection of the replica's own tenant AND provider type.
        builder.HasOne<BackupPlatform.Domain.Storage.StorageConnection>().WithMany()
            .HasForeignKey(replica => new { replica.StorageConnectionId, replica.TenantId, replica.StorageProvider })
            .HasPrincipalKey(connection => new { connection.Id, connection.TenantId, connection.ProviderType })
            .OnDelete(DeleteBehavior.Restrict);
        builder.HasIndex(replica => new { replica.TenantId, replica.StorageConnectionId }).HasDatabaseName("ix_backup_replicas_connection");
        builder.Property(replica => replica.StorageTarget).HasMaxLength(512).IsRequired();
        builder.Property(replica => replica.ExecutionLocation).HasConversion<string>().HasMaxLength(16).IsRequired();
        builder.Property(replica => replica.Key).HasMaxLength(1024).IsRequired();
        builder.Property(replica => replica.Checksum).HasMaxLength(64).IsRequired();
        builder.Property(replica => replica.ChecksumAlgorithm).HasMaxLength(16).IsRequired();
        builder.Property(replica => replica.VerificationState).HasMaxLength(32).IsRequired();
        builder.Property(replica => replica.VerificationMethod).HasMaxLength(64).IsRequired();
        builder.Property(replica => replica.Status).HasConversion<string>().HasMaxLength(16).IsRequired();
        builder.Property(replica => replica.DeleteReason).HasConversion<string>().HasMaxLength(16);
        builder.Property(replica => replica.DeleteLeaseOwner).HasMaxLength(128);
        builder.Property(replica => replica.DeleteFailureCode).HasMaxLength(128);
        builder.Property(replica => replica.DeleteCompletionFailureCode).HasMaxLength(128);
        builder.Ignore(replica => replica.IsUsable);
        builder.Ignore(replica => replica.WaitingForAgent);
        builder.HasIndex(replica => new { replica.TenantId, replica.OwningAgentId, replica.DeleteAfter })
            .HasDatabaseName("ix_backup_replicas_agent_deletions").HasFilter("\"ExecutionLocation\" = 'Agent' AND \"Status\" = 'Deleting'");
        // The delete workers' claim query is the union of these three: grace period over, failed delete due again, lease of a crashed owner expired.
        builder.HasIndex(replica => replica.DeleteAfter).HasDatabaseName("ix_backup_replicas_delete_pending").HasFilter("\"Status\" = 'DeletePending'");
        builder.HasIndex(replica => replica.DeleteNextAttemptAt).HasDatabaseName("ix_backup_replicas_delete_failed").HasFilter("\"Status\" = 'DeleteFailed'");
        builder.HasIndex(replica => replica.DeleteLeaseExpiresAt).HasDatabaseName("ix_backup_replicas_delete_leased").HasFilter("\"Status\" = 'Deleting'");
        // The orphan sweep asks which stored keys some replica still references.
        builder.HasIndex(replica => new { replica.StorageProvider, replica.Key }).HasDatabaseName("ix_backup_replicas_location");
        builder.HasIndex(replica => new { replica.TenantId, replica.ArtifactId, replica.StorageProvider, replica.StorageTarget, replica.Key }).IsUnique();
        // The same invariant as BackupArtifact.AddReplica, enforced where no application path can bypass it.
        builder.HasIndex(replica => new { replica.TenantId, replica.ArtifactId, replica.StorageConnectionId }).IsUnique().HasDatabaseName("ux_backup_replicas_live_verified_destination")
            .HasFilter("\"VerificationState\" = 'Verified' AND \"Status\" <> 'Deleted'");
    }
}

internal sealed class BackupDestinationOutcomeConfiguration : IEntityTypeConfiguration<BackupDestinationOutcome>
{
    public void Configure(EntityTypeBuilder<BackupDestinationOutcome> builder)
    {
        BackupMapping.Base(builder, "backup_destination_outcomes");
        builder.ToTable("backup_destination_outcomes", table =>
        {
            table.HasCheckConstraint("ck_backup_destination_outcome_state", "\"State\" IN ('Verified','Failed','Cancelled') AND \"Upload\" IN ('Succeeded','Failed','NotAttempted') AND \"Verification\" IN ('Verified','Failed','NotRun')");
            // Verified is the whole story of a stored, verified copy; every other state names a reason and no replica; a possibly-left-behind object was written and did not verify.
            table.HasCheckConstraint("ck_backup_destination_outcome_verified",
                "(\"State\" = 'Verified') = (\"ReplicaId\" IS NOT NULL) " +
                "AND (\"State\" <> 'Verified' OR (\"FailureCode\" IS NULL AND \"Upload\" = 'Succeeded' AND \"Verification\" = 'Verified' AND \"Key\" IS NOT NULL)) " +
                "AND (\"State\" = 'Verified' OR (\"FailureCode\" IS NOT NULL AND \"Verification\" <> 'Verified')) " +
                "AND (NOT \"ObjectMayRemain\" OR (\"State\" <> 'Verified' AND \"Key\" IS NOT NULL AND \"Upload\" = 'Succeeded'))");
            table.HasCheckConstraint("ck_backup_destination_outcome_times", "\"CompletedAt\" >= \"StartedAt\"");
        });
        builder.Property(outcome => outcome.StorageProvider).HasMaxLength(128).IsRequired();
        builder.Property(outcome => outcome.ExecutionLocation).HasConversion<string>().HasMaxLength(16).IsRequired();
        builder.Property(outcome => outcome.State).HasMaxLength(16).IsRequired();
        builder.Property(outcome => outcome.Upload).HasMaxLength(16).IsRequired();
        builder.Property(outcome => outcome.Verification).HasMaxLength(16).IsRequired();
        builder.Property(outcome => outcome.FailureCode).HasMaxLength(128);
        builder.Property(outcome => outcome.Key).HasMaxLength(1024);
        builder.Ignore(outcome => outcome.IsVerified);
        // One outcome per destination per attempt: a duplicate completion can never double-record a destination.
        builder.HasIndex(outcome => new { outcome.TenantId, outcome.AttemptId, outcome.StorageProvider, outcome.StorageConnectionId }).IsUnique();
        builder.HasIndex(outcome => new { outcome.TenantId, outcome.RunId });
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

internal sealed class BackupScheduleOccurrenceConfiguration : IEntityTypeConfiguration<BackupScheduleOccurrence>
{
    public void Configure(EntityTypeBuilder<BackupScheduleOccurrence> builder)
    {
        BackupMapping.Base(builder, "backup_schedule_occurrences");
        builder.ToTable("backup_schedule_occurrences", table =>
        {
            table.HasCheckConstraint("ck_backup_occurrence_outcome", "\"Outcome\" IN ('RunCreated','SkippedOverlap','MissedGraceExpired','MissedSuperseded','JobDisabled','AdmissionDenied')");
            // RunCreated names the created run and SkippedOverlap the run in the way; no other outcome names a run.
            table.HasCheckConstraint("ck_backup_occurrence_run", "(\"Outcome\" IN ('RunCreated','SkippedOverlap')) = (\"RunId\" IS NOT NULL)");
        });
        builder.Property(occurrence => occurrence.Outcome).HasConversion<string>().HasMaxLength(24).IsRequired();
        builder.Property(occurrence => occurrence.Reason).HasMaxLength(64).IsRequired();
        builder.HasOne<BackupJob>().WithMany().HasForeignKey(occurrence => new { occurrence.JobId, occurrence.TenantId })
            .HasPrincipalKey(job => new { job.Id, job.TenantId }).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<BackupRun>().WithMany().HasForeignKey(occurrence => new { occurrence.RunId, occurrence.TenantId })
            .HasPrincipalKey(run => new { run.Id, run.TenantId }).OnDelete(DeleteBehavior.Restrict);
        // A scheduled instant is decided exactly once. This key, not memory, is the scheduler's idempotency.
        builder.HasIndex(occurrence => new { occurrence.TenantId, occurrence.JobId, occurrence.ScheduledAt }).IsUnique();
        // At most one occurrence is the origin of a run.
        builder.HasIndex(occurrence => new { occurrence.TenantId, occurrence.RunId }).IsUnique().HasFilter("\"Outcome\" = 'RunCreated'");
        // Pruning looks at old decisions only.
        builder.HasIndex(occurrence => occurrence.DecidedAt).HasDatabaseName("ix_backup_occurrences_decided");
    }
}

internal sealed class ReplicaRepairOperationConfiguration : IEntityTypeConfiguration<ReplicaRepairOperation>
{
    public void Configure(EntityTypeBuilder<ReplicaRepairOperation> builder)
    {
        BackupMapping.Base(builder, "replica_repair_operations");
        builder.ToTable("replica_repair_operations", table =>
        {
            table.HasCheckConstraint("ck_replica_repair_operations_status", "\"Status\" IN ('Pending','InProgress','Succeeded','Failed','Cancelled')");
            table.HasCheckConstraint("ck_replica_repair_operations_bytes", "\"TransferredBytes\" >= 0 AND \"TotalBytes\" >= 0 AND \"TransferredBytes\" <= \"TotalBytes\"");
            table.HasCheckConstraint("ck_replica_repair_operations_reserved", "\"ReservedQuotaBytes\" >= 0");
            table.HasCheckConstraint("ck_replica_repair_operations_terminal", "(\"Status\" IN ('Succeeded','Failed','Cancelled')) = (\"CompletedAt\" IS NOT NULL)");
        });

        builder.Property(repair => repair.StorageProvider).HasMaxLength(128).IsRequired();
        builder.Property(repair => repair.TargetKey).HasMaxLength(1024);
        builder.Property(repair => repair.Status).HasConversion<string>().HasMaxLength(32).IsRequired();
        builder.Property(repair => repair.LeaseOwner).HasMaxLength(128);
        builder.Property(repair => repair.FailureCode).HasMaxLength(128);
        builder.Property(repair => repair.FailureReason).HasMaxLength(512);
        builder.Property(repair => repair.AttemptCount).IsRequired();
        builder.Property(repair => repair.MaxAttempts).IsRequired();
        builder.Property(repair => repair.ReservedQuotaBytes).HasColumnType("bigint").IsRequired();
        builder.Property(repair => repair.NextRetryAt);
        builder.Property(repair => repair.LastAttemptAt);
        builder.Property(repair => repair.LastFailureCode).HasMaxLength(64);
        builder.Ignore(repair => repair.IsExhausted);
        builder.HasOne<BackupArtifact>().WithMany().HasForeignKey(repair => new { repair.ArtifactId, repair.TenantId })
            .HasPrincipalKey(artifact => new { artifact.Id, artifact.TenantId }).OnDelete(DeleteBehavior.Restrict);

        builder.HasOne<BackupReplica>().WithMany().HasForeignKey(repair => new { repair.SourceReplicaId, repair.TenantId })
            .HasPrincipalKey(replica => new { replica.Id, replica.TenantId }).OnDelete(DeleteBehavior.Restrict).IsRequired(false);

        builder.HasIndex(repair => new { repair.TenantId, repair.ArtifactId, repair.StorageConnectionId })
            .HasDatabaseName("ux_replica_repair_active")
            .IsUnique()
            .HasFilter("\"Status\" IN ('Pending', 'InProgress')");

        builder.HasIndex(repair => repair.SourceReplicaId)
            .HasDatabaseName("ix_replica_repair_active_source")
            .HasFilter("\"Status\" IN ('Pending', 'InProgress') AND \"SourceReplicaId\" IS NOT NULL");

        builder.HasIndex(repair => new { repair.NextRetryAt, repair.Id })
            .HasDatabaseName("ix_replica_repair_due")
            .HasFilter("\"Status\" = 'Pending'");
    }
}
