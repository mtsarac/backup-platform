using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BackupPlatform.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class RetentionAndCleanup : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "ck_backup_replica_status",
                table: "backup_replicas");

            // Runs and artifacts that exist before this migration were published without an expiration and are kept forever (retention off,
            // no RetentionExpiresAt). Only jobs receive the platform default policy, so that every run created from now on is covered.
            migrationBuilder.AddColumn<TimeSpan>(
                name: "RetentionDeleteGrace",
                table: "backup_runs",
                type: "interval",
                nullable: false,
                defaultValue: new TimeSpan(1, 0, 0, 0, 0));

            migrationBuilder.AddColumn<bool>(
                name: "RetentionEnabled",
                table: "backup_runs",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<TimeSpan>(
                name: "RetentionKeepFor",
                table: "backup_runs",
                type: "interval",
                nullable: false,
                defaultValue: new TimeSpan(30, 0, 0, 0, 0));

            migrationBuilder.AlterColumn<string>(
                name: "Status",
                table: "backup_replicas",
                type: "character varying(16)",
                maxLength: 16,
                nullable: false,
                oldClrType: typeof(string),
                oldType: "text");

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "DeleteAfter",
                table: "backup_replicas",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "DeleteAttempts",
                table: "backup_replicas",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<string>(
                name: "DeleteFailureCode",
                table: "backup_replicas",
                type: "character varying(128)",
                maxLength: 128,
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "DeleteLeaseExpiresAt",
                table: "backup_replicas",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "DeleteLeaseOwner",
                table: "backup_replicas",
                type: "character varying(128)",
                maxLength: 128,
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "DeleteLeaseToken",
                table: "backup_replicas",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "DeleteNextAttemptAt",
                table: "backup_replicas",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "DeleteReason",
                table: "backup_replicas",
                type: "character varying(16)",
                maxLength: 16,
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "DeleteRequestedAt",
                table: "backup_replicas",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "DeleteRequestedByUserId",
                table: "backup_replicas",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "DeletedAt",
                table: "backup_replicas",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "LastDeleteAttemptAt",
                table: "backup_replicas",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<TimeSpan>(
                name: "RetentionDeleteGrace",
                table: "backup_jobs",
                type: "interval",
                nullable: false,
                defaultValue: new TimeSpan(1, 0, 0, 0, 0));

            migrationBuilder.AddColumn<bool>(
                name: "RetentionEnabled",
                table: "backup_jobs",
                type: "boolean",
                nullable: false,
                defaultValue: true);

            migrationBuilder.AddColumn<TimeSpan>(
                name: "RetentionKeepFor",
                table: "backup_jobs",
                type: "interval",
                nullable: false,
                defaultValue: new TimeSpan(30, 0, 0, 0, 0));

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "AcceptedAt",
                table: "backup_artifacts",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "RetentionAppliedAt",
                table: "backup_artifacts",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "RetentionExpiresAt",
                table: "backup_artifacts",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<TimeSpan>(
                name: "RetentionGracePeriod",
                table: "backup_artifacts",
                type: "interval",
                nullable: false,
                defaultValue: new TimeSpan(1, 0, 0, 0, 0));

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "RetentionPinnedAt",
                table: "backup_artifacts",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "ix_backup_occurrences_decided",
                table: "backup_schedule_occurrences",
                column: "DecidedAt");

            migrationBuilder.AddCheckConstraint(
                name: "ck_backup_runs_retention_policy",
                table: "backup_runs",
                sql: "\"RetentionKeepFor\" BETWEEN interval '1 second' AND interval '3650 days' AND \"RetentionDeleteGrace\" BETWEEN interval '1 second' AND interval '365 days'");

            migrationBuilder.CreateIndex(
                name: "ix_backup_replicas_delete_failed",
                table: "backup_replicas",
                column: "DeleteNextAttemptAt",
                filter: "\"Status\" = 'DeleteFailed'");

            migrationBuilder.CreateIndex(
                name: "ix_backup_replicas_delete_leased",
                table: "backup_replicas",
                column: "DeleteLeaseExpiresAt",
                filter: "\"Status\" = 'Deleting'");

            migrationBuilder.CreateIndex(
                name: "ix_backup_replicas_delete_pending",
                table: "backup_replicas",
                column: "DeleteAfter",
                filter: "\"Status\" = 'DeletePending'");

            migrationBuilder.CreateIndex(
                name: "ix_backup_replicas_location",
                table: "backup_replicas",
                columns: new[] { "StorageProvider", "Key" });

            migrationBuilder.AddCheckConstraint(
                name: "ck_backup_replica_lifecycle",
                table: "backup_replicas",
                sql: "(\"DeleteReason\" IS NULL OR \"DeleteReason\" IN ('RetentionExpired','Manual')) AND (\"DeleteRequestedByUserId\" IS NULL OR \"DeleteReason\" = 'Manual') AND ((\"Status\" = 'Available' AND \"DeleteRequestedAt\" IS NULL AND \"DeleteReason\" IS NULL AND \"DeleteAfter\" IS NULL AND \"DeleteLeaseToken\" IS NULL AND \"DeleteLeaseOwner\" IS NULL AND \"DeleteLeaseExpiresAt\" IS NULL AND \"DeleteNextAttemptAt\" IS NULL AND \"DeleteFailureCode\" IS NULL AND \"DeletedAt\" IS NULL AND \"DeleteAttempts\" = 0) OR (\"Status\" = 'DeletePending' AND \"DeleteRequestedAt\" IS NOT NULL AND \"DeleteReason\" IS NOT NULL AND \"DeleteAfter\" IS NOT NULL AND \"DeleteAfter\" >= \"DeleteRequestedAt\" AND \"DeleteLeaseToken\" IS NULL AND \"DeleteLeaseOwner\" IS NULL AND \"DeleteLeaseExpiresAt\" IS NULL AND \"DeleteNextAttemptAt\" IS NULL AND \"DeleteFailureCode\" IS NULL AND \"DeletedAt\" IS NULL AND \"DeleteAttempts\" = 0) OR (\"Status\" = 'Deleting' AND \"DeleteRequestedAt\" IS NOT NULL AND \"DeleteReason\" IS NOT NULL AND \"DeleteAfter\" IS NOT NULL AND \"DeleteLeaseToken\" IS NOT NULL AND \"DeleteLeaseOwner\" IS NOT NULL AND \"DeleteLeaseExpiresAt\" IS NOT NULL AND \"DeleteNextAttemptAt\" IS NULL AND \"DeletedAt\" IS NULL AND \"DeleteAttempts\" >= 1) OR (\"Status\" = 'DeleteFailed' AND \"DeleteRequestedAt\" IS NOT NULL AND \"DeleteReason\" IS NOT NULL AND \"DeleteAfter\" IS NOT NULL AND \"DeleteLeaseToken\" IS NULL AND \"DeleteLeaseOwner\" IS NULL AND \"DeleteLeaseExpiresAt\" IS NULL AND \"DeleteNextAttemptAt\" IS NOT NULL AND \"DeleteFailureCode\" IS NOT NULL AND \"DeletedAt\" IS NULL AND \"DeleteAttempts\" >= 1) OR (\"Status\" = 'Deleted' AND \"DeleteRequestedAt\" IS NOT NULL AND \"DeleteReason\" IS NOT NULL AND \"DeletedAt\" IS NOT NULL AND \"DeleteLeaseToken\" IS NULL AND \"DeleteLeaseOwner\" IS NULL AND \"DeleteLeaseExpiresAt\" IS NULL AND \"DeleteNextAttemptAt\" IS NULL AND \"DeleteFailureCode\" IS NULL AND \"DeleteAttempts\" >= 1))");

            migrationBuilder.AddCheckConstraint(
                name: "ck_backup_replica_status",
                table: "backup_replicas",
                sql: "\"Status\" IN ('Available','DeletePending','Deleting','DeleteFailed','Deleted')");

            migrationBuilder.AddCheckConstraint(
                name: "ck_backup_jobs_retention_policy",
                table: "backup_jobs",
                sql: "\"RetentionKeepFor\" BETWEEN interval '1 second' AND interval '3650 days' AND \"RetentionDeleteGrace\" BETWEEN interval '1 second' AND interval '365 days'");

            migrationBuilder.CreateIndex(
                name: "ix_backup_artifacts_retention_due",
                table: "backup_artifacts",
                column: "RetentionExpiresAt",
                filter: "\"RetentionExpiresAt\" IS NOT NULL AND \"RetentionPinnedAt\" IS NULL AND \"RetentionAppliedAt\" IS NULL");

            migrationBuilder.AddCheckConstraint(
                name: "ck_backup_artifact_retention",
                table: "backup_artifacts",
                sql: "\"RetentionGracePeriod\" BETWEEN interval '1 second' AND interval '365 days' AND (\"RetentionExpiresAt\" IS NULL OR (\"AcceptedAt\" IS NOT NULL AND \"RetentionExpiresAt\" > \"AcceptedAt\"))");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ix_backup_occurrences_decided",
                table: "backup_schedule_occurrences");

            migrationBuilder.DropCheckConstraint(
                name: "ck_backup_runs_retention_policy",
                table: "backup_runs");

            migrationBuilder.DropIndex(
                name: "ix_backup_replicas_delete_failed",
                table: "backup_replicas");

            migrationBuilder.DropIndex(
                name: "ix_backup_replicas_delete_leased",
                table: "backup_replicas");

            migrationBuilder.DropIndex(
                name: "ix_backup_replicas_delete_pending",
                table: "backup_replicas");

            migrationBuilder.DropIndex(
                name: "ix_backup_replicas_location",
                table: "backup_replicas");

            migrationBuilder.DropCheckConstraint(
                name: "ck_backup_replica_lifecycle",
                table: "backup_replicas");

            migrationBuilder.DropCheckConstraint(
                name: "ck_backup_replica_status",
                table: "backup_replicas");

            migrationBuilder.DropCheckConstraint(
                name: "ck_backup_jobs_retention_policy",
                table: "backup_jobs");

            migrationBuilder.DropIndex(
                name: "ix_backup_artifacts_retention_due",
                table: "backup_artifacts");

            migrationBuilder.DropCheckConstraint(
                name: "ck_backup_artifact_retention",
                table: "backup_artifacts");

            migrationBuilder.DropColumn(
                name: "RetentionDeleteGrace",
                table: "backup_runs");

            migrationBuilder.DropColumn(
                name: "RetentionEnabled",
                table: "backup_runs");

            migrationBuilder.DropColumn(
                name: "RetentionKeepFor",
                table: "backup_runs");

            migrationBuilder.DropColumn(
                name: "DeleteAfter",
                table: "backup_replicas");

            migrationBuilder.DropColumn(
                name: "DeleteAttempts",
                table: "backup_replicas");

            migrationBuilder.DropColumn(
                name: "DeleteFailureCode",
                table: "backup_replicas");

            migrationBuilder.DropColumn(
                name: "DeleteLeaseExpiresAt",
                table: "backup_replicas");

            migrationBuilder.DropColumn(
                name: "DeleteLeaseOwner",
                table: "backup_replicas");

            migrationBuilder.DropColumn(
                name: "DeleteLeaseToken",
                table: "backup_replicas");

            migrationBuilder.DropColumn(
                name: "DeleteNextAttemptAt",
                table: "backup_replicas");

            migrationBuilder.DropColumn(
                name: "DeleteReason",
                table: "backup_replicas");

            migrationBuilder.DropColumn(
                name: "DeleteRequestedAt",
                table: "backup_replicas");

            migrationBuilder.DropColumn(
                name: "DeleteRequestedByUserId",
                table: "backup_replicas");

            migrationBuilder.DropColumn(
                name: "DeletedAt",
                table: "backup_replicas");

            migrationBuilder.DropColumn(
                name: "LastDeleteAttemptAt",
                table: "backup_replicas");

            migrationBuilder.DropColumn(
                name: "RetentionDeleteGrace",
                table: "backup_jobs");

            migrationBuilder.DropColumn(
                name: "RetentionEnabled",
                table: "backup_jobs");

            migrationBuilder.DropColumn(
                name: "RetentionKeepFor",
                table: "backup_jobs");

            migrationBuilder.DropColumn(
                name: "AcceptedAt",
                table: "backup_artifacts");

            migrationBuilder.DropColumn(
                name: "RetentionAppliedAt",
                table: "backup_artifacts");

            migrationBuilder.DropColumn(
                name: "RetentionExpiresAt",
                table: "backup_artifacts");

            migrationBuilder.DropColumn(
                name: "RetentionGracePeriod",
                table: "backup_artifacts");

            migrationBuilder.DropColumn(
                name: "RetentionPinnedAt",
                table: "backup_artifacts");

            migrationBuilder.AlterColumn<string>(
                name: "Status",
                table: "backup_replicas",
                type: "text",
                nullable: false,
                oldClrType: typeof(string),
                oldType: "character varying(16)",
                oldMaxLength: 16);

            migrationBuilder.AddCheckConstraint(
                name: "ck_backup_replica_status",
                table: "backup_replicas",
                sql: "\"Status\" = 'Available'");
        }
    }
}
