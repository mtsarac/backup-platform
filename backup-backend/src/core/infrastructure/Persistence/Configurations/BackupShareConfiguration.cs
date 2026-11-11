using BackupPlatform.Domain.Backups;
using BackupPlatform.Domain.Tenancy;
using BackupPlatform.Infrastructure.Persistence.Conversions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace BackupPlatform.Infrastructure.Persistence.Configurations;

internal sealed class BackupShareConfiguration : IEntityTypeConfiguration<BackupShare>
{
    public void Configure(EntityTypeBuilder<BackupShare> builder)
    {
        BackupMapping.Base(builder, "backup_shares");

        builder.Property(share => share.ArtifactId)
            .IsRequired();

        builder.Property(share => share.CreatedByUserId)
            .HasConversion<UserIdConverter>()
            .HasColumnType("uuid")
            .IsRequired();

        builder.Property(share => share.CreatedByRole)
            .HasConversion<int>()
            .IsRequired();

        builder.Property(share => share.Reason)
            .HasMaxLength(512);

        builder.Property(share => share.CreatedAtUtc)
            .IsRequired();

        builder.Property(share => share.ExpiresAtUtc)
            .IsRequired();

        builder.Property(share => share.RevokedAtUtc);

        builder.Property(share => share.RevokedByUserId)
            .HasConversion<UserIdConverter>()
            .HasColumnType("uuid");

        builder.HasOne<BackupArtifact>()
            .WithMany()
            .HasForeignKey(share => new { share.ArtifactId, share.TenantId })
            .HasPrincipalKey(artifact => new { artifact.Id, artifact.TenantId })
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasOne<User>()
            .WithMany()
            .HasForeignKey(share => share.CreatedByUserId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasOne<User>()
            .WithMany()
            .HasForeignKey(share => share.RevokedByUserId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasIndex(share => new { share.TenantId, share.ArtifactId });
        builder.HasIndex(share => new { share.TenantId, share.CreatedAtUtc });
        builder.HasIndex(share => share.ExpiresAtUtc);

        builder.ToTable("backup_shares", table =>
        {
            table.HasCheckConstraint("ck_backup_shares_expiry", "\"ExpiresAtUtc\" > \"CreatedAtUtc\"");
            table.HasCheckConstraint("ck_backup_shares_artifact_id_nonempty", "\"ArtifactId\" <> '00000000-0000-0000-0000-000000000000'::uuid");
            table.HasCheckConstraint("ck_backup_shares_created_by_user_id_nonempty", "\"CreatedByUserId\" <> '00000000-0000-0000-0000-000000000000'::uuid");
            table.HasCheckConstraint("ck_backup_shares_created_by_role", "\"CreatedByRole\" IN (1, 2)");
        });
    }
}
