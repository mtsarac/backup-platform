using System.Text.Json;
using BackupPlatform.Domain.Storage;
using BackupPlatform.Infrastructure.Secrets;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace BackupPlatform.Infrastructure.Persistence.Configurations;

/// <summary>
/// Storage connections. The database backs the domain's rules: provider type and quota scope are well-formed labels, a tenant has at most one
/// system default per provider type and one connection per (case-insensitive) name, and a replica can only point at a connection of its own
/// tenant AND its own provider (composite foreign key), so no write path can attach a replica to someone else's destination.
/// </summary>
internal sealed class StorageConnectionConfiguration : IEntityTypeConfiguration<StorageConnection>
{
    public void Configure(EntityTypeBuilder<StorageConnection> builder)
    {
        BackupMapping.Base(builder, "storage_connections");
        builder.ToTable("storage_connections", table =>
        {
            table.HasCheckConstraint("ck_storage_connections_provider", "\"ProviderType\" ~ '^[a-z0-9]([a-z0-9-]*[a-z0-9])?$' AND length(\"ProviderType\") <= 32");
            table.HasCheckConstraint("ck_storage_connections_ownership", "\"Ownership\" IN ('PlatformManaged','TenantManaged')");
            table.HasCheckConstraint("ck_storage_connections_scope", "\"QuotaScope\" ~ '^[a-z0-9]([a-z0-9-]*[a-z0-9])?$' AND length(\"QuotaScope\") <= 64");
            table.HasCheckConstraint("ck_storage_connections_name", "length(btrim(\"Name\")) BETWEEN 1 AND 100 AND \"NormalizedName\" = lower(btrim(\"Name\"))");
            table.HasCheckConstraint("ck_storage_connections_test", "(\"LastTestStatus\" IS NULL) = (\"LastTestedAt\" IS NULL) AND (\"LastTestStatus\" IS NULL OR \"LastTestStatus\" IN ('Succeeded','Failed','NotTestable'))");
            table.HasCheckConstraint("ck_storage_connections_secret", "(\"SecretReference\" IS NULL) OR (length(\"SecretReference\") BETWEEN 1 AND 128 AND \"SecretUpdatedAt\" IS NOT NULL)");
        });
        builder.Property(connection => connection.Name).HasMaxLength(StorageConnection.MaximumNameLength).IsRequired();
        builder.Property(connection => connection.NormalizedName).HasMaxLength(StorageConnection.MaximumNameLength).IsRequired();
        // Same width as backup_replicas.StorageProvider so the composite foreign key joins like-for-like columns.
        builder.Property(connection => connection.ProviderType).HasMaxLength(128).IsRequired();
        builder.Property(connection => connection.Ownership).HasConversion<string>().HasMaxLength(16).IsRequired();
        builder.Property(connection => connection.QuotaScope).HasMaxLength(StorageQuotaScopes.MaximumLength).IsRequired();
        builder.Property(connection => connection.Enabled).IsRequired();
        builder.Property(connection => connection.AdministrativelyDisabled).HasDefaultValue(false).IsRequired();
        builder.Ignore(connection => connection.EffectiveEnabled);
        builder.Property(connection => connection.IsDefault).IsRequired();
        builder.Property(connection => connection.Configuration)
            .HasConversion(
                value => JsonSerializer.Serialize(value, (JsonSerializerOptions?)null),
                value => JsonSerializer.Deserialize<Dictionary<string, string>>(value, (JsonSerializerOptions?)null)!,
                new ValueComparer<IReadOnlyDictionary<string, string>>(
                    (left, right) => left!.Count == right!.Count && left.All(pair => right.ContainsKey(pair.Key) && right[pair.Key] == pair.Value),
                    value => value.Aggregate(0, (hash, pair) => HashCode.Combine(hash, pair.Key, pair.Value)),
                    value => new Dictionary<string, string>(value)))
            .HasColumnName("ConfigurationJson").HasColumnType("jsonb").IsRequired();
        builder.Property(connection => connection.SecretFieldNames)
            .HasConversion(
                value => JsonSerializer.Serialize(value, (JsonSerializerOptions?)null),
                value => JsonSerializer.Deserialize<List<string>>(value, (JsonSerializerOptions?)null)!,
                new ValueComparer<IReadOnlyList<string>>(
                    (left, right) => left!.SequenceEqual(right!),
                    value => value.Aggregate(0, (hash, item) => HashCode.Combine(hash, item)),
                    value => value.ToList()))
            .HasColumnName("SecretFieldNamesJson").HasColumnType("jsonb").IsRequired();
        builder.Property(connection => connection.SecretReference).HasMaxLength(128);
        builder.Property(connection => connection.SecretUpdatedAt).HasColumnType("timestamp with time zone");
        builder.Property(connection => connection.LastTestStatus).HasConversion<string>().HasMaxLength(16);
        builder.Property(connection => connection.LastTestCode).HasMaxLength(64);
        builder.Property(connection => connection.LastTestedAt).HasColumnType("timestamp with time zone");
        builder.Property(connection => connection.CreatedAt).HasColumnType("timestamp with time zone").IsRequired();
        builder.Property(connection => connection.UpdatedAt).HasColumnType("timestamp with time zone").IsRequired();
        builder.Property(connection => connection.StateChangedAt).HasColumnType("timestamp with time zone");
        builder.Ignore(connection => connection.HasSecret);
        // The composite key replicas reference: tenant AND provider type travel with the id.
        builder.HasAlternateKey(connection => new { connection.Id, connection.TenantId, connection.ProviderType }).HasName("ak_storage_connections_target");
        builder.HasIndex(connection => new { connection.TenantId, connection.NormalizedName }).IsUnique().HasDatabaseName("ux_storage_connections_name");
        builder.HasIndex(connection => new { connection.TenantId, connection.ProviderType }).IsUnique().HasDatabaseName("ux_storage_connections_default").HasFilter("\"IsDefault\"");
        builder.HasIndex(connection => new { connection.Ownership, connection.ProviderType }).HasDatabaseName("ix_storage_connections_ownership");
    }
}

/// <summary>Opaque ciphertext of the secret provider, by reference name. Holds nothing a database dump could use without the host's Data Protection keys.</summary>
internal sealed class SecretCiphertextConfiguration : IEntityTypeConfiguration<SecretCiphertextRecord>
{
    public void Configure(EntityTypeBuilder<SecretCiphertextRecord> builder)
    {
        builder.ToTable("platform_secrets", table => table.HasCheckConstraint("ck_platform_secrets_name", "length(\"Name\") BETWEEN 1 AND 128"));
        builder.HasKey(record => record.Name);
        builder.Property(record => record.Name).HasMaxLength(128).ValueGeneratedNever();
        builder.Property(record => record.Ciphertext).IsRequired();
        builder.Property(record => record.CreatedAt).HasColumnType("timestamp with time zone").IsRequired();
        builder.Property(record => record.UpdatedAt).HasColumnType("timestamp with time zone").IsRequired();
    }
}
