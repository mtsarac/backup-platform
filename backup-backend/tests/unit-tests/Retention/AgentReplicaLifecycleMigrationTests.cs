using BackupPlatform.Domain.Backups;
using BackupPlatform.Infrastructure.Persistence;
using BackupPlatform.Infrastructure.Persistence.Migrations;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.EntityFrameworkCore.Migrations.Operations;

namespace BackupPlatform.UnitTests;

public sealed class AgentReplicaLifecycleMigrationTests
{
    [Fact]
    public void Upgrade_adds_nullable_claim_attribution_and_backfills_only_previously_claimed_local_deletions()
    {
        var operations = new AgentReplicaLifecycle().UpOperations;
        var column = Assert.Single(operations.OfType<AddColumnOperation>());
        Assert.Equal("backup_replicas", column.Table);
        Assert.Equal(nameof(BackupReplica.DeleteAgentId), column.Name);
        Assert.Equal("uuid", column.ColumnType);
        Assert.True(column.IsNullable);
        Assert.Null(column.DefaultValue);
        Assert.Null(column.DefaultValueSql);

        var backfill = Assert.Single(operations.OfType<SqlOperation>());
        Assert.Contains("SET \"DeleteAgentId\" = \"OwningAgentId\"", backfill.Sql);
        Assert.Contains("WHERE \"ExecutionLocation\" = 'Agent'", backfill.Sql);
        Assert.Contains("(\"DeleteLeaseToken\" IS NOT NULL OR \"DeleteCompletionToken\" IS NOT NULL)", backfill.Sql);
        Assert.DoesNotContain("gen_random_uuid", backfill.Sql);
        Assert.DoesNotContain("SET \"DeleteLeaseToken\"", backfill.Sql);
        Assert.DoesNotContain("\"Key\"", backfill.Sql);
        Assert.DoesNotContain("\"StorageProvider\"", backfill.Sql);
        Assert.DoesNotContain(operations, operation => operation is DropColumnOperation or DropTableOperation or AlterColumnOperation);
    }

    [Fact]
    public void New_constraints_and_nullable_claim_mapping_match_both_the_current_model_and_snapshot()
    {
        var options = new DbContextOptionsBuilder<PlatformDbContext>()
            .UseNpgsql("Host=localhost;Database=model_only;Username=model_only;Password=unused")
            .Options;
        using var context = PlatformDbContext.CreateForPlatformOperations(options);
        var current = context.GetService<IDesignTimeModel>().Model.FindEntityType(typeof(BackupReplica));
        var snapshot = context.GetService<IMigrationsAssembly>().ModelSnapshot!.Model.FindEntityType(typeof(BackupReplica).FullName!);
        Assert.NotNull(current);
        Assert.NotNull(snapshot);

        foreach (var entity in new[] { current, snapshot })
        {
            var owner = entity.FindProperty(nameof(BackupReplica.DeleteAgentId));
            Assert.NotNull(owner);
            Assert.Equal(typeof(Guid?), owner.ClrType);
            Assert.True(owner.IsNullable);
            Assert.Equal("uuid", owner.GetColumnType());
            foreach (var constraint in new AgentReplicaLifecycle().UpOperations.OfType<AddCheckConstraintOperation>())
            {
                Assert.Equal(constraint.Sql, entity.GetCheckConstraints().Single(check => check.Name == constraint.Name).Sql);
            }
        }
    }

    [Fact]
    public void External_deleting_claims_have_a_tenant_scoped_partial_index_without_changing_local_claim_indexes()
    {
        var index = Assert.Single(new AgentReplicaLifecycle().UpOperations.OfType<CreateIndexOperation>());
        Assert.Equal("ix_backup_replicas_external_agent_deletions", index.Name);
        Assert.Equal("backup_replicas", index.Table);
        Assert.Equal(["TenantId", "DeleteAfter"], index.Columns);
        Assert.Equal("\"ExecutionLocation\" = 'Backend' AND \"StorageProvider\" IN ('sftp','google-drive') AND \"Status\" = 'Deleting'", index.Filter);
        Assert.DoesNotContain(new AgentReplicaLifecycle().UpOperations, operation => operation is DropIndexOperation);
    }

    [Fact]
    public void Rollback_checks_external_authority_before_dropping_any_schema_and_never_rewrites_deletion_state()
    {
        var operations = new AgentReplicaLifecycle().DownOperations;
        var guard = Assert.IsType<SqlOperation>(operations[0]);
        Assert.Contains("LOCK TABLE backup_replicas IN ACCESS EXCLUSIVE MODE", guard.Sql);
        Assert.Contains("RAISE EXCEPTION", guard.Sql);
        Assert.Contains("\"ExecutionLocation\" = 'Backend' AND \"StorageProvider\" IN ('sftp','google-drive')", guard.Sql);
        Assert.Contains("\"DeleteOperationId\" IS NOT NULL", guard.Sql);
        Assert.Contains("\"DeleteAgentId\" IS NOT NULL", guard.Sql);
        Assert.Contains("\"DeleteCompletionToken\" IS NOT NULL", guard.Sql);
        Assert.Contains("\"DeleteCompletionAbsenceVerified\" IS NOT NULL", guard.Sql);
        Assert.Contains("\"DeleteCompletionFailureCode\" IS NOT NULL", guard.Sql);
        Assert.Contains("\"Status\" = 'Deleting' AND \"DeleteLeaseToken\" IS NULL", guard.Sql);
        Assert.Contains("\"Status\" = 'DeleteFailed' AND \"DeleteNextAttemptAt\" IS NULL", guard.Sql);
        Assert.False(guard.SuppressTransaction);
        Assert.DoesNotContain("UPDATE ", guard.Sql);
        Assert.DoesNotContain("DELETE FROM", guard.Sql);
        var dropped = Assert.Single(operations.OfType<DropColumnOperation>());
        Assert.Equal(nameof(BackupReplica.DeleteAgentId), dropped.Name);
        Assert.Equal("backup_replicas", dropped.Table);
        Assert.Equal(3, operations.OfType<AddCheckConstraintOperation>().Count());
        Assert.All(operations.OfType<AddCheckConstraintOperation>(), constraint => Assert.DoesNotContain("\"DeleteAgentId\"", constraint.Sql));
        Assert.Equal("ix_backup_replicas_external_agent_deletions", Assert.Single(operations.OfType<DropIndexOperation>()).Name);
    }
}
