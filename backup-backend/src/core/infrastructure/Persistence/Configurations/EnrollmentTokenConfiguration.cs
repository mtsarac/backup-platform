using BackupPlatform.Agents.Enrollment;
using BackupPlatform.Domain.Agents;
using BackupPlatform.Domain.Tenancy;
using BackupPlatform.Infrastructure.Persistence.Conversions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace BackupPlatform.Infrastructure.Persistence.Configurations;

/// <summary>Token history is retained: no cascading deletes, and only the secret hash is stored.</summary>
internal sealed class EnrollmentTokenConfiguration : IEntityTypeConfiguration<EnrollmentToken>
{
    public void Configure(EntityTypeBuilder<EnrollmentToken> builder)
    {
        builder.ToTable("agent_enrollment_tokens", table =>
        {
            table.HasCheckConstraint("ck_agent_enrollment_tokens_id", "\"Id\" <> '00000000-0000-0000-0000-000000000000'::uuid");
            table.HasCheckConstraint("ck_agent_enrollment_tokens_tenant_id", "\"TenantId\" <> '00000000-0000-0000-0000-000000000000'::uuid");
            table.HasCheckConstraint("ck_agent_enrollment_tokens_hash", "octet_length(\"SecretHash\") = 32");
            table.HasCheckConstraint("ck_agent_enrollment_tokens_expiry", "\"ExpiresAt\" > \"CreatedAt\"");
            table.HasCheckConstraint("ck_agent_enrollment_tokens_consumption", "(\"ConsumedAt\" IS NULL) = (\"ConsumedByAgentId\" IS NULL)");
            table.HasCheckConstraint("ck_agent_enrollment_tokens_revocation", "(\"RevokedAt\" IS NULL) = (\"RevokedBy\" IS NULL)");
            table.HasCheckConstraint("ck_agent_enrollment_tokens_terminal", "\"ConsumedAt\" IS NULL OR \"RevokedAt\" IS NULL");
        });
        builder.HasKey(token => token.Id);
        builder.Property(token => token.Id).HasColumnType("uuid").ValueGeneratedNever().IsRequired();
        builder.Property(token => token.TenantId)
            .HasConversion<TenantIdConverter>().HasColumnType("uuid").ValueGeneratedNever().IsConcurrencyToken().IsRequired();
        builder.Property(token => token.SecretHash).HasColumnType("bytea").IsRequired();
        builder.Property(token => token.CreatedBy).HasConversion<UserIdConverter>().HasColumnType("uuid").IsRequired();
        builder.Property(token => token.CreatedAt).HasColumnType("timestamp with time zone").IsRequired();
        builder.Property(token => token.ExpiresAt).HasColumnType("timestamp with time zone").IsRequired();
        builder.Property(token => token.ConsumedAt).HasColumnType("timestamp with time zone");
        builder.Property(token => token.ConsumedByAgentId).HasConversion<AgentIdConverter>().HasColumnType("uuid");
        builder.Property(token => token.RevokedAt).HasColumnType("timestamp with time zone");
        builder.Property(token => token.RevokedBy).HasConversion<UserIdConverter>().HasColumnType("uuid");
        builder.HasOne<Tenant>().WithMany().HasForeignKey(token => token.TenantId).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<User>().WithMany().HasForeignKey(token => token.CreatedBy).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<User>().WithMany().HasForeignKey(token => token.RevokedBy).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<Agent>().WithMany()
            .HasForeignKey(token => new { token.ConsumedByAgentId, token.TenantId })
            .HasPrincipalKey(agent => new { agent.Id, agent.TenantId })
            .OnDelete(DeleteBehavior.Restrict);
        builder.HasIndex(token => token.TenantId);
        builder.Property<uint>("xmin").IsRowVersion();
    }
}
