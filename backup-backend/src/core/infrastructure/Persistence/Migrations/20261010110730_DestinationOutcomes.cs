using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BackupPlatform.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class DestinationOutcomes : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "backup_destination_outcomes",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    TenantId = table.Column<Guid>(type: "uuid", nullable: false),
                    RunId = table.Column<Guid>(type: "uuid", nullable: false),
                    AttemptId = table.Column<Guid>(type: "uuid", nullable: false),
                    StorageProvider = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    StorageConnectionId = table.Column<Guid>(type: "uuid", nullable: false),
                    IsPrimary = table.Column<bool>(type: "boolean", nullable: false),
                    ExecutionLocation = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    State = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    Upload = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    Verification = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    FailureCode = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                    Key = table.Column<string>(type: "character varying(1024)", maxLength: 1024, nullable: true),
                    ObjectMayRemain = table.Column<bool>(type: "boolean", nullable: false),
                    StartedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    CompletedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    ReplicaId = table.Column<Guid>(type: "uuid", nullable: true),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_backup_destination_outcomes", x => x.Id);
                    table.UniqueConstraint("AK_backup_destination_outcomes_Id_TenantId", x => new { x.Id, x.TenantId });
                    table.CheckConstraint("ck_backup_destination_outcome_state", "\"State\" IN ('Verified','Failed','Cancelled') AND \"Upload\" IN ('Succeeded','Failed','NotAttempted') AND \"Verification\" IN ('Verified','Failed','NotRun')");
                    table.CheckConstraint("ck_backup_destination_outcome_times", "\"CompletedAt\" >= \"StartedAt\"");
                    table.CheckConstraint("ck_backup_destination_outcome_verified", "(\"State\" = 'Verified') = (\"ReplicaId\" IS NOT NULL) AND (\"State\" <> 'Verified' OR (\"FailureCode\" IS NULL AND \"Upload\" = 'Succeeded' AND \"Verification\" = 'Verified' AND \"Key\" IS NOT NULL)) AND (\"State\" = 'Verified' OR (\"FailureCode\" IS NOT NULL AND \"Verification\" <> 'Verified')) AND (NOT \"ObjectMayRemain\" OR (\"State\" <> 'Verified' AND \"Key\" IS NOT NULL AND \"Upload\" = 'Succeeded'))");
                    table.CheckConstraint("ck_backup_destination_outcomes_id", "\"Id\" <> '00000000-0000-0000-0000-000000000000'::uuid");
                    table.CheckConstraint("ck_backup_destination_outcomes_tenant", "\"TenantId\" <> '00000000-0000-0000-0000-000000000000'::uuid");
                    table.ForeignKey(
                        name: "FK_backup_destination_outcomes_backup_run_attempts_AttemptId_T~",
                        columns: x => new { x.AttemptId, x.TenantId },
                        principalTable: "backup_run_attempts",
                        principalColumns: new[] { "Id", "TenantId" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_backup_destination_outcomes_tenants_TenantId",
                        column: x => x.TenantId,
                        principalTable: "tenants",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "ux_backup_replicas_live_verified_destination",
                table: "backup_replicas",
                columns: new[] { "TenantId", "ArtifactId", "StorageConnectionId" },
                unique: true,
                filter: "\"VerificationState\" = 'Verified' AND \"Status\" <> 'Deleted'");

            migrationBuilder.CreateIndex(
                name: "IX_backup_destination_outcomes_AttemptId_TenantId",
                table: "backup_destination_outcomes",
                columns: new[] { "AttemptId", "TenantId" });

            migrationBuilder.CreateIndex(
                name: "IX_backup_destination_outcomes_TenantId",
                table: "backup_destination_outcomes",
                column: "TenantId");

            migrationBuilder.CreateIndex(
                name: "IX_backup_destination_outcomes_TenantId_AttemptId_StorageProvi~",
                table: "backup_destination_outcomes",
                columns: new[] { "TenantId", "AttemptId", "StorageProvider", "StorageConnectionId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_backup_destination_outcomes_TenantId_RunId",
                table: "backup_destination_outcomes",
                columns: new[] { "TenantId", "RunId" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "backup_destination_outcomes");

            migrationBuilder.DropIndex(
                name: "ux_backup_replicas_live_verified_destination",
                table: "backup_replicas");
        }
    }
}
