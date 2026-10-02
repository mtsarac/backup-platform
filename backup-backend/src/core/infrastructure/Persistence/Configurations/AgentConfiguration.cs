using BackupPlatform.Domain.Agents;
using BackupPlatform.Domain.Tenancy;
using BackupPlatform.Infrastructure.Persistence.Conversions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace BackupPlatform.Infrastructure.Persistence.Configurations;

/// <summary>
/// Persists agent identity, lifecycle, and software metadata only. Capabilities are explicitly
/// ignored in this iteration; the context rejects saves of nonempty capability sets so that
/// unsupported capability state cannot be silently discarded.
/// </summary>
internal sealed class AgentConfiguration : IEntityTypeConfiguration<Agent>
{
    public void Configure(EntityTypeBuilder<Agent> builder)
    {
        builder.ToTable("agents", table =>
        {
            table.HasCheckConstraint("ck_agents_id_nonempty", "\"Id\" <> '00000000-0000-0000-0000-000000000000'::uuid");
            table.HasCheckConstraint("ck_agents_tenant_id_nonempty", "\"TenantId\" <> '00000000-0000-0000-0000-000000000000'::uuid");
            table.HasCheckConstraint("ck_agents_status", "\"Status\" BETWEEN 1 AND 4");
            table.HasCheckConstraint("ck_agents_credential_generation", """
                ("Status" = 1 AND "CredentialGeneration" = 0)
                OR ("Status" IN (2, 3) AND "CredentialGeneration" >= 1)
                OR ("Status" = 4 AND "CredentialGeneration" >= 0)
                """);
            table.HasCheckConstraint("ck_agents_host_name", "\"HostName\" IS NULL OR \"HostName\" ~ '[^[:space:]]'");
            table.HasCheckConstraint("ck_agents_installation_id", "\"InstallationId\" IS NULL OR \"InstallationId\" <> '00000000-0000-0000-0000-000000000000'::uuid");
        });
        builder.HasKey(agent => agent.Id);
        builder.Property(agent => agent.Id)
            .HasConversion<AgentIdConverter>()
            .HasColumnType("uuid")
            .ValueGeneratedNever()
            .IsRequired();
        builder.Property(agent => agent.TenantId)
            .HasConversion<TenantIdConverter>()
            .HasColumnType("uuid")
            .ValueGeneratedNever()
            .IsConcurrencyToken()
            .IsRequired();
        builder.Property(agent => agent.Software)
            .HasConversion<AgentSoftwareConverter>()
            .HasColumnType("jsonb")
            .IsRequired();
        builder.Property(agent => agent.Status).HasConversion<int>().IsRequired();
        builder.Property(agent => agent.CredentialGeneration).IsRequired();
        builder.Property(agent => agent.HostName).HasMaxLength(Agent.MaximumHostNameLength);
        builder.Property(agent => agent.InstallationId).HasColumnType("uuid");
        builder.Ignore(agent => agent.Capabilities);
        builder.HasOne<Tenant>().WithMany()
            .HasForeignKey(agent => agent.TenantId)
            .OnDelete(DeleteBehavior.Restrict);
        builder.HasIndex(agent => agent.TenantId);
        builder.HasAlternateKey(agent => new { agent.Id, agent.TenantId }).HasName("ak_agents_id_tenant_id");
        builder.HasIndex(agent => new { agent.TenantId, agent.InstallationId })
            .IsUnique()
            .HasDatabaseName("ux_agents_tenant_installation_live")
            .HasFilter("\"InstallationId\" IS NOT NULL AND \"Status\" <> 4");
        builder.Property<uint>("xmin").IsRowVersion();
    }
}
