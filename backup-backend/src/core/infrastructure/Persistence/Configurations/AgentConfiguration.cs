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
        builder.Ignore(agent => agent.Capabilities);
        builder.HasOne<Tenant>().WithMany()
            .HasForeignKey(agent => agent.TenantId)
            .OnDelete(DeleteBehavior.Restrict);
        builder.HasIndex(agent => agent.TenantId);
        builder.Property<uint>("xmin").IsRowVersion();
    }
}
