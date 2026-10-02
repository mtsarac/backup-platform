using BackupPlatform.Agents.Credentials;
using BackupPlatform.Domain.Agents;
using BackupPlatform.Infrastructure.Persistence.Conversions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace BackupPlatform.Infrastructure.Persistence.Configurations;

/// <summary>
/// Credential generations keep their history (revoked rows are never deleted) and never hold plaintext.
/// Partial unique indexes bound each agent to at most one active and one pending credential, so rotation can
/// never leave an unbounded set of valid secrets even if application logic regressed.
/// </summary>
internal sealed class AgentCredentialConfiguration : IEntityTypeConfiguration<AgentCredential>
{
    public void Configure(EntityTypeBuilder<AgentCredential> builder)
    {
        builder.ToTable("agent_credentials", table =>
        {
            table.HasCheckConstraint("ck_agent_credentials_id", "\"Id\" <> '00000000-0000-0000-0000-000000000000'::uuid");
            table.HasCheckConstraint("ck_agent_credentials_hash", "octet_length(\"SecretHash\") = 32");
            table.HasCheckConstraint("ck_agent_credentials_generation", "\"Generation\" >= 1");
            table.HasCheckConstraint("ck_agent_credentials_state", """
                ("Status" = 1 AND "ExpiresAt" IS NOT NULL AND "ActivatedAt" IS NULL AND "RevokedAt" IS NULL AND "RevocationReason" IS NULL)
                OR ("Status" = 2 AND "ExpiresAt" IS NULL AND "ActivatedAt" IS NOT NULL AND "RevokedAt" IS NULL AND "RevocationReason" IS NULL)
                OR ("Status" = 3 AND "RevokedAt" IS NOT NULL AND "RevocationReason" BETWEEN 1 AND 3)
                """);
        });
        builder.HasKey(credential => credential.Id);
        builder.Property(credential => credential.Id).HasColumnType("uuid").ValueGeneratedNever().IsRequired();
        builder.Property(credential => credential.AgentId).HasConversion<AgentIdConverter>().HasColumnType("uuid").IsRequired();
        builder.Property(credential => credential.TenantId)
            .HasConversion<TenantIdConverter>().HasColumnType("uuid").ValueGeneratedNever().IsConcurrencyToken().IsRequired();
        builder.Property(credential => credential.Generation).IsRequired();
        builder.Property(credential => credential.SecretHash).HasColumnType("bytea").IsRequired();
        builder.Property(credential => credential.Status).HasConversion<int>().IsRequired();
        builder.Property(credential => credential.IssuedAt).HasColumnType("timestamp with time zone").IsRequired();
        builder.Property(credential => credential.ExpiresAt).HasColumnType("timestamp with time zone");
        builder.Property(credential => credential.ActivatedAt).HasColumnType("timestamp with time zone");
        builder.Property(credential => credential.RevokedAt).HasColumnType("timestamp with time zone");
        builder.Property(credential => credential.RevocationReason).HasConversion<int?>();
        builder.HasOne<Agent>().WithMany()
            .HasForeignKey(credential => new { credential.AgentId, credential.TenantId })
            .HasPrincipalKey(agent => new { agent.Id, agent.TenantId })
            .OnDelete(DeleteBehavior.Restrict)
            .IsRequired();
        builder.HasIndex(credential => credential.AgentId, "ux_agent_credentials_one_active")
            .IsUnique().HasDatabaseName("ux_agent_credentials_one_active").HasFilter("\"Status\" = 2");
        builder.HasIndex(credential => credential.AgentId, "ux_agent_credentials_one_pending")
            .IsUnique().HasDatabaseName("ux_agent_credentials_one_pending").HasFilter("\"Status\" = 1");
        builder.Property<uint>("xmin").IsRowVersion();
    }
}
