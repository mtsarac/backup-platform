using BackupPlatform.Agents.Capabilities;
using BackupPlatform.Domain.Agents;
using BackupPlatform.Infrastructure.Persistence.Conversions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace BackupPlatform.Infrastructure.Persistence.Configurations;

/// <summary>
/// One row per capability of an agent's last synced snapshot; the whole snapshot is replaced in one transaction by
/// <c>PostgreSqlAgentCapabilityStore</c>. Not versioned with xmin: rows are never updated, only deleted and inserted
/// under the agent's row lock.
/// </summary>
internal sealed class AgentCapabilityRecordConfiguration : IEntityTypeConfiguration<AgentCapabilityRecord>
{
    public void Configure(EntityTypeBuilder<AgentCapabilityRecord> builder)
    {
        builder.ToTable("agent_capabilities", table =>
        {
            table.HasCheckConstraint("ck_agent_capabilities_tenant_id", "\"TenantId\" <> '00000000-0000-0000-0000-000000000000'::uuid");
            table.HasCheckConstraint("ck_agent_capabilities_support", "\"Support\" IN (0, 1, 2)");
            table.HasCheckConstraint("ck_agent_capabilities_text", "length(\"Identifier\") > 0 AND length(\"Revision\") > 0");
        });
        builder.HasKey(record => new { record.AgentId, record.TenantId, record.Identifier });
        builder.Property(record => record.AgentId).HasConversion<AgentIdConverter>().HasColumnType("uuid").ValueGeneratedNever().IsRequired();
        builder.Property(record => record.TenantId).HasConversion<TenantIdConverter>().HasColumnType("uuid").IsRequired();
        builder.Property(record => record.Identifier).HasMaxLength(128).IsRequired();
        builder.Property(record => record.Support).HasConversion<int>().IsRequired();
        builder.Property(record => record.MetadataJson).HasMaxLength(8192);
        builder.Property(record => record.Revision).HasMaxLength(64).IsRequired();
        builder.Property(record => record.ReportedAt).HasColumnType("timestamp with time zone").IsRequired();
        builder.HasOne<Agent>().WithMany()
            .HasForeignKey(record => new { record.AgentId, record.TenantId })
            .HasPrincipalKey(agent => new { agent.Id, agent.TenantId })
            .OnDelete(DeleteBehavior.Restrict)
            .IsRequired();
        builder.HasIndex(record => new { record.TenantId, record.AgentId });
    }
}
