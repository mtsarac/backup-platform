using BackupPlatform.Domain.Backups;
using BackupPlatform.Domain.Downloads;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace BackupPlatform.Infrastructure.Persistence.Configurations;

internal sealed class DownloadSessionConfiguration : IEntityTypeConfiguration<DownloadSession>
{
    public void Configure(EntityTypeBuilder<DownloadSession> builder)
    {
        BackupMapping.Base(builder, "download_sessions");
        builder.Ignore(session => session.IsTerminal);
        builder.Property(session => session.State).HasConversion<string>().HasMaxLength(16).IsRequired();
        builder.Property(session => session.StorageProvider).HasMaxLength(64).IsRequired();
        builder.Property(session => session.StorageTarget).HasMaxLength(1024).IsRequired();
        builder.Property(session => session.Key).HasMaxLength(2048).IsRequired();
        builder.Property(session => session.Checksum).HasMaxLength(64).IsRequired();
        builder.Property(session => session.Filename).HasMaxLength(255).IsRequired();
        builder.Property(session => session.LeaseOwner).HasMaxLength(128);
        builder.Property(session => session.FailureCode).HasMaxLength(128);
        builder.HasOne<BackupArtifact>().WithMany().HasForeignKey(session => new { session.ArtifactId, session.TenantId })
            .HasPrincipalKey(artifact => new { artifact.Id, artifact.TenantId }).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<BackupReplica>().WithMany().HasForeignKey(session => new { session.ReplicaId, session.TenantId })
            .HasPrincipalKey(replica => new { replica.Id, replica.TenantId }).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<BackupPlatform.Domain.Support.SupportAccessSession>().WithMany().HasForeignKey(session => new { session.SupportSessionId, session.TenantId })
            .HasPrincipalKey(support => new { support.Id, support.TenantId }).OnDelete(DeleteBehavior.Restrict);
        builder.HasIndex(session => session.SupportSessionId).HasDatabaseName("ix_download_sessions_support").HasFilter("\"SupportSessionId\" IS NOT NULL");
        builder.HasIndex(session => session.ReplicaId).HasDatabaseName("ix_download_sessions_active").HasFilter("\"ReleasedAt\" IS NULL");
        builder.HasIndex(session => session.ExpiresAt).HasDatabaseName("ix_download_sessions_expiry").HasFilter("\"ReleasedAt\" IS NULL");
        builder.HasIndex(session => new { session.TenantId, session.AgentId, session.State });
        builder.ToTable("download_sessions", table =>
        {
            table.HasCheckConstraint("ck_download_sessions_state", "\"State\" IN ('Preparing','Ready','Streaming','Completed','Failed','Cancelled')");
            table.HasCheckConstraint("ck_download_sessions_bytes", "\"SizeBytes\" >= 0 AND \"TransferredBytes\" BETWEEN 0 AND \"SizeBytes\"");
            table.HasCheckConstraint("ck_download_sessions_expiry", "\"ExpiresAt\" > \"CreatedAt\"");
            table.HasCheckConstraint("ck_download_sessions_release", "(\"State\" IN ('Completed','Failed','Cancelled')) = (\"ReleasedAt\" IS NOT NULL)");
            table.HasCheckConstraint("ck_download_sessions_lease", "(\"LeaseOwner\" IS NULL) = (\"LeaseExpiresAt\" IS NULL)");
        });
    }
}
