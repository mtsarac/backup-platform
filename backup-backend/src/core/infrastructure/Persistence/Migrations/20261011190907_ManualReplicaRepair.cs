using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BackupPlatform.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class ManualReplicaRepair : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "replica_repair_operations",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    TenantId = table.Column<Guid>(type: "uuid", nullable: false),
                    ArtifactId = table.Column<Guid>(type: "uuid", nullable: false),
                    StorageConnectionId = table.Column<Guid>(type: "uuid", nullable: false),
                    StorageProvider = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    SourceReplicaId = table.Column<Guid>(type: "uuid", nullable: true),
                    TargetReplicaId = table.Column<Guid>(type: "uuid", nullable: true),
                    Status = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    LeaseOwner = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                    LeaseToken = table.Column<Guid>(type: "uuid", nullable: true),
                    LeaseExpiresAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    FailureCode = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                    FailureReason = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: true),
                    TransferredBytes = table.Column<long>(type: "bigint", nullable: false),
                    TotalBytes = table.Column<long>(type: "bigint", nullable: false),
                    RequestedByUserId = table.Column<Guid>(type: "uuid", nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    StartedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    CompletedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    CancellationRequestedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_replica_repair_operations", x => x.Id);
                    table.UniqueConstraint("AK_replica_repair_operations_Id_TenantId", x => new { x.Id, x.TenantId });
                    table.CheckConstraint("ck_replica_repair_operations_bytes", "\"TransferredBytes\" >= 0 AND \"TotalBytes\" >= 0 AND \"TransferredBytes\" <= \"TotalBytes\"");
                    table.CheckConstraint("ck_replica_repair_operations_id", "\"Id\" <> '00000000-0000-0000-0000-000000000000'::uuid");
                    table.CheckConstraint("ck_replica_repair_operations_status", "\"Status\" IN ('Pending','InProgress','Succeeded','Failed','Cancelled')");
                    table.CheckConstraint("ck_replica_repair_operations_tenant", "\"TenantId\" <> '00000000-0000-0000-0000-000000000000'::uuid");
                    table.CheckConstraint("ck_replica_repair_operations_terminal", "(\"Status\" IN ('Succeeded','Failed','Cancelled')) = (\"CompletedAt\" IS NOT NULL)");
                    table.ForeignKey(
                        name: "FK_replica_repair_operations_backup_artifacts_ArtifactId_Tenan~",
                        columns: x => new { x.ArtifactId, x.TenantId },
                        principalTable: "backup_artifacts",
                        principalColumns: new[] { "Id", "TenantId" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_replica_repair_operations_backup_replicas_SourceReplicaId_T~",
                        columns: x => new { x.SourceReplicaId, x.TenantId },
                        principalTable: "backup_replicas",
                        principalColumns: new[] { "Id", "TenantId" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_replica_repair_operations_tenants_TenantId",
                        column: x => x.TenantId,
                        principalTable: "tenants",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "ix_replica_repair_active_source",
                table: "replica_repair_operations",
                column: "SourceReplicaId",
                filter: "\"Status\" IN ('Pending', 'InProgress') AND \"SourceReplicaId\" IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_replica_repair_operations_ArtifactId_TenantId",
                table: "replica_repair_operations",
                columns: new[] { "ArtifactId", "TenantId" });

            migrationBuilder.CreateIndex(
                name: "IX_replica_repair_operations_SourceReplicaId_TenantId",
                table: "replica_repair_operations",
                columns: new[] { "SourceReplicaId", "TenantId" });

            migrationBuilder.CreateIndex(
                name: "IX_replica_repair_operations_TenantId",
                table: "replica_repair_operations",
                column: "TenantId");

            migrationBuilder.CreateIndex(
                name: "ux_replica_repair_active",
                table: "replica_repair_operations",
                columns: new[] { "TenantId", "ArtifactId", "StorageConnectionId" },
                unique: true,
                filter: "\"Status\" IN ('Pending', 'InProgress')");

            migrationBuilder.Sql("""
                CREATE OR REPLACE FUNCTION backup_replica_pin_guard() RETURNS trigger AS $$
                BEGIN
                    IF EXISTS (SELECT 1 FROM restore_replica_pins WHERE "ReplicaId" = NEW."Id" AND "ReleasedAt" IS NULL)
                       OR EXISTS (SELECT 1 FROM download_sessions WHERE "ReplicaId" = NEW."Id" AND "TenantId" = NEW."TenantId"
                           AND "ReleasedAt" IS NULL AND "ExpiresAt" > clock_timestamp()
                           AND ("LeaseExpiresAt" IS NULL OR "LeaseExpiresAt" > clock_timestamp()))
                       OR EXISTS (SELECT 1 FROM replica_repair_operations WHERE "SourceReplicaId" = NEW."Id" AND "TenantId" = NEW."TenantId"
                           AND "Status" IN ('Pending', 'InProgress')) THEN
                        RAISE EXCEPTION 'A pinned replica cannot begin its physical deletion.' USING ERRCODE = '23514';
                    END IF;
                    RETURN NEW;
                END;
                $$ LANGUAGE plpgsql;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                CREATE OR REPLACE FUNCTION backup_replica_pin_guard() RETURNS trigger AS $$
                BEGIN
                    IF EXISTS (SELECT 1 FROM restore_replica_pins WHERE "ReplicaId" = NEW."Id" AND "ReleasedAt" IS NULL)
                       OR EXISTS (SELECT 1 FROM download_sessions WHERE "ReplicaId" = NEW."Id" AND "TenantId" = NEW."TenantId"
                           AND "ReleasedAt" IS NULL AND "ExpiresAt" > clock_timestamp()
                           AND ("LeaseExpiresAt" IS NULL OR "LeaseExpiresAt" > clock_timestamp())) THEN
                        RAISE EXCEPTION 'A pinned replica cannot begin its physical deletion.' USING ERRCODE = '23514';
                    END IF;
                    RETURN NEW;
                END;
                $$ LANGUAGE plpgsql;
                """);
            migrationBuilder.DropTable(
                name: "replica_repair_operations");
        }
    }
}
