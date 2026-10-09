using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BackupPlatform.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class RestorePreparations : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "ck_restore_operations_outcome",
                table: "restore_operations");

            migrationBuilder.DropCheckConstraint(
                name: "ck_restore_operations_text",
                table: "restore_operations");

            migrationBuilder.AddColumn<Guid>(
                name: "PrecheckId",
                table: "restore_operations",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Purpose",
                table: "restore_operations",
                type: "character varying(16)",
                maxLength: 16,
                nullable: false,
                defaultValue: "Execution");

            migrationBuilder.AddCheckConstraint(
                name: "ck_restore_operations_outcome",
                table: "restore_operations",
                sql: "(\"Status\" <> 'Succeeded' OR (((\"Purpose\" = 'Execution' AND \"TargetState\" = 'Online') OR (\"Purpose\" <> 'Execution' AND \"TargetState\" = 'NotModified')) AND \"FailureCode\" IS NULL)) AND (\"Status\" NOT IN ('Failed','Cancelled','AgentLost') OR (\"TargetState\" <> 'Online' AND \"FailureCode\" IS NOT NULL))");

            migrationBuilder.AddCheckConstraint(
                name: "ck_restore_operations_purpose",
                table: "restore_operations",
                sql: "\"Purpose\" IN ('Execution','Precheck','Discovery')");

            migrationBuilder.AddCheckConstraint(
                name: "ck_restore_operations_text",
                table: "restore_operations",
                sql: "((\"Purpose\" = 'Discovery' AND \"DatabaseName\" = '') OR (\"Purpose\" <> 'Discovery' AND length(\"DatabaseName\") BETWEEN 1 AND 128)) AND length(\"IdempotencyKey\") BETWEEN 1 AND 128 AND length(\"RequestFingerprint\") BETWEEN 1 AND 128");

            migrationBuilder.DropIndex(
                name: "IX_restore_operations_TenantId_ArtifactId_IdempotencyKey",
                table: "restore_operations");

            migrationBuilder.CreateIndex(
                name: "IX_restore_operations_TenantId_ArtifactId_Purpose_Idempotency~",
                table: "restore_operations",
                columns: new[] { "TenantId", "ArtifactId", "Purpose", "IdempotencyKey" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "ck_restore_operations_outcome",
                table: "restore_operations");

            migrationBuilder.DropCheckConstraint(
                name: "ck_restore_operations_purpose",
                table: "restore_operations");

            migrationBuilder.DropCheckConstraint(
                name: "ck_restore_operations_text",
                table: "restore_operations");

            migrationBuilder.DropIndex(
                name: "IX_restore_operations_TenantId_ArtifactId_Purpose_Idempotency~",
                table: "restore_operations");

            migrationBuilder.CreateIndex(
                name: "IX_restore_operations_TenantId_ArtifactId_IdempotencyKey",
                table: "restore_operations",
                columns: new[] { "TenantId", "ArtifactId", "IdempotencyKey" },
                unique: true);

            migrationBuilder.DropColumn(
                name: "PrecheckId",
                table: "restore_operations");

            migrationBuilder.DropColumn(
                name: "Purpose",
                table: "restore_operations");

            migrationBuilder.AddCheckConstraint(
                name: "ck_restore_operations_outcome",
                table: "restore_operations",
                sql: "(\"Status\" <> 'Succeeded' OR (\"TargetState\" = 'Online' AND \"FailureCode\" IS NULL)) AND (\"Status\" NOT IN ('Failed','Cancelled','AgentLost') OR (\"TargetState\" <> 'Online' AND \"FailureCode\" IS NOT NULL))");

            migrationBuilder.AddCheckConstraint(
                name: "ck_restore_operations_text",
                table: "restore_operations",
                sql: "length(\"DatabaseName\") BETWEEN 1 AND 128 AND length(\"IdempotencyKey\") BETWEEN 1 AND 128 AND length(\"RequestFingerprint\") BETWEEN 1 AND 128");
        }
    }
}
