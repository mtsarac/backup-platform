using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BackupPlatform.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class RestoreOperations : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "restore_operations",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    TenantId = table.Column<Guid>(type: "uuid", nullable: false),
                    ArtifactId = table.Column<Guid>(type: "uuid", nullable: false),
                    ReplicaId = table.Column<Guid>(type: "uuid", nullable: false),
                    SourceAccess = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    AgentId = table.Column<Guid>(type: "uuid", nullable: false),
                    DatabaseName = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    Mode = table.Column<string>(type: "character varying(24)", maxLength: 24, nullable: false),
                    OverwritePolicy = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    IdempotencyKey = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    RequestFingerprint = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    RequestedByUserId = table.Column<Guid>(type: "uuid", nullable: true),
                    Status = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    CurrentAttemptNumber = table.Column<int>(type: "integer", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    StartedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    CompletedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    CancellationRequestedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    FailureCode = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                    TargetState = table.Column<string>(type: "character varying(24)", maxLength: 24, nullable: true),
                    ResultJson = table.Column<string>(type: "jsonb", nullable: true),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false),
                    OptionTerminateActiveConnections = table.Column<bool>(type: "boolean", nullable: false),
                    OptionVerifyBackup = table.Column<bool>(type: "boolean", nullable: false),
                    OptionVerifyChecksum = table.Column<bool>(type: "boolean", nullable: false),
                    TargetCredentialReference = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    TargetEncrypt = table.Column<bool>(type: "boolean", nullable: false),
                    TargetHost = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: false),
                    TargetIntegratedSecurity = table.Column<bool>(type: "boolean", nullable: false),
                    TargetPort = table.Column<int>(type: "integer", nullable: true),
                    TargetTrustServerCertificate = table.Column<bool>(type: "boolean", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_restore_operations", x => x.Id);
                    table.UniqueConstraint("AK_restore_operations_Id_TenantId", x => new { x.Id, x.TenantId });
                    table.CheckConstraint("ck_restore_operations_attempt", "\"CurrentAttemptNumber\" >= 1");
                    table.CheckConstraint("ck_restore_operations_id", "\"Id\" <> '00000000-0000-0000-0000-000000000000'::uuid");
                    table.CheckConstraint("ck_restore_operations_mode", "(\"Mode\" = 'NewDatabase' AND \"OverwritePolicy\" = 'Forbidden') OR (\"Mode\" = 'ReplaceExistingDatabase' AND \"OverwritePolicy\" = 'ReplaceExisting')");
                    table.CheckConstraint("ck_restore_operations_outcome", "(\"Status\" <> 'Succeeded' OR (\"TargetState\" = 'Online' AND \"FailureCode\" IS NULL)) AND (\"Status\" NOT IN ('Failed','Cancelled','AgentLost') OR (\"TargetState\" <> 'Online' AND \"FailureCode\" IS NOT NULL))");
                    table.CheckConstraint("ck_restore_operations_result", "\"ResultJson\" IS NULL OR \"CompletedAt\" IS NOT NULL");
                    table.CheckConstraint("ck_restore_operations_source", "\"SourceAccess\" IN ('AgentLocal','CentralStorage')");
                    table.CheckConstraint("ck_restore_operations_status", "\"Status\" IN ('Pending','Leased','Running','Succeeded','Failed','Cancelled','AgentLost')");
                    table.CheckConstraint("ck_restore_operations_target", "(\"TargetPort\" IS NULL OR \"TargetPort\" BETWEEN 1 AND 65535) AND ((\"TargetIntegratedSecurity\" AND \"TargetCredentialReference\" IS NULL) OR (NOT \"TargetIntegratedSecurity\" AND \"TargetCredentialReference\" IS NOT NULL))");
                    table.CheckConstraint("ck_restore_operations_target_state", "\"TargetState\" IS NULL OR \"TargetState\" IN ('NotModified','Online','PartiallyRestored','Unknown')");
                    table.CheckConstraint("ck_restore_operations_tenant", "\"TenantId\" <> '00000000-0000-0000-0000-000000000000'::uuid");
                    table.CheckConstraint("ck_restore_operations_terminal", "(\"Status\" IN ('Succeeded','Failed','Cancelled','AgentLost')) = (\"CompletedAt\" IS NOT NULL) AND (\"CompletedAt\" IS NOT NULL) = (\"TargetState\" IS NOT NULL)");
                    table.CheckConstraint("ck_restore_operations_text", "length(\"DatabaseName\") BETWEEN 1 AND 128 AND length(\"IdempotencyKey\") BETWEEN 1 AND 128 AND length(\"RequestFingerprint\") BETWEEN 1 AND 128");
                    table.ForeignKey(
                        name: "FK_restore_operations_agents_AgentId_TenantId",
                        columns: x => new { x.AgentId, x.TenantId },
                        principalTable: "agents",
                        principalColumns: new[] { "Id", "TenantId" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_restore_operations_backup_artifacts_ArtifactId_TenantId",
                        columns: x => new { x.ArtifactId, x.TenantId },
                        principalTable: "backup_artifacts",
                        principalColumns: new[] { "Id", "TenantId" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_restore_operations_backup_replicas_ReplicaId_TenantId",
                        columns: x => new { x.ReplicaId, x.TenantId },
                        principalTable: "backup_replicas",
                        principalColumns: new[] { "Id", "TenantId" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_restore_operations_tenants_TenantId",
                        column: x => x.TenantId,
                        principalTable: "tenants",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "restore_attempts",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    TenantId = table.Column<Guid>(type: "uuid", nullable: false),
                    OperationId = table.Column<Guid>(type: "uuid", nullable: false),
                    AgentId = table.Column<Guid>(type: "uuid", nullable: false),
                    Number = table.Column<int>(type: "integer", nullable: false),
                    Status = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    LeasedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    StartedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    CompletedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    LeaseToken = table.Column<Guid>(type: "uuid", nullable: true),
                    LeaseExpiresAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    SoftwareVersion = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    ProtocolVersion = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: true),
                    Platform = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: true),
                    ProgressStage = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    ProgressPercent = table.Column<double>(type: "double precision", nullable: true),
                    ProgressAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    FailureCode = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                    TargetState = table.Column<string>(type: "character varying(24)", maxLength: 24, nullable: true),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_restore_attempts", x => x.Id);
                    table.UniqueConstraint("AK_restore_attempts_Id_TenantId", x => new { x.Id, x.TenantId });
                    table.CheckConstraint("ck_restore_attempts_id", "\"Id\" <> '00000000-0000-0000-0000-000000000000'::uuid");
                    table.CheckConstraint("ck_restore_attempts_lease", "\"Status\" NOT IN ('Leased','Running') OR (\"LeaseToken\" IS NOT NULL AND \"LeaseToken\" <> '00000000-0000-0000-0000-000000000000'::uuid AND \"LeaseExpiresAt\" IS NOT NULL)");
                    table.CheckConstraint("ck_restore_attempts_number", "\"Number\" >= 1");
                    table.CheckConstraint("ck_restore_attempts_progress", "\"ProgressPercent\" IS NULL OR (\"ProgressPercent\" >= 0 AND \"ProgressPercent\" <= 100)");
                    table.CheckConstraint("ck_restore_attempts_started", "\"Status\" <> 'Running' OR \"StartedAt\" IS NOT NULL");
                    table.CheckConstraint("ck_restore_attempts_status", "\"Status\" IN ('Pending','Leased','Running','Succeeded','Failed','Cancelled','AgentLost')");
                    table.CheckConstraint("ck_restore_attempts_target_state", "\"TargetState\" IS NULL OR \"TargetState\" IN ('NotModified','Online','PartiallyRestored','Unknown')");
                    table.CheckConstraint("ck_restore_attempts_tenant", "\"TenantId\" <> '00000000-0000-0000-0000-000000000000'::uuid");
                    table.CheckConstraint("ck_restore_attempts_terminal", "(\"Status\" IN ('Succeeded','Failed','Cancelled','AgentLost')) = (\"CompletedAt\" IS NOT NULL) AND (\"CompletedAt\" IS NOT NULL) = (\"TargetState\" IS NOT NULL)");
                    table.ForeignKey(
                        name: "FK_restore_attempts_agents_AgentId_TenantId",
                        columns: x => new { x.AgentId, x.TenantId },
                        principalTable: "agents",
                        principalColumns: new[] { "Id", "TenantId" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_restore_attempts_restore_operations_OperationId_TenantId",
                        columns: x => new { x.OperationId, x.TenantId },
                        principalTable: "restore_operations",
                        principalColumns: new[] { "Id", "TenantId" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_restore_attempts_tenants_TenantId",
                        column: x => x.TenantId,
                        principalTable: "tenants",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "restore_replica_pins",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    TenantId = table.Column<Guid>(type: "uuid", nullable: false),
                    OperationId = table.Column<Guid>(type: "uuid", nullable: false),
                    ReplicaId = table.Column<Guid>(type: "uuid", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    ReleasedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_restore_replica_pins", x => x.Id);
                    table.UniqueConstraint("AK_restore_replica_pins_Id_TenantId", x => new { x.Id, x.TenantId });
                    table.CheckConstraint("ck_restore_replica_pins_id", "\"Id\" <> '00000000-0000-0000-0000-000000000000'::uuid");
                    table.CheckConstraint("ck_restore_replica_pins_tenant", "\"TenantId\" <> '00000000-0000-0000-0000-000000000000'::uuid");
                    table.ForeignKey(
                        name: "FK_restore_replica_pins_backup_replicas_ReplicaId_TenantId",
                        columns: x => new { x.ReplicaId, x.TenantId },
                        principalTable: "backup_replicas",
                        principalColumns: new[] { "Id", "TenantId" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_restore_replica_pins_restore_operations_OperationId_TenantId",
                        columns: x => new { x.OperationId, x.TenantId },
                        principalTable: "restore_operations",
                        principalColumns: new[] { "Id", "TenantId" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_restore_replica_pins_tenants_TenantId",
                        column: x => x.TenantId,
                        principalTable: "tenants",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_restore_attempts_AgentId_TenantId",
                table: "restore_attempts",
                columns: new[] { "AgentId", "TenantId" });

            migrationBuilder.CreateIndex(
                name: "ix_restore_attempts_leased",
                table: "restore_attempts",
                column: "LeaseExpiresAt",
                filter: "\"Status\" IN ('Leased','Running')");

            migrationBuilder.CreateIndex(
                name: "IX_restore_attempts_LeaseToken",
                table: "restore_attempts",
                column: "LeaseToken",
                unique: true,
                filter: "\"LeaseToken\" IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_restore_attempts_OperationId_TenantId",
                table: "restore_attempts",
                columns: new[] { "OperationId", "TenantId" });

            migrationBuilder.CreateIndex(
                name: "IX_restore_attempts_TenantId",
                table: "restore_attempts",
                column: "TenantId");

            migrationBuilder.CreateIndex(
                name: "IX_restore_attempts_TenantId_AgentId_Status",
                table: "restore_attempts",
                columns: new[] { "TenantId", "AgentId", "Status" });

            migrationBuilder.CreateIndex(
                name: "IX_restore_attempts_TenantId_OperationId_Number",
                table: "restore_attempts",
                columns: new[] { "TenantId", "OperationId", "Number" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_restore_operations_AgentId_TenantId",
                table: "restore_operations",
                columns: new[] { "AgentId", "TenantId" });

            migrationBuilder.CreateIndex(
                name: "IX_restore_operations_ArtifactId_TenantId",
                table: "restore_operations",
                columns: new[] { "ArtifactId", "TenantId" });

            migrationBuilder.CreateIndex(
                name: "ix_restore_operations_pending",
                table: "restore_operations",
                columns: new[] { "CreatedAt", "Id" },
                filter: "\"Status\" = 'Pending'");

            migrationBuilder.CreateIndex(
                name: "IX_restore_operations_ReplicaId_TenantId",
                table: "restore_operations",
                columns: new[] { "ReplicaId", "TenantId" });

            migrationBuilder.CreateIndex(
                name: "IX_restore_operations_TenantId",
                table: "restore_operations",
                column: "TenantId");

            migrationBuilder.CreateIndex(
                name: "IX_restore_operations_TenantId_AgentId_Status_CreatedAt",
                table: "restore_operations",
                columns: new[] { "TenantId", "AgentId", "Status", "CreatedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_restore_operations_TenantId_ArtifactId_CreatedAt",
                table: "restore_operations",
                columns: new[] { "TenantId", "ArtifactId", "CreatedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_restore_operations_TenantId_ArtifactId_IdempotencyKey",
                table: "restore_operations",
                columns: new[] { "TenantId", "ArtifactId", "IdempotencyKey" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_restore_operations_TenantId_CreatedAt",
                table: "restore_operations",
                columns: new[] { "TenantId", "CreatedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_restore_operations_TenantId_Status_CreatedAt",
                table: "restore_operations",
                columns: new[] { "TenantId", "Status", "CreatedAt" });

            migrationBuilder.CreateIndex(
                name: "ix_restore_replica_pins_active",
                table: "restore_replica_pins",
                column: "ReplicaId",
                filter: "\"ReleasedAt\" IS NULL");

            migrationBuilder.CreateIndex(
                name: "IX_restore_replica_pins_OperationId_TenantId",
                table: "restore_replica_pins",
                columns: new[] { "OperationId", "TenantId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_restore_replica_pins_ReplicaId_TenantId",
                table: "restore_replica_pins",
                columns: new[] { "ReplicaId", "TenantId" });

            migrationBuilder.CreateIndex(
                name: "IX_restore_replica_pins_TenantId",
                table: "restore_replica_pins",
                column: "TenantId");

            // The retention pin, enforced by the database whatever the application does. A restore locks the replica row and inserts its pin in one transaction;
            // a delete claim locks the same row and re-checks the pin; and this trigger refuses the transition itself, so a path that forgot to look cannot delete
            // the bytes under a running restore. The row lock makes the check exact: a pin cannot appear between a delete claim's check and its update.
            migrationBuilder.Sql("""
                CREATE FUNCTION backup_replica_pin_guard() RETURNS trigger LANGUAGE plpgsql AS $$
                BEGIN
                    IF EXISTS (SELECT 1 FROM restore_replica_pins WHERE "ReplicaId" = NEW."Id" AND "ReleasedAt" IS NULL) THEN
                        RAISE EXCEPTION 'A replica pinned by a restore cannot begin its physical deletion.' USING ERRCODE = '23514';
                    END IF;
                    RETURN NEW;
                END $$;
                CREATE TRIGGER trg_backup_replica_pin_guard BEFORE UPDATE OF "Status" ON backup_replicas
                    FOR EACH ROW WHEN (NEW."Status" IN ('Deleting','DeleteFailed','Deleted') AND NEW."Status" IS DISTINCT FROM OLD."Status")
                    EXECUTE FUNCTION backup_replica_pin_guard();
                """);
            // The other direction: a replica whose physical delete has begun, failed or finished is never pinned.
            migrationBuilder.Sql("""
                CREATE FUNCTION restore_pin_replica_guard() RETURNS trigger LANGUAGE plpgsql AS $$
                DECLARE
                    replica_status text;
                BEGIN
                    IF NEW."ReleasedAt" IS NULL THEN
                        SELECT "Status" INTO replica_status FROM backup_replicas
                            WHERE "Id" = NEW."ReplicaId" AND "TenantId" = NEW."TenantId" FOR UPDATE;
                        IF replica_status IN ('Deleting','DeleteFailed','Deleted') THEN
                            RAISE EXCEPTION 'A replica that is being or has been deleted cannot be pinned.' USING ERRCODE = '23514';
                        END IF;
                    END IF;
                    RETURN NEW;
                END $$;
                CREATE TRIGGER trg_restore_pin_replica_guard BEFORE INSERT ON restore_replica_pins
                    FOR EACH ROW EXECUTE FUNCTION restore_pin_replica_guard();
                """);
            // A released pin is history: it is never taken up again.
            migrationBuilder.Sql("""
                CREATE FUNCTION restore_pin_release_guard() RETURNS trigger LANGUAGE plpgsql AS $$
                BEGIN
                    IF OLD."ReleasedAt" IS NOT NULL AND NEW."ReleasedAt" IS DISTINCT FROM OLD."ReleasedAt" THEN
                        RAISE EXCEPTION 'A released replica pin cannot change.' USING ERRCODE = '23514';
                    END IF;
                    RETURN NEW;
                END $$;
                CREATE TRIGGER trg_restore_pin_release_guard BEFORE UPDATE ON restore_replica_pins
                    FOR EACH ROW EXECUTE FUNCTION restore_pin_release_guard();
                """);
            // A terminal outcome is immutable in the database, whatever the application does.
            migrationBuilder.Sql("""
                CREATE FUNCTION restore_operation_terminal_guard() RETURNS trigger LANGUAGE plpgsql AS $$
                BEGIN
                    IF OLD."Status" IN ('Succeeded','Failed','Cancelled','AgentLost') AND (NEW."Status" IS DISTINCT FROM OLD."Status"
                        OR NEW."FailureCode" IS DISTINCT FROM OLD."FailureCode"
                        OR NEW."TargetState" IS DISTINCT FROM OLD."TargetState"
                        OR NEW."CompletedAt" IS DISTINCT FROM OLD."CompletedAt"
                        OR NEW."ResultJson" IS DISTINCT FROM OLD."ResultJson") THEN
                        RAISE EXCEPTION 'A terminal restore is immutable.' USING ERRCODE = '23514';
                    END IF;
                    RETURN NEW;
                END $$;
                CREATE TRIGGER trg_restore_operation_terminal_guard BEFORE UPDATE ON restore_operations
                    FOR EACH ROW EXECUTE FUNCTION restore_operation_terminal_guard();
                """);
            migrationBuilder.Sql("""
                CREATE FUNCTION restore_attempt_terminal_guard() RETURNS trigger LANGUAGE plpgsql AS $$
                BEGIN
                    IF OLD."Status" IN ('Succeeded','Failed','Cancelled','AgentLost') AND (NEW."Status" IS DISTINCT FROM OLD."Status"
                        OR NEW."FailureCode" IS DISTINCT FROM OLD."FailureCode"
                        OR NEW."TargetState" IS DISTINCT FROM OLD."TargetState"
                        OR NEW."CompletedAt" IS DISTINCT FROM OLD."CompletedAt") THEN
                        RAISE EXCEPTION 'A terminal restore attempt is immutable.' USING ERRCODE = '23514';
                    END IF;
                    RETURN NEW;
                END $$;
                CREATE TRIGGER trg_restore_attempt_terminal_guard BEFORE UPDATE ON restore_attempts
                    FOR EACH ROW EXECUTE FUNCTION restore_attempt_terminal_guard();
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("DROP TRIGGER trg_backup_replica_pin_guard ON backup_replicas; DROP FUNCTION backup_replica_pin_guard();");
            migrationBuilder.Sql("DROP TRIGGER trg_restore_pin_replica_guard ON restore_replica_pins; DROP FUNCTION restore_pin_replica_guard();");
            migrationBuilder.Sql("DROP TRIGGER trg_restore_pin_release_guard ON restore_replica_pins; DROP FUNCTION restore_pin_release_guard();");
            migrationBuilder.Sql("DROP TRIGGER trg_restore_attempt_terminal_guard ON restore_attempts; DROP FUNCTION restore_attempt_terminal_guard();");
            migrationBuilder.Sql("DROP TRIGGER trg_restore_operation_terminal_guard ON restore_operations; DROP FUNCTION restore_operation_terminal_guard();");

            migrationBuilder.DropTable(
                name: "restore_attempts");

            migrationBuilder.DropTable(
                name: "restore_replica_pins");

            migrationBuilder.DropTable(
                name: "restore_operations");
        }
    }
}
