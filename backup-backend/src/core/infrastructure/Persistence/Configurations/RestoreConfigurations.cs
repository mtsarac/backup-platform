using BackupPlatform.Domain.Agents;
using BackupPlatform.Domain.Backups;
using BackupPlatform.Domain.Restores;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace BackupPlatform.Infrastructure.Persistence.Configurations;

internal static class RestoreMapping
{
    /// <summary>The statuses of an operation and of its attempts, as stored (enum names, readable in a query and in a partial index).</summary>
    internal const string Statuses = "'Pending','Leased','Running','Succeeded','Failed','Cancelled','AgentLost'";
    internal const string TerminalStatuses = "'Succeeded','Failed','Cancelled','AgentLost'";
    internal const string TargetStates = "'NotModified','Online','PartiallyRestored','Unknown'";
}

internal sealed class RestoreOperationConfiguration : IEntityTypeConfiguration<RestoreOperation>
{
    public void Configure(EntityTypeBuilder<RestoreOperation> builder)
    {
        BackupMapping.Base(builder, "restore_operations");
        BackupMapping.Agent(builder);
        builder.ToTable("restore_operations", table =>
        {
            table.HasCheckConstraint("ck_restore_operations_status", $"\"Status\" IN ({RestoreMapping.Statuses})");
            table.HasCheckConstraint("ck_restore_operations_source", "\"SourceAccess\" IN ('AgentLocal','CentralStorage')");
            // The decision to overwrite is a stored fact and it must agree with the mode: only a replace restore carries the overwrite policy.
            table.HasCheckConstraint("ck_restore_operations_mode",
                "(\"Mode\" = 'NewDatabase' AND \"OverwritePolicy\" = 'Forbidden') OR (\"Mode\" = 'ReplaceExistingDatabase' AND \"OverwritePolicy\" = 'ReplaceExisting')");
            table.HasCheckConstraint("ck_restore_operations_attempt", "\"CurrentAttemptNumber\" >= 1");
            table.HasCheckConstraint("ck_restore_operations_text",
                "length(\"DatabaseName\") BETWEEN 1 AND 128 AND length(\"IdempotencyKey\") BETWEEN 1 AND 128 AND length(\"RequestFingerprint\") BETWEEN 1 AND 128");
            table.HasCheckConstraint("ck_restore_operations_target",
                "(\"TargetPort\" IS NULL OR \"TargetPort\" BETWEEN 1 AND 65535) AND " +
                "((\"TargetIntegratedSecurity\" AND \"TargetCredentialReference\" IS NULL) OR (NOT \"TargetIntegratedSecurity\" AND \"TargetCredentialReference\" IS NOT NULL))");
            // An operation ends exactly once: terminal, completed and with a stated target state all go together, and only the outcomes that make sense carry a code.
            table.HasCheckConstraint("ck_restore_operations_terminal",
                $"(\"Status\" IN ({RestoreMapping.TerminalStatuses})) = (\"CompletedAt\" IS NOT NULL) AND (\"CompletedAt\" IS NOT NULL) = (\"TargetState\" IS NOT NULL)");
            table.HasCheckConstraint("ck_restore_operations_target_state", $"\"TargetState\" IS NULL OR \"TargetState\" IN ({RestoreMapping.TargetStates})");
            table.HasCheckConstraint("ck_restore_operations_outcome",
                "(\"Status\" <> 'Succeeded' OR (\"TargetState\" = 'Online' AND \"FailureCode\" IS NULL)) AND " +
                "(\"Status\" NOT IN ('Failed','Cancelled','AgentLost') OR (\"TargetState\" <> 'Online' AND \"FailureCode\" IS NOT NULL))");
            table.HasCheckConstraint("ck_restore_operations_result", "\"ResultJson\" IS NULL OR \"CompletedAt\" IS NOT NULL");
        });
        builder.Property(operation => operation.SourceAccess).HasConversion<string>().HasMaxLength(16).IsRequired();
        builder.Property(operation => operation.Mode).HasConversion<string>().HasMaxLength(24).IsRequired();
        builder.Property(operation => operation.OverwritePolicy).HasConversion<string>().HasMaxLength(16).IsRequired();
        builder.Property(operation => operation.Status).HasConversion<string>().HasMaxLength(16).IsRequired();
        builder.Property(operation => operation.TargetState).HasConversion<string>().HasMaxLength(24);
        builder.Property(operation => operation.DatabaseName).HasMaxLength(128).IsRequired();
        builder.Property(operation => operation.IdempotencyKey).HasMaxLength(128).IsRequired();
        builder.Property(operation => operation.RequestFingerprint).HasMaxLength(128).IsRequired();
        builder.Property(operation => operation.FailureCode).HasMaxLength(128);
        // The agent's typed report. jsonb, so it is validated as JSON by the database; the store compares reports semantically, never as text.
        builder.Property(operation => operation.ResultJson).HasColumnType("jsonb");
        builder.ComplexProperty(operation => operation.Target, target =>
        {
            target.Property(value => value.Host).HasColumnName("TargetHost").HasMaxLength(255);
            target.Property(value => value.Port).HasColumnName("TargetPort");
            target.Property(value => value.IntegratedSecurity).HasColumnName("TargetIntegratedSecurity");
            target.Property(value => value.DatabaseCredentialReference).HasColumnName("TargetCredentialReference").HasMaxLength(64);
            target.Property(value => value.Encrypt).HasColumnName("TargetEncrypt");
            target.Property(value => value.TrustServerCertificate).HasColumnName("TargetTrustServerCertificate");
        });
        builder.ComplexProperty(operation => operation.Options, options =>
        {
            options.Property(value => value.TerminateActiveConnections).HasColumnName("OptionTerminateActiveConnections");
            options.Property(value => value.VerifyChecksum).HasColumnName("OptionVerifyChecksum");
            options.Property(value => value.VerifyBackup).HasColumnName("OptionVerifyBackup");
        });
        builder.Ignore(operation => operation.CurrentAttempt);
        builder.Ignore(operation => operation.IsTerminal);
        builder.Ignore(operation => operation.IsActive);
        builder.HasOne<BackupArtifact>().WithMany().HasForeignKey(operation => new { operation.ArtifactId, operation.TenantId })
            .HasPrincipalKey(artifact => new { artifact.Id, artifact.TenantId }).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<BackupReplica>().WithMany().HasForeignKey(operation => new { operation.ReplicaId, operation.TenantId })
            .HasPrincipalKey(replica => new { replica.Id, replica.TenantId }).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<Agent>().WithMany().HasForeignKey(operation => new { operation.AgentId, operation.TenantId })
            .HasPrincipalKey(agent => new { agent.Id, agent.TenantId }).OnDelete(DeleteBehavior.Restrict);
        builder.HasMany(operation => operation.Attempts).WithOne().HasForeignKey(attempt => new { attempt.OperationId, attempt.TenantId })
            .HasPrincipalKey(operation => new { operation.Id, operation.TenantId }).OnDelete(DeleteBehavior.Restrict);
        builder.Navigation(operation => operation.Attempts).HasField("_attempts").UsePropertyAccessMode(PropertyAccessMode.Field);
        // One pin per operation: the one-to-one foreign key is unique.
        builder.HasOne(operation => operation.Pin).WithOne().HasForeignKey<RestoreReplicaPin>(pin => new { pin.OperationId, pin.TenantId })
            .HasPrincipalKey<RestoreOperation>(operation => new { operation.Id, operation.TenantId }).OnDelete(DeleteBehavior.Restrict).IsRequired();
        // The same request key answers with the same operation, whichever node or retry asks.
        builder.HasIndex(operation => new { operation.TenantId, operation.ArtifactId, operation.IdempotencyKey }).IsUnique();
        builder.HasIndex(operation => new { operation.TenantId, operation.CreatedAt });
        builder.HasIndex(operation => new { operation.TenantId, operation.ArtifactId, operation.CreatedAt });
        builder.HasIndex(operation => new { operation.TenantId, operation.Status, operation.CreatedAt });
        // The agent's claim and its busy checks look at one agent's operations by status.
        builder.HasIndex(operation => new { operation.TenantId, operation.AgentId, operation.Status, operation.CreatedAt });
        // The unclaimed-restore sweep: pending operations, oldest first.
        builder.HasIndex(operation => new { operation.CreatedAt, operation.Id }).HasDatabaseName("ix_restore_operations_pending").HasFilter("\"Status\" = 'Pending'");
    }
}

internal sealed class RestoreAttemptConfiguration : IEntityTypeConfiguration<RestoreAttempt>
{
    public void Configure(EntityTypeBuilder<RestoreAttempt> builder)
    {
        BackupMapping.Base(builder, "restore_attempts");
        BackupMapping.Agent(builder);
        builder.ToTable("restore_attempts", table =>
        {
            table.HasCheckConstraint("ck_restore_attempts_status", $"\"Status\" IN ({RestoreMapping.Statuses})");
            table.HasCheckConstraint("ck_restore_attempts_number", "\"Number\" >= 1");
            table.HasCheckConstraint("ck_restore_attempts_lease",
                "\"Status\" NOT IN ('Leased','Running') OR (\"LeaseToken\" IS NOT NULL AND \"LeaseToken\" <> '00000000-0000-0000-0000-000000000000'::uuid AND \"LeaseExpiresAt\" IS NOT NULL)");
            table.HasCheckConstraint("ck_restore_attempts_progress", "\"ProgressPercent\" IS NULL OR (\"ProgressPercent\" >= 0 AND \"ProgressPercent\" <= 100)");
            table.HasCheckConstraint("ck_restore_attempts_terminal",
                $"(\"Status\" IN ({RestoreMapping.TerminalStatuses})) = (\"CompletedAt\" IS NOT NULL) AND (\"CompletedAt\" IS NOT NULL) = (\"TargetState\" IS NOT NULL)");
            table.HasCheckConstraint("ck_restore_attempts_target_state", $"\"TargetState\" IS NULL OR \"TargetState\" IN ({RestoreMapping.TargetStates})");
            table.HasCheckConstraint("ck_restore_attempts_started", "\"Status\" <> 'Running' OR \"StartedAt\" IS NOT NULL");
        });
        builder.Property(attempt => attempt.Status).HasConversion<string>().HasMaxLength(16).IsRequired();
        builder.Property(attempt => attempt.TargetState).HasConversion<string>().HasMaxLength(24);
        builder.Property(attempt => attempt.SoftwareVersion).HasMaxLength(64);
        builder.Property(attempt => attempt.ProtocolVersion).HasMaxLength(32);
        builder.Property(attempt => attempt.Platform).HasMaxLength(32);
        builder.Property(attempt => attempt.ProgressStage).HasMaxLength(64);
        builder.Property(attempt => attempt.FailureCode).HasMaxLength(128);
        builder.Ignore(attempt => attempt.IsTerminal);
        builder.HasOne<Agent>().WithMany().HasForeignKey(attempt => new { attempt.AgentId, attempt.TenantId })
            .HasPrincipalKey(agent => new { agent.Id, agent.TenantId }).OnDelete(DeleteBehavior.Restrict);
        builder.HasIndex(attempt => new { attempt.TenantId, attempt.OperationId, attempt.Number }).IsUnique();
        // The busy checks of backup and restore claims count one agent's live attempts.
        builder.HasIndex(attempt => new { attempt.TenantId, attempt.AgentId, attempt.Status });
        builder.HasIndex(attempt => attempt.LeaseToken).IsUnique().HasFilter("\"LeaseToken\" IS NOT NULL");
        // The stale-lease sweep: attempts that hold a lease, earliest expiry first.
        builder.HasIndex(attempt => attempt.LeaseExpiresAt).HasDatabaseName("ix_restore_attempts_leased").HasFilter("\"Status\" IN ('Leased','Running')");
    }
}

internal sealed class RestoreReplicaPinConfiguration : IEntityTypeConfiguration<RestoreReplicaPin>
{
    public void Configure(EntityTypeBuilder<RestoreReplicaPin> builder)
    {
        BackupMapping.Base(builder, "restore_replica_pins");
        builder.Ignore(pin => pin.IsActive);
        builder.HasOne<BackupReplica>().WithMany().HasForeignKey(pin => new { pin.ReplicaId, pin.TenantId })
            .HasPrincipalKey(replica => new { replica.Id, replica.TenantId }).OnDelete(DeleteBehavior.Restrict);
        // What every delete claim and the replica trigger ask: is this replica pinned right now? Only unreleased pins are indexed.
        builder.HasIndex(pin => pin.ReplicaId).HasDatabaseName("ix_restore_replica_pins_active").HasFilter("\"ReleasedAt\" IS NULL");
    }
}
