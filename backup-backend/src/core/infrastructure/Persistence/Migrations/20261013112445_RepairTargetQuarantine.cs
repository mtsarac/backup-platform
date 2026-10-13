using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BackupPlatform.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class RepairTargetQuarantine : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "ck_backup_replica_lifecycle",
                table: "backup_replicas");

            migrationBuilder.DropCheckConstraint(
                name: "ck_backup_replica_status",
                table: "backup_replicas");

            migrationBuilder.AddCheckConstraint(
                name: "ck_backup_replica_lifecycle",
                table: "backup_replicas",
                sql: "(\"DeleteReason\" IS NULL OR \"DeleteReason\" IN ('RetentionExpired','Manual')) AND (\"DeleteRequestedByUserId\" IS NULL OR \"DeleteReason\" = 'Manual') AND ((\"Status\" IN ('Available','Quarantined') AND \"DeleteRequestedAt\" IS NULL AND \"DeleteReason\" IS NULL AND \"DeleteAfter\" IS NULL AND \"DeleteLeaseToken\" IS NULL AND \"DeleteLeaseOwner\" IS NULL AND \"DeleteLeaseExpiresAt\" IS NULL AND \"DeleteNextAttemptAt\" IS NULL AND \"DeleteFailureCode\" IS NULL AND \"DeletedAt\" IS NULL AND \"DeleteAttempts\" = 0) OR (\"Status\" = 'DeletePending' AND \"DeleteRequestedAt\" IS NOT NULL AND \"DeleteReason\" IS NOT NULL AND \"DeleteAfter\" IS NOT NULL AND \"DeleteAfter\" >= \"DeleteRequestedAt\" AND \"DeleteLeaseToken\" IS NULL AND \"DeleteLeaseOwner\" IS NULL AND \"DeleteLeaseExpiresAt\" IS NULL AND \"DeleteNextAttemptAt\" IS NULL AND \"DeleteFailureCode\" IS NULL AND \"DeletedAt\" IS NULL AND \"DeleteAttempts\" = 0) OR (\"Status\" = 'Deleting' AND \"DeleteRequestedAt\" IS NOT NULL AND \"DeleteReason\" IS NOT NULL AND \"DeleteAfter\" IS NOT NULL AND \"DeletedAt\" IS NULL AND ((\"DeleteLeaseToken\" IS NOT NULL AND \"DeleteLeaseOwner\" IS NOT NULL AND \"DeleteLeaseExpiresAt\" IS NOT NULL AND \"DeleteNextAttemptAt\" IS NULL AND \"DeleteAttempts\" >= 1 AND (\"ExecutionLocation\" = 'Backend' OR (\"DeleteOperationId\" IS NOT NULL AND \"DeleteFailureCode\" IS NULL))) OR (\"ExecutionLocation\" = 'Agent' AND \"DeleteOperationId\" IS NOT NULL AND \"DeleteLeaseToken\" IS NULL AND \"DeleteLeaseOwner\" IS NULL AND \"DeleteLeaseExpiresAt\" IS NULL AND \"DeleteNextAttemptAt\" IS NOT NULL AND \"DeleteFailureCode\" = 'agent_unavailable' AND \"DeleteAttempts\" >= 0))) OR (\"Status\" = 'DeleteFailed' AND \"DeleteRequestedAt\" IS NOT NULL AND \"DeleteReason\" IS NOT NULL AND \"DeleteAfter\" IS NOT NULL AND \"DeleteLeaseToken\" IS NULL AND \"DeleteLeaseOwner\" IS NULL AND \"DeleteLeaseExpiresAt\" IS NULL AND (\"ExecutionLocation\" = 'Agent' OR \"DeleteNextAttemptAt\" IS NOT NULL) AND \"DeleteFailureCode\" IS NOT NULL AND \"DeletedAt\" IS NULL AND \"DeleteAttempts\" >= 1) OR (\"Status\" = 'Deleted' AND \"DeleteRequestedAt\" IS NOT NULL AND \"DeleteReason\" IS NOT NULL AND \"DeletedAt\" IS NOT NULL AND \"DeleteLeaseToken\" IS NULL AND \"DeleteLeaseOwner\" IS NULL AND \"DeleteLeaseExpiresAt\" IS NULL AND \"DeleteNextAttemptAt\" IS NULL AND \"DeleteFailureCode\" IS NULL AND \"DeleteAttempts\" >= 1))");

            migrationBuilder.AddCheckConstraint(
                name: "ck_backup_replica_status",
                table: "backup_replicas",
                sql: "\"Status\" IN ('Available','DeletePending','Deleting','DeleteFailed','Deleted','Quarantined')");

            // Older AlreadyExists failures retained intent but never registered the object for reconciliation.
            migrationBuilder.Sql("""
                INSERT INTO backup_replicas (
                    "Id", "TenantId", "ArtifactId", "StorageProvider", "StorageTarget", "StorageConnectionId", "Key",
                    "SizeBytes", "Checksum", "ChecksumAlgorithm", "VerificationState", "VerificationMethod",
                    "CreatedAt", "Status", "ExecutionLocation", "DeleteAttempts")
                SELECT DISTINCT ON (repair."TenantId", repair."StorageConnectionId", repair."TargetKey")
                    gen_random_uuid(), repair."TenantId", repair."ArtifactId", repair."StorageProvider",
                    CASE WHEN repair."StorageProvider" IN ('sftp', 'google-drive')
                        AND repair."StorageConnectionId" <> '00000000-0000-0000-0000-000000000000'::uuid
                        THEN repair."StorageConnectionId"::text ELSE repair."StorageProvider" END,
                    repair."StorageConnectionId", repair."TargetKey", GREATEST(1, repair."TotalBytes"),
                    artifact."ArtifactChecksum", artifact."ChecksumAlgorithm", 'Failed', 'RepairTargetConflict',
                    repair."CompletedAt", 'Quarantined', 'Backend', 0
                FROM replica_repair_operations repair
                JOIN backup_artifacts artifact ON artifact."TenantId" = repair."TenantId" AND artifact."Id" = repair."ArtifactId"
                WHERE repair."Status" = 'Failed' AND repair."FailureCode" = 'AlreadyVerified'
                    AND repair."TargetKey" IS NOT NULL AND repair."TargetKey" <> ''
                    AND NOT EXISTS (SELECT 1 FROM backup_replicas replica
                        WHERE replica."TenantId" = repair."TenantId" AND replica."StorageConnectionId" = repair."StorageConnectionId"
                            AND replica."Key" = repair."TargetKey")
                ORDER BY repair."TenantId", repair."StorageConnectionId", repair."TargetKey", repair."CompletedAt" DESC;

                UPDATE replica_repair_operations
                SET "FailureCode" = 'TargetConflict', "LastFailureCode" = 'TargetConflict',
                    "FailureReason" = 'Target object conflicts with expected content; ownership requires reconciliation.'
                WHERE "Status" = 'Failed' AND "FailureCode" = 'AlreadyVerified' AND "TargetKey" IS NOT NULL AND "TargetKey" <> '';

                -- Rebuild derived quota projections through the existing missing-row path.
                DELETE FROM tenant_storage_usage usage
                WHERE EXISTS (SELECT 1 FROM backup_replicas replica
                    WHERE replica."TenantId" = usage."TenantId" AND replica."Status" = 'Quarantined'
                        AND replica."VerificationMethod" = 'RepairTargetConflict');
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                DO $$
                BEGIN
                    IF EXISTS (SELECT 1 FROM backup_replicas WHERE "Status" = 'Quarantined') THEN
                        RAISE EXCEPTION 'Reconcile quarantined repair targets before downgrading; ownership cannot be discarded.';
                    END IF;
                END $$;
                """);
            migrationBuilder.DropCheckConstraint(
                name: "ck_backup_replica_lifecycle",
                table: "backup_replicas");

            migrationBuilder.DropCheckConstraint(
                name: "ck_backup_replica_status",
                table: "backup_replicas");

            migrationBuilder.AddCheckConstraint(
                name: "ck_backup_replica_lifecycle",
                table: "backup_replicas",
                sql: "(\"DeleteReason\" IS NULL OR \"DeleteReason\" IN ('RetentionExpired','Manual')) AND (\"DeleteRequestedByUserId\" IS NULL OR \"DeleteReason\" = 'Manual') AND ((\"Status\" = 'Available' AND \"DeleteRequestedAt\" IS NULL AND \"DeleteReason\" IS NULL AND \"DeleteAfter\" IS NULL AND \"DeleteLeaseToken\" IS NULL AND \"DeleteLeaseOwner\" IS NULL AND \"DeleteLeaseExpiresAt\" IS NULL AND \"DeleteNextAttemptAt\" IS NULL AND \"DeleteFailureCode\" IS NULL AND \"DeletedAt\" IS NULL AND \"DeleteAttempts\" = 0) OR (\"Status\" = 'DeletePending' AND \"DeleteRequestedAt\" IS NOT NULL AND \"DeleteReason\" IS NOT NULL AND \"DeleteAfter\" IS NOT NULL AND \"DeleteAfter\" >= \"DeleteRequestedAt\" AND \"DeleteLeaseToken\" IS NULL AND \"DeleteLeaseOwner\" IS NULL AND \"DeleteLeaseExpiresAt\" IS NULL AND \"DeleteNextAttemptAt\" IS NULL AND \"DeleteFailureCode\" IS NULL AND \"DeletedAt\" IS NULL AND \"DeleteAttempts\" = 0) OR (\"Status\" = 'Deleting' AND \"DeleteRequestedAt\" IS NOT NULL AND \"DeleteReason\" IS NOT NULL AND \"DeleteAfter\" IS NOT NULL AND \"DeletedAt\" IS NULL AND ((\"DeleteLeaseToken\" IS NOT NULL AND \"DeleteLeaseOwner\" IS NOT NULL AND \"DeleteLeaseExpiresAt\" IS NOT NULL AND \"DeleteNextAttemptAt\" IS NULL AND \"DeleteAttempts\" >= 1 AND (\"ExecutionLocation\" = 'Backend' OR (\"DeleteOperationId\" IS NOT NULL AND \"DeleteFailureCode\" IS NULL))) OR (\"ExecutionLocation\" = 'Agent' AND \"DeleteOperationId\" IS NOT NULL AND \"DeleteLeaseToken\" IS NULL AND \"DeleteLeaseOwner\" IS NULL AND \"DeleteLeaseExpiresAt\" IS NULL AND \"DeleteNextAttemptAt\" IS NOT NULL AND \"DeleteFailureCode\" = 'agent_unavailable' AND \"DeleteAttempts\" >= 0))) OR (\"Status\" = 'DeleteFailed' AND \"DeleteRequestedAt\" IS NOT NULL AND \"DeleteReason\" IS NOT NULL AND \"DeleteAfter\" IS NOT NULL AND \"DeleteLeaseToken\" IS NULL AND \"DeleteLeaseOwner\" IS NULL AND \"DeleteLeaseExpiresAt\" IS NULL AND (\"ExecutionLocation\" = 'Agent' OR \"DeleteNextAttemptAt\" IS NOT NULL) AND \"DeleteFailureCode\" IS NOT NULL AND \"DeletedAt\" IS NULL AND \"DeleteAttempts\" >= 1) OR (\"Status\" = 'Deleted' AND \"DeleteRequestedAt\" IS NOT NULL AND \"DeleteReason\" IS NOT NULL AND \"DeletedAt\" IS NOT NULL AND \"DeleteLeaseToken\" IS NULL AND \"DeleteLeaseOwner\" IS NULL AND \"DeleteLeaseExpiresAt\" IS NULL AND \"DeleteNextAttemptAt\" IS NULL AND \"DeleteFailureCode\" IS NULL AND \"DeleteAttempts\" >= 1))");

            migrationBuilder.AddCheckConstraint(
                name: "ck_backup_replica_status",
                table: "backup_replicas",
                sql: "\"Status\" IN ('Available','DeletePending','Deleting','DeleteFailed','Deleted')");
        }
    }
}
