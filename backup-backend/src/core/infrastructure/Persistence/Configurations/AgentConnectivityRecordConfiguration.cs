using BackupPlatform.Agents.Connectivity;
using BackupPlatform.Domain.Agents;
using BackupPlatform.Infrastructure.Persistence.Conversions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace BackupPlatform.Infrastructure.Persistence.Configurations;

/// <summary>
/// One row per agent, written only by atomic monotonic upserts. Online/Offline is derived from LastSeenAt, never
/// stored, so an offline agent produces no writes. Connection identifiers are ephemeral and have no column.
/// </summary>
internal sealed class AgentConnectivityRecordConfiguration : IEntityTypeConfiguration<AgentConnectivityRecord>
{
    public void Configure(EntityTypeBuilder<AgentConnectivityRecord> builder)
    {
        builder.ToTable("agent_connectivity", table =>
        {
            table.HasCheckConstraint("ck_agent_connectivity_tenant_id", "\"TenantId\" <> '00000000-0000-0000-0000-000000000000'::uuid");
            table.HasCheckConstraint("ck_agent_connectivity_protocol", "(\"ReportedProtocolMajor\" IS NULL) = (\"ReportedProtocolMinor\" IS NULL) AND (\"ReportedProtocolMajor\" IS NULL OR (\"ReportedProtocolMajor\" >= 1 AND \"ReportedProtocolMinor\" >= 0))");
        });
        builder.HasKey(record => record.AgentId);
        builder.Property(record => record.AgentId).HasConversion<AgentIdConverter>().HasColumnType("uuid").ValueGeneratedNever().IsRequired();
        builder.Property(record => record.TenantId).HasConversion<TenantIdConverter>().HasColumnType("uuid").IsRequired();
        builder.Property(record => record.LastSeenAt).HasColumnType("timestamp with time zone").IsRequired();
        builder.Property(record => record.LastHeartbeatAt).HasColumnType("timestamp with time zone");
        builder.Property(record => record.LastConnectedAt).HasColumnType("timestamp with time zone");
        builder.Property(record => record.ReportedAgentVersion).HasMaxLength(64);
        builder.Property(record => record.ReportedPlatform).HasConversion<int?>();
        builder.Property(record => record.ReportedOsDescription).HasMaxLength(256);
        builder.Property(record => record.ReportedRuntimeVersion).HasMaxLength(256);
        builder.Property(record => record.CapabilityRevision).HasMaxLength(64);
        // Deliberately WithMany: a one-to-one mapping adds a second unique index on (AgentId, TenantId), and a unique
        // index other than the upsert's ON CONFLICT arbiter (the primary key) can raise duplicate-key errors when two
        // first writes for one agent race. The primary key alone enforces one row per agent.
        builder.HasOne<Agent>().WithMany()
            .HasForeignKey(record => new { record.AgentId, record.TenantId })
            .HasPrincipalKey(agent => new { agent.Id, agent.TenantId })
            .OnDelete(DeleteBehavior.Restrict)
            .IsRequired();
        builder.HasIndex(record => new { record.TenantId, record.LastSeenAt });
    }
}
