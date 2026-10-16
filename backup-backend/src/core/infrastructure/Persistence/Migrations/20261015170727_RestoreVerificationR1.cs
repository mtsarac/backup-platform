using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BackupPlatform.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class RestoreVerificationR1 : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "ck_restore_operations_purpose",
                table: "restore_operations");

            migrationBuilder.AddColumn<int>(
                name: "CleanupAttempts",
                table: "restore_operations",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<string>(
                name: "CleanupDatabaseName",
                table: "restore_operations",
                type: "character varying(128)",
                maxLength: 128,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "CleanupKind",
                table: "restore_operations",
                type: "character varying(24)",
                maxLength: 24,
                nullable: false,
                defaultValue: "None");

            migrationBuilder.AddColumn<string>(
                name: "CleanupLastFailureCode",
                table: "restore_operations",
                type: "character varying(64)",
                maxLength: 64,
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "CleanupLeaseExpiresAt",
                table: "restore_operations",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "CleanupLeaseToken",
                table: "restore_operations",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "CleanupNextAttemptAt",
                table: "restore_operations",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "CleanupRequestedByUserId",
                table: "restore_operations",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "CleanupState",
                table: "restore_operations",
                type: "character varying(16)",
                maxLength: 16,
                nullable: false,
                defaultValue: "Idle");

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "CleanupUpdatedAt",
                table: "restore_operations",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "ix_restore_operations_cleanup_pending",
                table: "restore_operations",
                columns: new[] { "TenantId", "AgentId" },
                filter: "\"CleanupState\" = 'Pending'");

            migrationBuilder.AddCheckConstraint(
                name: "ck_restore_operations_cleanup",
                table: "restore_operations",
                sql: "\"CleanupKind\" IN ('None','VerificationDatabase','RollbackCopy') AND \"CleanupState\" IN ('Idle','Retained','Pending','Completed') AND \"CleanupAttempts\" >= 0 AND (\"CleanupKind\" = 'None') = (\"CleanupDatabaseName\" IS NULL) AND (\"CleanupKind\" <> 'None' OR \"CleanupState\" = 'Idle') AND (\"Purpose\" = 'Verification') = (\"CleanupKind\" = 'VerificationDatabase') AND (\"CleanupKind\" <> 'VerificationDatabase' OR \"CleanupDatabaseName\" = \"DatabaseName\") AND (\"CleanupState\" IN ('Pending','Completed') OR \"CleanupLeaseToken\" IS NULL) AND (\"CleanupState\" NOT IN ('Pending','Completed','Retained') OR \"CompletedAt\" IS NOT NULL)");

            migrationBuilder.AddCheckConstraint(
                name: "ck_restore_operations_purpose",
                table: "restore_operations",
                sql: "\"Purpose\" IN ('Execution','Precheck','Discovery','Verification')");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ix_restore_operations_cleanup_pending",
                table: "restore_operations");

            migrationBuilder.DropCheckConstraint(
                name: "ck_restore_operations_cleanup",
                table: "restore_operations");

            migrationBuilder.DropCheckConstraint(
                name: "ck_restore_operations_purpose",
                table: "restore_operations");

            migrationBuilder.DropColumn(
                name: "CleanupAttempts",
                table: "restore_operations");

            migrationBuilder.DropColumn(
                name: "CleanupDatabaseName",
                table: "restore_operations");

            migrationBuilder.DropColumn(
                name: "CleanupKind",
                table: "restore_operations");

            migrationBuilder.DropColumn(
                name: "CleanupLastFailureCode",
                table: "restore_operations");

            migrationBuilder.DropColumn(
                name: "CleanupLeaseExpiresAt",
                table: "restore_operations");

            migrationBuilder.DropColumn(
                name: "CleanupLeaseToken",
                table: "restore_operations");

            migrationBuilder.DropColumn(
                name: "CleanupNextAttemptAt",
                table: "restore_operations");

            migrationBuilder.DropColumn(
                name: "CleanupRequestedByUserId",
                table: "restore_operations");

            migrationBuilder.DropColumn(
                name: "CleanupState",
                table: "restore_operations");

            migrationBuilder.DropColumn(
                name: "CleanupUpdatedAt",
                table: "restore_operations");

            migrationBuilder.AddCheckConstraint(
                name: "ck_restore_operations_purpose",
                table: "restore_operations",
                sql: "\"Purpose\" IN ('Execution','Precheck','Discovery')");
        }
    }
}
