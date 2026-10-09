using BackupPlatform.Domain.Identity;
using BackupPlatform.Domain.Storage;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace BackupPlatform.Infrastructure.Persistence.Configurations;

internal sealed class GoogleDriveOAuthStateConfiguration : IEntityTypeConfiguration<GoogleDriveOAuthState>
{
    public void Configure(EntityTypeBuilder<GoogleDriveOAuthState> builder)
    {
        builder.ToTable("google_drive_oauth_states");
        builder.HasKey(state => state.State);
        builder.Property(state => state.State).HasMaxLength(128).IsRequired();
        builder.Property(state => state.TenantId).HasConversion(id => id.Value, value => new TenantId(value)).IsRequired();
        builder.Property(state => state.ConnectionId).IsRequired();
        builder.Property(state => state.UserId).IsRequired();
        builder.Property(state => state.CodeVerifier).HasMaxLength(128).IsRequired();
        builder.Property(state => state.RedirectUri).HasMaxLength(512).IsRequired();
        builder.Property(state => state.CreatedAt).IsRequired();
        builder.Property(state => state.ExpiresAt).IsRequired();
        builder.Property(state => state.Used).IsRequired();

        builder.HasIndex(state => new { state.TenantId, state.ConnectionId })
            .HasDatabaseName("ix_google_drive_oauth_states_connection");
    }
}

internal sealed class GoogleDriveFileMetadataConfiguration : IEntityTypeConfiguration<GoogleDriveFileMetadata>
{
    public void Configure(EntityTypeBuilder<GoogleDriveFileMetadata> builder)
    {
        builder.ToTable("google_drive_file_metadata");
        builder.HasKey(file => file.Id);
        builder.Property(file => file.TenantId).HasConversion(id => id.Value, value => new TenantId(value)).IsRequired();
        builder.Property(file => file.ConnectionId).IsRequired();
        builder.Property(file => file.StorageKey).HasMaxLength(1024).IsRequired();
        builder.Property(file => file.FileId).HasMaxLength(128).IsRequired();
        builder.Property(file => file.ParentFolderId).HasMaxLength(128).IsRequired();
        builder.Property(file => file.FileName).HasMaxLength(256).IsRequired();
        builder.Property(file => file.SizeBytes).IsRequired();
        builder.Property(file => file.Checksum).HasMaxLength(64);
        builder.Property(file => file.Md5Checksum).HasMaxLength(64);
        builder.Property(file => file.CreatedAt).IsRequired();
        builder.Property(file => file.UpdatedAt).IsRequired();
        builder.Property(file => file.DriftKind).HasMaxLength(32);
        builder.Property(file => file.DriftDetectedAt);

        builder.HasIndex(file => new { file.TenantId, file.ConnectionId, file.StorageKey })
            .IsUnique()
            .HasDatabaseName("ix_google_drive_file_metadata_key");

        builder.HasIndex(file => new { file.ConnectionId, file.FileId })
            .HasDatabaseName("ix_google_drive_file_metadata_file_id");
    }
}
