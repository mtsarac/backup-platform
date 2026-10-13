using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BackupPlatform.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class DurableOrphanCleanupLedger : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {

            migrationBuilder.CreateTable(
                name: "backup_storage_cleanups",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    TenantId = table.Column<Guid>(type: "uuid", nullable: false),
                    JobId = table.Column<Guid>(type: "uuid", nullable: false),
                    RunId = table.Column<Guid>(type: "uuid", nullable: false),
                    AttemptId = table.Column<Guid>(type: "uuid", nullable: false),
                    StorageProvider = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    StorageName = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    TargetIdentity = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    Key = table.Column<string>(type: "character varying(1024)", maxLength: 1024, nullable: false),
                    IsPartial = table.Column<bool>(type: "boolean", nullable: false),
                    Status = table.Column<int>(type: "integer", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    LeaseToken = table.Column<Guid>(type: "uuid", nullable: true),
                    LeaseExpiresAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    CompletedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_backup_storage_cleanups", x => x.Id);
                    table.UniqueConstraint("AK_backup_storage_cleanups_Id_TenantId", x => new { x.Id, x.TenantId });
                    table.CheckConstraint("ck_backup_storage_cleanups_id", "\"Id\" <> '00000000-0000-0000-0000-000000000000'::uuid");
                    table.CheckConstraint("ck_backup_storage_cleanups_lease", "(\"Status\" = 2 AND \"CompletedAt\" IS NOT NULL) OR (\"Status\" = 1 AND (\"LeaseToken\" IS NULL) = (\"LeaseExpiresAt\" IS NULL))");
                    table.CheckConstraint("ck_backup_storage_cleanups_status", "\"Status\" IN (1, 2)");
                    table.CheckConstraint("ck_backup_storage_cleanups_tenant", "\"TenantId\" <> '00000000-0000-0000-0000-000000000000'::uuid");
                    table.ForeignKey(
                        name: "FK_backup_storage_cleanups_tenants_TenantId",
                        column: x => x.TenantId,
                        principalTable: "tenants",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });


            migrationBuilder.CreateIndex(
                name: "IX_backup_storage_cleanups_StorageProvider_StorageName_Status_~",
                table: "backup_storage_cleanups",
                columns: new[] { "StorageProvider", "StorageName", "Status", "LeaseExpiresAt" },
                filter: "\"Status\" = 1");

            migrationBuilder.CreateIndex(
                name: "IX_backup_storage_cleanups_StorageProvider_StorageName_TargetI~",
                table: "backup_storage_cleanups",
                columns: new[] { "StorageProvider", "StorageName", "TargetIdentity" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_backup_storage_cleanups_TenantId",
                table: "backup_storage_cleanups",
                column: "TenantId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "backup_storage_cleanups");

            migrationBuilder.DropCheckConstraint(
                name: "ck_audit_events_actor_kind",
                table: "audit_events");

            migrationBuilder.AddCheckConstraint(
                name: "ck_audit_events_actor_kind",
                table: "audit_events",
                sql: "\"ActorKind\" IN (1, 2, 3)");
        }
    }
}
