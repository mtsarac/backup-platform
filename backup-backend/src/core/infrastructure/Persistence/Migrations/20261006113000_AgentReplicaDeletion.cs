using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BackupPlatform.Infrastructure.Persistence.Migrations
{
    public partial class AgentReplicaDeletion : Migration
    {
        private const string ExecutorConstraint = "(\"ExecutionLocation\" = 'Backend' AND \"OwningAgentId\" IS NULL AND \"DeleteOperationId\" IS NULL AND \"DeleteCompletionToken\" IS NULL) OR (\"ExecutionLocation\" = 'Agent' AND \"StorageProvider\" = 'local' AND \"OwningAgentId\" IS NOT NULL AND \"OwningAgentId\" <> '00000000-0000-0000-0000-000000000000'::uuid)";
        private const string CompletionConstraint = "(\"DeleteCompletionToken\" IS NULL AND \"DeleteCompletionAbsenceVerified\" IS NULL AND \"DeleteCompletionFailureCode\" IS NULL) OR (\"ExecutionLocation\" = 'Agent' AND \"DeleteOperationId\" IS NOT NULL AND \"DeleteCompletionToken\" IS NOT NULL AND \"DeleteCompletionAbsenceVerified\" IS NOT NULL AND ((\"Status\" = 'Deleted' AND \"DeleteCompletionAbsenceVerified\" AND \"DeleteCompletionFailureCode\" IS NULL) OR (\"Status\" = 'DeleteFailed' AND NOT \"DeleteCompletionAbsenceVerified\" AND \"DeleteCompletionFailureCode\" = \"DeleteFailureCode\")))";
        private const string LifecycleConstraint = "(\"DeleteReason\" IS NULL OR \"DeleteReason\" IN ('RetentionExpired','Manual')) AND (\"DeleteRequestedByUserId\" IS NULL OR \"DeleteReason\" = 'Manual') AND ((\"Status\" = 'Available' AND \"DeleteRequestedAt\" IS NULL AND \"DeleteReason\" IS NULL AND \"DeleteAfter\" IS NULL AND \"DeleteLeaseToken\" IS NULL AND \"DeleteLeaseOwner\" IS NULL AND \"DeleteLeaseExpiresAt\" IS NULL AND \"DeleteNextAttemptAt\" IS NULL AND \"DeleteFailureCode\" IS NULL AND \"DeletedAt\" IS NULL AND \"DeleteAttempts\" = 0) OR (\"Status\" = 'DeletePending' AND \"DeleteRequestedAt\" IS NOT NULL AND \"DeleteReason\" IS NOT NULL AND \"DeleteAfter\" IS NOT NULL AND \"DeleteAfter\" >= \"DeleteRequestedAt\" AND \"DeleteLeaseToken\" IS NULL AND \"DeleteLeaseOwner\" IS NULL AND \"DeleteLeaseExpiresAt\" IS NULL AND \"DeleteNextAttemptAt\" IS NULL AND \"DeleteFailureCode\" IS NULL AND \"DeletedAt\" IS NULL AND \"DeleteAttempts\" = 0) OR (\"Status\" = 'Deleting' AND \"DeleteRequestedAt\" IS NOT NULL AND \"DeleteReason\" IS NOT NULL AND \"DeleteAfter\" IS NOT NULL AND \"DeletedAt\" IS NULL AND ((\"DeleteLeaseToken\" IS NOT NULL AND \"DeleteLeaseOwner\" IS NOT NULL AND \"DeleteLeaseExpiresAt\" IS NOT NULL AND \"DeleteNextAttemptAt\" IS NULL AND \"DeleteAttempts\" >= 1 AND (\"ExecutionLocation\" = 'Backend' OR (\"DeleteOperationId\" IS NOT NULL AND \"DeleteFailureCode\" IS NULL))) OR (\"ExecutionLocation\" = 'Agent' AND \"DeleteOperationId\" IS NOT NULL AND \"DeleteLeaseToken\" IS NULL AND \"DeleteLeaseOwner\" IS NULL AND \"DeleteLeaseExpiresAt\" IS NULL AND \"DeleteNextAttemptAt\" IS NOT NULL AND \"DeleteFailureCode\" = 'agent_unavailable' AND \"DeleteAttempts\" >= 0))) OR (\"Status\" = 'DeleteFailed' AND \"DeleteRequestedAt\" IS NOT NULL AND \"DeleteReason\" IS NOT NULL AND \"DeleteAfter\" IS NOT NULL AND \"DeleteLeaseToken\" IS NULL AND \"DeleteLeaseOwner\" IS NULL AND \"DeleteLeaseExpiresAt\" IS NULL AND (\"ExecutionLocation\" = 'Agent' OR \"DeleteNextAttemptAt\" IS NOT NULL) AND \"DeleteFailureCode\" IS NOT NULL AND \"DeletedAt\" IS NULL AND \"DeleteAttempts\" >= 1) OR (\"Status\" = 'Deleted' AND \"DeleteRequestedAt\" IS NOT NULL AND \"DeleteReason\" IS NOT NULL AND \"DeletedAt\" IS NOT NULL AND \"DeleteLeaseToken\" IS NULL AND \"DeleteLeaseOwner\" IS NULL AND \"DeleteLeaseExpiresAt\" IS NULL AND \"DeleteNextAttemptAt\" IS NULL AND \"DeleteFailureCode\" IS NULL AND \"DeleteAttempts\" >= 1))";
        private const string PreviousLifecycleConstraint = "(\"DeleteReason\" IS NULL OR \"DeleteReason\" IN ('RetentionExpired','Manual')) AND (\"DeleteRequestedByUserId\" IS NULL OR \"DeleteReason\" = 'Manual') AND ((\"Status\" = 'Available' AND \"DeleteRequestedAt\" IS NULL AND \"DeleteReason\" IS NULL AND \"DeleteAfter\" IS NULL AND \"DeleteLeaseToken\" IS NULL AND \"DeleteLeaseOwner\" IS NULL AND \"DeleteLeaseExpiresAt\" IS NULL AND \"DeleteNextAttemptAt\" IS NULL AND \"DeleteFailureCode\" IS NULL AND \"DeletedAt\" IS NULL AND \"DeleteAttempts\" = 0) OR (\"Status\" = 'DeletePending' AND \"DeleteRequestedAt\" IS NOT NULL AND \"DeleteReason\" IS NOT NULL AND \"DeleteAfter\" IS NOT NULL AND \"DeleteAfter\" >= \"DeleteRequestedAt\" AND \"DeleteLeaseToken\" IS NULL AND \"DeleteLeaseOwner\" IS NULL AND \"DeleteLeaseExpiresAt\" IS NULL AND \"DeleteNextAttemptAt\" IS NULL AND \"DeleteFailureCode\" IS NULL AND \"DeletedAt\" IS NULL AND \"DeleteAttempts\" = 0) OR (\"Status\" = 'Deleting' AND \"DeleteRequestedAt\" IS NOT NULL AND \"DeleteReason\" IS NOT NULL AND \"DeleteAfter\" IS NOT NULL AND \"DeleteLeaseToken\" IS NOT NULL AND \"DeleteLeaseOwner\" IS NOT NULL AND \"DeleteLeaseExpiresAt\" IS NOT NULL AND \"DeleteNextAttemptAt\" IS NULL AND \"DeletedAt\" IS NULL AND \"DeleteAttempts\" >= 1) OR (\"Status\" = 'DeleteFailed' AND \"DeleteRequestedAt\" IS NOT NULL AND \"DeleteReason\" IS NOT NULL AND \"DeleteAfter\" IS NOT NULL AND \"DeleteLeaseToken\" IS NULL AND \"DeleteLeaseOwner\" IS NULL AND \"DeleteLeaseExpiresAt\" IS NULL AND \"DeleteNextAttemptAt\" IS NOT NULL AND \"DeleteFailureCode\" IS NOT NULL AND \"DeletedAt\" IS NULL AND \"DeleteAttempts\" >= 1) OR (\"Status\" = 'Deleted' AND \"DeleteRequestedAt\" IS NOT NULL AND \"DeleteReason\" IS NOT NULL AND \"DeletedAt\" IS NOT NULL AND \"DeleteLeaseToken\" IS NULL AND \"DeleteLeaseOwner\" IS NULL AND \"DeleteLeaseExpiresAt\" IS NULL AND \"DeleteNextAttemptAt\" IS NULL AND \"DeleteFailureCode\" IS NULL AND \"DeleteAttempts\" >= 1))";

        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint("ck_backup_replica_lifecycle", "backup_replicas");
            migrationBuilder.AddColumn<string>("ExecutionLocation", "backup_replicas", type: "character varying(16)", maxLength: 16, nullable: true);
            foreach (var column in new[] { "OwningAgentId", "DeleteOperationId", "DeleteCompletionToken" })
                migrationBuilder.AddColumn<Guid>(column, "backup_replicas", type: "uuid", nullable: true);
            migrationBuilder.AddColumn<bool>("DeleteCompletionAbsenceVerified", "backup_replicas", type: "boolean", nullable: true);
            migrationBuilder.AddColumn<string>("DeleteCompletionFailureCode", "backup_replicas", type: "character varying(128)", maxLength: 128, nullable: true);

            // Location comes only from the persisted provider. Local owners are resolved through the immutable artifact/attempt/run chain.
            migrationBuilder.Sql("""
                UPDATE backup_replicas replica
                SET "ExecutionLocation" = CASE WHEN replica."StorageProvider" = 'local' THEN 'Agent' ELSE 'Backend' END,
                    "OwningAgentId" = CASE WHEN replica."StorageProvider" = 'local' THEN run."AgentId" ELSE NULL END
                FROM backup_artifacts artifact
                JOIN backup_run_attempts attempt ON attempt."Id" = artifact."AttemptId" AND attempt."TenantId" = artifact."TenantId"
                JOIN backup_runs run ON run."Id" = attempt."RunId" AND run."TenantId" = attempt."TenantId"
                WHERE artifact."Id" = replica."ArtifactId" AND artifact."TenantId" = replica."TenantId";

                UPDATE backup_jobs SET "Configuration" = "Configuration" || jsonb_build_object('StorageExecutionLocation',
                    CASE WHEN "Configuration"->>'StorageProvider' = 'local' THEN 2 ELSE 1 END)
                WHERE NOT "Configuration" ? 'StorageExecutionLocation';
                UPDATE backup_runs SET "Configuration" = "Configuration" || jsonb_build_object('StorageExecutionLocation',
                    CASE WHEN "Configuration"->>'StorageProvider' = 'local' THEN 2 ELSE 1 END)
                WHERE NOT "Configuration" ? 'StorageExecutionLocation';

                UPDATE backup_replicas
                SET "DeleteOperationId" = gen_random_uuid(), "DeleteLeaseToken" = NULL, "DeleteLeaseOwner" = NULL,
                    "DeleteLeaseExpiresAt" = NULL, "DeleteNextAttemptAt" = CURRENT_TIMESTAMP + interval '1 minute',
                    "DeleteFailureCode" = 'agent_unavailable'
                WHERE "ExecutionLocation" = 'Agent' AND "Status" = 'Deleting';
                """);
            migrationBuilder.AlterColumn<string>("ExecutionLocation", "backup_replicas", type: "character varying(16)", maxLength: 16,
                nullable: false, oldClrType: typeof(string), oldType: "character varying(16)", oldMaxLength: 16, oldNullable: true);
            migrationBuilder.CreateIndex("ix_backup_replicas_agent_deletions", "backup_replicas",
                new[] { "TenantId", "OwningAgentId", "DeleteAfter" }, filter: "\"ExecutionLocation\" = 'Agent' AND \"Status\" = 'Deleting'");
            migrationBuilder.AddCheckConstraint("ck_backup_replica_executor", "backup_replicas", ExecutorConstraint);
            migrationBuilder.AddCheckConstraint("ck_backup_replica_completion", "backup_replicas", CompletionConstraint);
            migrationBuilder.AddCheckConstraint("ck_backup_replica_lifecycle", "backup_replicas", LifecycleConstraint);
        }

        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint("ck_backup_replica_executor", "backup_replicas");
            migrationBuilder.DropCheckConstraint("ck_backup_replica_completion", "backup_replicas");
            migrationBuilder.DropCheckConstraint("ck_backup_replica_lifecycle", "backup_replicas");
            migrationBuilder.DropIndex("ix_backup_replicas_agent_deletions", "backup_replicas");
            // Old code cannot represent unclaimed dispatch or a terminal failure without a retry. Do not manufacture a backend lease.
            migrationBuilder.Sql("""
                UPDATE backup_replicas SET "Status" = 'DeleteFailed', "DeleteFailureCode" = 'agent_unavailable',
                    "DeleteNextAttemptAt" = CURRENT_TIMESTAMP + interval '1 minute', "DeleteAttempts" = GREATEST("DeleteAttempts", 1)
                WHERE "ExecutionLocation" = 'Agent' AND "Status" = 'Deleting' AND "DeleteLeaseToken" IS NULL;
                UPDATE backup_replicas SET "DeleteNextAttemptAt" = CURRENT_TIMESTAMP + interval '1 minute'
                WHERE "Status" = 'DeleteFailed' AND "DeleteNextAttemptAt" IS NULL;
                UPDATE backup_jobs SET "Configuration" = "Configuration" - 'StorageExecutionLocation';
                UPDATE backup_runs SET "Configuration" = "Configuration" - 'StorageExecutionLocation';
                """);
            foreach (var column in new[] { "ExecutionLocation", "OwningAgentId", "DeleteOperationId", "DeleteCompletionToken",
                "DeleteCompletionAbsenceVerified", "DeleteCompletionFailureCode" })
                migrationBuilder.DropColumn(column, "backup_replicas");
            migrationBuilder.AddCheckConstraint("ck_backup_replica_lifecycle", "backup_replicas", PreviousLifecycleConstraint);
        }
    }
}
