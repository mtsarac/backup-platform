using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BackupPlatform.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class BackupScheduling : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "ck_backup_runs_type",
                table: "backup_runs");

            migrationBuilder.AlterColumn<string>(
                name: "TriggerType",
                table: "backup_runs",
                type: "character varying(16)",
                maxLength: 16,
                nullable: false,
                oldClrType: typeof(string),
                oldType: "text");

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "ClaimDeadline",
                table: "backup_runs",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "ScheduledOccurrence",
                table: "backup_runs",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ScheduleCron",
                table: "backup_jobs",
                type: "character varying(128)",
                maxLength: 128,
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "ScheduleEffectiveFrom",
                table: "backup_jobs",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "ScheduleEnabled",
                table: "backup_jobs",
                type: "boolean",
                nullable: true);

            migrationBuilder.AddColumn<TimeSpan>(
                name: "ScheduleMissedRunGrace",
                table: "backup_jobs",
                type: "interval",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ScheduleTimeZone",
                table: "backup_jobs",
                type: "character varying(64)",
                maxLength: 64,
                nullable: true);

            migrationBuilder.CreateTable(
                name: "backup_schedule_occurrences",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    TenantId = table.Column<Guid>(type: "uuid", nullable: false),
                    JobId = table.Column<Guid>(type: "uuid", nullable: false),
                    ScheduledAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    Outcome = table.Column<string>(type: "character varying(24)", maxLength: 24, nullable: false),
                    Reason = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    RunId = table.Column<Guid>(type: "uuid", nullable: true),
                    DecidedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_backup_schedule_occurrences", x => x.Id);
                    table.UniqueConstraint("AK_backup_schedule_occurrences_Id_TenantId", x => new { x.Id, x.TenantId });
                    table.CheckConstraint("ck_backup_occurrence_outcome", "\"Outcome\" IN ('RunCreated','SkippedOverlap','MissedGraceExpired','MissedSuperseded','JobDisabled')");
                    table.CheckConstraint("ck_backup_occurrence_run", "(\"Outcome\" IN ('RunCreated','SkippedOverlap')) = (\"RunId\" IS NOT NULL)");
                    table.CheckConstraint("ck_backup_schedule_occurrences_id", "\"Id\" <> '00000000-0000-0000-0000-000000000000'::uuid");
                    table.CheckConstraint("ck_backup_schedule_occurrences_tenant", "\"TenantId\" <> '00000000-0000-0000-0000-000000000000'::uuid");
                    table.ForeignKey(
                        name: "FK_backup_schedule_occurrences_backup_jobs_JobId_TenantId",
                        columns: x => new { x.JobId, x.TenantId },
                        principalTable: "backup_jobs",
                        principalColumns: new[] { "Id", "TenantId" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_backup_schedule_occurrences_backup_runs_RunId_TenantId",
                        columns: x => new { x.RunId, x.TenantId },
                        principalTable: "backup_runs",
                        principalColumns: new[] { "Id", "TenantId" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_backup_schedule_occurrences_tenants_TenantId",
                        column: x => x.TenantId,
                        principalTable: "tenants",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_backup_runs_ClaimDeadline_Id",
                table: "backup_runs",
                columns: new[] { "ClaimDeadline", "Id" },
                filter: "\"Status\" = 1 AND \"ClaimDeadline\" IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_backup_runs_TenantId_JobId_ScheduledOccurrence",
                table: "backup_runs",
                columns: new[] { "TenantId", "JobId", "ScheduledOccurrence" },
                unique: true,
                filter: "\"ScheduledOccurrence\" IS NOT NULL");

            migrationBuilder.AddCheckConstraint(
                name: "ck_backup_runs_trigger",
                table: "backup_runs",
                sql: "(\"TriggerType\" = 'Manual' AND \"ScheduledOccurrence\" IS NULL AND \"ClaimDeadline\" IS NULL) OR (\"TriggerType\" = 'Scheduled' AND \"ScheduledOccurrence\" IS NOT NULL AND \"ClaimDeadline\" IS NOT NULL AND \"ClaimDeadline\" >= \"ScheduledOccurrence\")");

            migrationBuilder.AddCheckConstraint(
                name: "ck_backup_runs_type",
                table: "backup_runs",
                sql: "\"BackupType\" = 'Full'");

            migrationBuilder.CreateIndex(
                name: "ix_backup_jobs_scheduled",
                table: "backup_jobs",
                columns: new[] { "TenantId", "Id" },
                filter: "\"Enabled\" AND \"ScheduleEnabled\"");

            migrationBuilder.AddCheckConstraint(
                name: "ck_backup_jobs_schedule",
                table: "backup_jobs",
                sql: "(\"ScheduleEnabled\" IS NULL AND \"ScheduleCron\" IS NULL AND \"ScheduleTimeZone\" IS NULL AND \"ScheduleMissedRunGrace\" IS NULL AND \"ScheduleEffectiveFrom\" IS NULL) OR (\"ScheduleEnabled\" IS NOT NULL AND \"ScheduleCron\" IS NOT NULL AND \"ScheduleTimeZone\" IS NOT NULL AND \"ScheduleMissedRunGrace\" IS NOT NULL AND \"ScheduleEffectiveFrom\" IS NOT NULL AND \"ScheduleMissedRunGrace\" BETWEEN interval '1 minute' AND interval '7 days')");

            migrationBuilder.CreateIndex(
                name: "IX_backup_schedule_occurrences_JobId_TenantId",
                table: "backup_schedule_occurrences",
                columns: new[] { "JobId", "TenantId" });

            migrationBuilder.CreateIndex(
                name: "IX_backup_schedule_occurrences_RunId_TenantId",
                table: "backup_schedule_occurrences",
                columns: new[] { "RunId", "TenantId" });

            migrationBuilder.CreateIndex(
                name: "IX_backup_schedule_occurrences_TenantId",
                table: "backup_schedule_occurrences",
                column: "TenantId");

            migrationBuilder.CreateIndex(
                name: "IX_backup_schedule_occurrences_TenantId_JobId_ScheduledAt",
                table: "backup_schedule_occurrences",
                columns: new[] { "TenantId", "JobId", "ScheduledAt" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_backup_schedule_occurrences_TenantId_RunId",
                table: "backup_schedule_occurrences",
                columns: new[] { "TenantId", "RunId" },
                unique: true,
                filter: "\"Outcome\" = 'RunCreated'");

            // A decided occurrence is history and the scheduler's idempotency memory: it can be deleted by retention, never rewritten.
            migrationBuilder.Sql("""
                CREATE FUNCTION backup_occurrence_immutable() RETURNS trigger LANGUAGE plpgsql AS $$
                BEGIN
                    RAISE EXCEPTION 'A decided schedule occurrence is immutable.' USING ERRCODE = '23514';
                END $$;
                CREATE TRIGGER trg_backup_occurrence_immutable BEFORE UPDATE ON backup_schedule_occurrences
                    FOR EACH ROW EXECUTE FUNCTION backup_occurrence_immutable();
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("DROP TRIGGER trg_backup_occurrence_immutable ON backup_schedule_occurrences; DROP FUNCTION backup_occurrence_immutable();");

            migrationBuilder.DropTable(
                name: "backup_schedule_occurrences");

            migrationBuilder.DropIndex(
                name: "IX_backup_runs_ClaimDeadline_Id",
                table: "backup_runs");

            migrationBuilder.DropIndex(
                name: "IX_backup_runs_TenantId_JobId_ScheduledOccurrence",
                table: "backup_runs");

            migrationBuilder.DropCheckConstraint(
                name: "ck_backup_runs_trigger",
                table: "backup_runs");

            migrationBuilder.DropCheckConstraint(
                name: "ck_backup_runs_type",
                table: "backup_runs");

            migrationBuilder.DropIndex(
                name: "ix_backup_jobs_scheduled",
                table: "backup_jobs");

            migrationBuilder.DropCheckConstraint(
                name: "ck_backup_jobs_schedule",
                table: "backup_jobs");

            migrationBuilder.DropColumn(
                name: "ClaimDeadline",
                table: "backup_runs");

            migrationBuilder.DropColumn(
                name: "ScheduledOccurrence",
                table: "backup_runs");

            migrationBuilder.DropColumn(
                name: "ScheduleCron",
                table: "backup_jobs");

            migrationBuilder.DropColumn(
                name: "ScheduleEffectiveFrom",
                table: "backup_jobs");

            migrationBuilder.DropColumn(
                name: "ScheduleEnabled",
                table: "backup_jobs");

            migrationBuilder.DropColumn(
                name: "ScheduleMissedRunGrace",
                table: "backup_jobs");

            migrationBuilder.DropColumn(
                name: "ScheduleTimeZone",
                table: "backup_jobs");

            migrationBuilder.AlterColumn<string>(
                name: "TriggerType",
                table: "backup_runs",
                type: "text",
                nullable: false,
                oldClrType: typeof(string),
                oldType: "character varying(16)",
                oldMaxLength: 16);

            migrationBuilder.AddCheckConstraint(
                name: "ck_backup_runs_type",
                table: "backup_runs",
                sql: "\"BackupType\" = 'Full' AND \"TriggerType\" = 'Manual'");
        }
    }
}
