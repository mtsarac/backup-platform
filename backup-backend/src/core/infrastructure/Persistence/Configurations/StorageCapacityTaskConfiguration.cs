using BackupPlatform.Domain.Agents;
using BackupPlatform.Domain.Storage;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace BackupPlatform.Infrastructure.Persistence.Configurations;

internal sealed class StorageCapacityTaskConfiguration : IEntityTypeConfiguration<StorageCapacityTask>
{
    public void Configure(EntityTypeBuilder<StorageCapacityTask> builder)
    {
        BackupMapping.Base(builder, "storage_capacity_tasks");
        builder.Property(task => task.CompletionJson).HasColumnType("jsonb");
        builder.HasIndex(task => new { task.TenantId, task.ConnectionId, task.AgentId }).IsUnique()
            .HasFilter("\"CompletedAt\" IS NULL").HasDatabaseName("ux_storage_capacity_tasks_pending");
        builder.HasIndex(task => new { task.TenantId, task.AgentId, task.CreatedAt });
        builder.HasOne<StorageConnection>().WithMany().HasForeignKey(task => new { task.ConnectionId, task.TenantId })
            .HasPrincipalKey(connection => new { connection.Id, connection.TenantId }).OnDelete(DeleteBehavior.Cascade);
    }
}
