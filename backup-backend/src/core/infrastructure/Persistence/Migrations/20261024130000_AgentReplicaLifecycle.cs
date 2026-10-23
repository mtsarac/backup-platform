using System;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BackupPlatform.Infrastructure.Persistence.Migrations;

[DbContext(typeof(PlatformDbContext))]
[Migration("20261024130000_AgentReplicaLifecycle")]
public partial class AgentReplicaLifecycle : Migration
{
    private const string LocalExecutor = "\"ExecutionLocation\" = 'Agent'";
    private const string AgentExecutor = "\"ExecutionLocation\" = 'Agent' OR (\"ExecutionLocation\" = 'Backend' AND \"StorageProvider\" IN ('sftp','google-drive'))";
    private const string CompletionExecutor = "\"ExecutionLocation\" = 'Agent' OR (\"ExecutionLocation\" = 'Backend' AND \"StorageProvider\" IN ('sftp','google-drive') AND \"DeleteAgentId\" IS NOT NULL)";
    private const string PreviousExecutorConstraint = "(\"ExecutionLocation\" = 'Backend' AND \"OwningAgentId\" IS NULL AND \"DeleteOperationId\" IS NULL AND \"DeleteCompletionToken\" IS NULL) OR (\"ExecutionLocation\" = 'Agent' AND \"StorageProvider\" = 'local' AND \"OwningAgentId\" IS NOT NULL AND \"OwningAgentId\" <> '00000000-0000-0000-0000-000000000000'::uuid)";
    private const string ExecutorConstraint = "(\"DeleteAgentId\" IS NULL OR \"DeleteAgentId\" <> '00000000-0000-0000-0000-000000000000'::uuid) AND ((\"ExecutionLocation\" = 'Backend' AND \"OwningAgentId\" IS NULL AND (\"StorageProvider\" IN ('sftp','google-drive') OR (\"DeleteOperationId\" IS NULL AND \"DeleteCompletionToken\" IS NULL AND \"DeleteAgentId\" IS NULL))) OR (\"ExecutionLocation\" = 'Agent' AND \"StorageProvider\" = 'local' AND \"OwningAgentId\" IS NOT NULL AND \"OwningAgentId\" <> '00000000-0000-0000-0000-000000000000'::uuid))";

    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<Guid>("DeleteAgentId", "backup_replicas", type: "uuid", nullable: true);
        migrationBuilder.Sql("""
            UPDATE backup_replicas
            SET "DeleteAgentId" = "OwningAgentId"
            WHERE "ExecutionLocation" = 'Agent'
                AND ("DeleteLeaseToken" IS NOT NULL OR "DeleteCompletionToken" IS NOT NULL);
            """);
        ReplaceConstraints(migrationBuilder, externalAgents: true);
        migrationBuilder.CreateIndex("ix_backup_replicas_external_agent_deletions", "backup_replicas",
            new[] { "TenantId", "DeleteAfter" },
            filter: "\"ExecutionLocation\" = 'Backend' AND \"StorageProvider\" IN ('sftp','google-drive') AND \"Status\" = 'Deleting'");
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        // Keep the guard and the rollback under one table lock: a concurrent agent must not create authority after this check.
        migrationBuilder.Sql("""
            LOCK TABLE backup_replicas IN ACCESS EXCLUSIVE MODE;
            DO $$
            BEGIN
                IF EXISTS (
                    SELECT 1 FROM backup_replicas
                    WHERE "ExecutionLocation" = 'Backend' AND "StorageProvider" IN ('sftp','google-drive')
                        AND ("DeleteOperationId" IS NOT NULL OR "DeleteAgentId" IS NOT NULL
                            OR "DeleteCompletionToken" IS NOT NULL OR "DeleteCompletionAbsenceVerified" IS NOT NULL
                            OR "DeleteCompletionFailureCode" IS NOT NULL
                            OR ("Status" = 'Deleting' AND "DeleteLeaseToken" IS NULL)
                            OR ("Status" = 'DeleteFailed' AND "DeleteNextAttemptAt" IS NULL))
                ) THEN
                    RAISE EXCEPTION 'Cannot roll back agent replica lifecycle while external agent deletion authority or receipts exist.';
                END IF;
            END $$;
            """);
        ReplaceConstraints(migrationBuilder, externalAgents: false);
        migrationBuilder.DropIndex("ix_backup_replicas_external_agent_deletions", "backup_replicas");
        migrationBuilder.DropColumn("DeleteAgentId", "backup_replicas");
    }

    private static void ReplaceConstraints(MigrationBuilder migrationBuilder, bool externalAgents)
    {
        migrationBuilder.DropCheckConstraint("ck_backup_replica_executor", "backup_replicas");
        migrationBuilder.DropCheckConstraint("ck_backup_replica_completion", "backup_replicas");
        migrationBuilder.DropCheckConstraint("ck_backup_replica_lifecycle", "backup_replicas");
        migrationBuilder.AddCheckConstraint("ck_backup_replica_executor", "backup_replicas", externalAgents ? ExecutorConstraint : PreviousExecutorConstraint);
        migrationBuilder.AddCheckConstraint("ck_backup_replica_completion", "backup_replicas", CompletionConstraint(externalAgents ? CompletionExecutor : LocalExecutor));
        migrationBuilder.AddCheckConstraint("ck_backup_replica_lifecycle", "backup_replicas", LifecycleConstraint(externalAgents ? AgentExecutor : LocalExecutor));
    }

    private static string CompletionConstraint(string agentExecutor) =>
        "(\"DeleteCompletionToken\" IS NULL AND \"DeleteCompletionAbsenceVerified\" IS NULL AND \"DeleteCompletionFailureCode\" IS NULL) OR " +
        $"(({agentExecutor}) AND \"DeleteOperationId\" IS NOT NULL AND \"DeleteCompletionToken\" IS NOT NULL AND \"DeleteCompletionAbsenceVerified\" IS NOT NULL AND " +
        "((\"Status\" = 'Deleted' AND \"DeleteCompletionAbsenceVerified\" AND \"DeleteCompletionFailureCode\" IS NULL) OR " +
        "(\"Status\" = 'DeleteFailed' AND NOT \"DeleteCompletionAbsenceVerified\" AND \"DeleteCompletionFailureCode\" = \"DeleteFailureCode\")))";

    private static string LifecycleConstraint(string agentExecutor) =>
        "(\"DeleteReason\" IS NULL OR \"DeleteReason\" IN ('RetentionExpired','Manual')) AND (\"DeleteRequestedByUserId\" IS NULL OR \"DeleteReason\" = 'Manual') AND (" +
        "(\"Status\" IN ('Available','Quarantined') AND \"DeleteRequestedAt\" IS NULL AND \"DeleteReason\" IS NULL AND \"DeleteAfter\" IS NULL AND \"DeleteLeaseToken\" IS NULL AND \"DeleteLeaseOwner\" IS NULL AND \"DeleteLeaseExpiresAt\" IS NULL " +
        "AND \"DeleteNextAttemptAt\" IS NULL AND \"DeleteFailureCode\" IS NULL AND \"DeletedAt\" IS NULL AND \"DeleteAttempts\" = 0) " +
        "OR (\"Status\" = 'DeletePending' AND \"DeleteRequestedAt\" IS NOT NULL AND \"DeleteReason\" IS NOT NULL AND \"DeleteAfter\" IS NOT NULL AND \"DeleteAfter\" >= \"DeleteRequestedAt\" AND \"DeleteLeaseToken\" IS NULL AND \"DeleteLeaseOwner\" IS NULL " +
        "AND \"DeleteLeaseExpiresAt\" IS NULL AND \"DeleteNextAttemptAt\" IS NULL AND \"DeleteFailureCode\" IS NULL AND \"DeletedAt\" IS NULL AND \"DeleteAttempts\" = 0) " +
        "OR (\"Status\" = 'Deleting' AND \"DeleteRequestedAt\" IS NOT NULL AND \"DeleteReason\" IS NOT NULL AND \"DeleteAfter\" IS NOT NULL AND \"DeletedAt\" IS NULL AND (" +
        "(\"DeleteLeaseToken\" IS NOT NULL AND \"DeleteLeaseOwner\" IS NOT NULL AND \"DeleteLeaseExpiresAt\" IS NOT NULL AND \"DeleteNextAttemptAt\" IS NULL AND \"DeleteAttempts\" >= 1 " +
        "AND (\"ExecutionLocation\" = 'Backend' OR (\"DeleteOperationId\" IS NOT NULL AND \"DeleteFailureCode\" IS NULL))) OR " +
        $"(({agentExecutor}) AND \"DeleteOperationId\" IS NOT NULL AND \"DeleteLeaseToken\" IS NULL AND \"DeleteLeaseOwner\" IS NULL AND \"DeleteLeaseExpiresAt\" IS NULL " +
        "AND \"DeleteNextAttemptAt\" IS NOT NULL AND \"DeleteFailureCode\" = 'agent_unavailable' AND \"DeleteAttempts\" >= 0))) " +
        "OR (\"Status\" = 'DeleteFailed' AND \"DeleteRequestedAt\" IS NOT NULL AND \"DeleteReason\" IS NOT NULL AND \"DeleteAfter\" IS NOT NULL AND \"DeleteLeaseToken\" IS NULL AND \"DeleteLeaseOwner\" IS NULL " +
        $"AND \"DeleteLeaseExpiresAt\" IS NULL AND ({agentExecutor} OR \"DeleteNextAttemptAt\" IS NOT NULL) AND \"DeleteFailureCode\" IS NOT NULL AND \"DeletedAt\" IS NULL AND \"DeleteAttempts\" >= 1) " +
        "OR (\"Status\" = 'Deleted' AND \"DeleteRequestedAt\" IS NOT NULL AND \"DeleteReason\" IS NOT NULL AND \"DeletedAt\" IS NOT NULL AND \"DeleteLeaseToken\" IS NULL AND \"DeleteLeaseOwner\" IS NULL " +
        "AND \"DeleteLeaseExpiresAt\" IS NULL AND \"DeleteNextAttemptAt\" IS NULL AND \"DeleteFailureCode\" IS NULL AND \"DeleteAttempts\" >= 1))";
}
