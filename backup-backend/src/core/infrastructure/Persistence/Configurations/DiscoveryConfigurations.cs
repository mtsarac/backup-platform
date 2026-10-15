using BackupPlatform.Domain.Agents;
using BackupPlatform.Domain.Discovery;
using BackupPlatform.Infrastructure.Persistence.Conversions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace BackupPlatform.Infrastructure.Persistence.Configurations;

internal sealed class DiscoveryAgentStateConfiguration : IEntityTypeConfiguration<DiscoveryAgentState>
{
    public void Configure(EntityTypeBuilder<DiscoveryAgentState> builder)
    {
        builder.ToTable("discovery_agent_states", table =>
        {
            table.HasCheckConstraint("ck_discovery_agent_states_ids", "\"AgentId\" <> '00000000-0000-0000-0000-000000000000'::uuid AND \"TenantId\" <> '00000000-0000-0000-0000-000000000000'::uuid");
            table.HasCheckConstraint("ck_discovery_agent_states_interval", $"\"ScanIntervalMinutes\" BETWEEN {DiscoveryAgentState.MinimumIntervalMinutes} AND {DiscoveryAgentState.MaximumIntervalMinutes}");
        });
        builder.HasKey(state => state.AgentId);
        builder.Property(state => state.AgentId).HasConversion<AgentIdConverter>().HasColumnType("uuid").ValueGeneratedNever().IsRequired();
        builder.Property(state => state.TenantId).HasConversion<TenantIdConverter>().HasColumnType("uuid").IsRequired();
        builder.Property(state => state.Enabled).IsRequired();
        builder.Property(state => state.ScanIntervalMinutes).IsRequired();
        builder.Property(state => state.IncludeDocker).IsRequired();
        builder.Property(state => state.ScanRequestedAt).HasColumnType("timestamp with time zone");
        builder.Property(state => state.ScanRequestReason).HasMaxLength(16);
        builder.Property(state => state.RequestedInstanceKey).HasMaxLength(DiscoveredInstance.MaximumKeyLength);
        builder.Property(state => state.ScanId).HasColumnType("uuid");
        builder.Property(state => state.ScanReason).HasMaxLength(16);
        builder.Property(state => state.ScanStartedAt).HasColumnType("timestamp with time zone");
        builder.Property(state => state.NextScanDueAt).HasColumnType("timestamp with time zone");
        builder.Property(state => state.LastScanAt).HasColumnType("timestamp with time zone");
        builder.Property(state => state.LastSuccessfulScanAt).HasColumnType("timestamp with time zone");
        builder.Property(state => state.LastOutcome).HasMaxLength(16);
        builder.Property(state => state.LastFailureCode).HasMaxLength(64);
        builder.Property(state => state.SourcesJson).HasColumnType("jsonb").IsRequired();
        builder.HasOne<Agent>().WithMany()
            .HasForeignKey(state => new { state.AgentId, state.TenantId })
            .HasPrincipalKey(agent => new { agent.Id, agent.TenantId })
            .OnDelete(DeleteBehavior.Restrict)
            .IsRequired();
        builder.HasIndex(state => state.TenantId);
        builder.Property<uint>("xmin").IsRowVersion();
    }
}

internal sealed class DiscoveredInstanceConfiguration : IEntityTypeConfiguration<DiscoveredInstance>
{
    public void Configure(EntityTypeBuilder<DiscoveredInstance> builder)
    {
        BackupMapping.Base(builder, "discovery_instances");
        builder.ToTable("discovery_instances", table =>
        {
            table.HasCheckConstraint("ck_discovery_instances_engine", "\"Engine\" IN ('mssql','postgres')");
            table.HasCheckConstraint("ck_discovery_instances_presence", "\"Presence\" IN ('Present','Missing')");
            table.HasCheckConstraint("ck_discovery_instances_port", "\"Port\" IS NULL OR \"Port\" BETWEEN 1 AND 65535");
            table.HasCheckConstraint("ck_discovery_instances_binding", "NOT (\"IntegratedSecurity\" AND \"CredentialReference\" IS NOT NULL)");
        });
        BackupMapping.Agent(builder);
        builder.Property(instance => instance.InstanceKey).HasMaxLength(DiscoveredInstance.MaximumKeyLength).IsRequired();
        builder.Property(instance => instance.Engine).HasMaxLength(16).IsRequired();
        builder.Property(instance => instance.Source).HasMaxLength(16).IsRequired();
        builder.Property(instance => instance.DisplayName).HasMaxLength(200).IsRequired();
        builder.Property(instance => instance.InstanceName).HasMaxLength(128);
        builder.Property(instance => instance.Host).HasMaxLength(255).IsRequired();
        builder.Property(instance => instance.Version).HasMaxLength(128);
        builder.Property(instance => instance.Edition).HasMaxLength(128);
        builder.Property(instance => instance.HostPlatform).HasMaxLength(128);
        builder.Property(instance => instance.ContainerName).HasMaxLength(256);
        builder.Property(instance => instance.ContainerImage).HasMaxLength(256);
        builder.Property(instance => instance.Presence).HasConversion<string>().HasMaxLength(16).IsRequired();
        builder.Property(instance => instance.Reachability).HasMaxLength(16).IsRequired();
        builder.Property(instance => instance.Access).HasMaxLength(32).IsRequired();
        builder.Property(instance => instance.AccessReasonCode).HasMaxLength(64);
        builder.Property(instance => instance.FullBackup).HasMaxLength(16).IsRequired();
        builder.Property(instance => instance.FullBackupReasonCode).HasMaxLength(64);
        builder.Property(instance => instance.CredentialReference).HasMaxLength(128);
        builder.Property(instance => instance.LastChangeKind).HasConversion<string>().HasMaxLength(16);
        builder.Property(instance => instance.FirstSeenAt).HasColumnType("timestamp with time zone").IsRequired();
        builder.Property(instance => instance.LastSeenAt).HasColumnType("timestamp with time zone").IsRequired();
        builder.Property(instance => instance.LastChangeAt).HasColumnType("timestamp with time zone");
        builder.Property(instance => instance.DatabasesCheckedAt).HasColumnType("timestamp with time zone");
        builder.Ignore(instance => instance.HasBinding);
        builder.HasOne<Agent>().WithMany()
            .HasForeignKey(instance => new { instance.AgentId, instance.TenantId })
            .HasPrincipalKey(agent => new { agent.Id, agent.TenantId })
            .OnDelete(DeleteBehavior.Restrict)
            .IsRequired();
        builder.HasIndex(instance => new { instance.AgentId, instance.InstanceKey }).IsUnique().HasDatabaseName("ux_discovery_instances_agent_key");
    }
}

internal sealed class DiscoveredDatabaseConfiguration : IEntityTypeConfiguration<DiscoveredDatabase>
{
    public void Configure(EntityTypeBuilder<DiscoveredDatabase> builder)
    {
        BackupMapping.Base(builder, "discovery_databases");
        builder.ToTable("discovery_databases", table =>
            table.HasCheckConstraint("ck_discovery_databases_presence", "\"Presence\" IN ('Present','Missing')"));
        builder.Property(database => database.Name).HasMaxLength(DiscoveredDatabase.MaximumNameLength).IsRequired();
        builder.Property(database => database.State).HasMaxLength(16).IsRequired();
        builder.Property(database => database.FullBackup).HasMaxLength(16).IsRequired();
        builder.Property(database => database.FullBackupReasonCode).HasMaxLength(64);
        builder.Property(database => database.Presence).HasConversion<string>().HasMaxLength(16).IsRequired();
        builder.Property(database => database.FirstSeenAt).HasColumnType("timestamp with time zone").IsRequired();
        builder.Property(database => database.LastSeenAt).HasColumnType("timestamp with time zone").IsRequired();
        builder.HasOne<DiscoveredInstance>().WithMany()
            .HasForeignKey(database => new { database.InstanceId, database.TenantId })
            .HasPrincipalKey("Id", "TenantId")
            .OnDelete(DeleteBehavior.Cascade)
            .IsRequired();
        builder.HasIndex(database => new { database.InstanceId, database.Name }).IsUnique().HasDatabaseName("ux_discovery_databases_instance_name");
    }
}
