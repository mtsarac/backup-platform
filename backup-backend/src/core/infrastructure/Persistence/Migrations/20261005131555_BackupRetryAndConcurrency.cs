using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BackupPlatform.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class BackupRetryAndConcurrency : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "ck_backup_runs_status",
                table: "backup_runs");

            migrationBuilder.DropIndex(
                name: "IX_backup_run_attempts_TenantId_AgentId",
                table: "backup_run_attempts");

            migrationBuilder.DropCheckConstraint(
                name: "ck_backup_job_policy",
                table: "backup_jobs");

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "NextAttemptAt",
                table: "backup_runs",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "OverlapPolicy",
                table: "backup_runs",
                type: "character varying(16)",
                maxLength: 16,
                nullable: false,
                defaultValue: "Queue");

            migrationBuilder.AddColumn<int>(
                name: "RetryBackoff",
                table: "backup_runs",
                type: "integer",
                nullable: false,
                defaultValue: 2);

            migrationBuilder.AddColumn<TimeSpan>(
                name: "RetryInitialDelay",
                table: "backup_runs",
                type: "interval",
                nullable: false,
                defaultValue: TimeSpan.FromSeconds(30));

            migrationBuilder.AddColumn<double>(
                name: "RetryJitter",
                table: "backup_runs",
                type: "double precision",
                nullable: false,
                defaultValue: 0.2);

            migrationBuilder.AddColumn<int>(
                name: "RetryMaxAttempts",
                table: "backup_runs",
                type: "integer",
                nullable: false,
                defaultValue: 3);

            migrationBuilder.AddColumn<TimeSpan>(
                name: "RetryMaxDelay",
                table: "backup_runs",
                type: "interval",
                nullable: false,
                defaultValue: TimeSpan.FromMinutes(10));

            migrationBuilder.AddColumn<int>(
                name: "RetryState",
                table: "backup_runs",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "EligibleAt",
                table: "backup_run_attempts",
                type: "timestamp with time zone",
                nullable: false,
                defaultValue: new DateTimeOffset(new DateTime(1, 1, 1, 0, 0, 0, 0, DateTimeKind.Unspecified), new TimeSpan(0, 0, 0, 0, 0)));

            migrationBuilder.Sql("UPDATE backup_run_attempts SET \"EligibleAt\" = \"CreatedAt\";");

            migrationBuilder.AlterColumn<string>(
                name: "OverlapPolicy",
                table: "backup_jobs",
                type: "character varying(16)",
                maxLength: 16,
                nullable: false,
                oldClrType: typeof(string),
                oldType: "text");

            migrationBuilder.AddColumn<int>(
                name: "RetryBackoff",
                table: "backup_jobs",
                type: "integer",
                nullable: false,
                defaultValue: 2);

            migrationBuilder.AddColumn<TimeSpan>(
                name: "RetryInitialDelay",
                table: "backup_jobs",
                type: "interval",
                nullable: false,
                defaultValue: TimeSpan.FromSeconds(30));

            migrationBuilder.AddColumn<double>(
                name: "RetryJitter",
                table: "backup_jobs",
                type: "double precision",
                nullable: false,
                defaultValue: 0.2);

            migrationBuilder.AddColumn<int>(
                name: "RetryMaxAttempts",
                table: "backup_jobs",
                type: "integer",
                nullable: false,
                defaultValue: 3);

            migrationBuilder.AddColumn<TimeSpan>(
                name: "RetryMaxDelay",
                table: "backup_jobs",
                type: "interval",
                nullable: false,
                defaultValue: TimeSpan.FromMinutes(10));

            migrationBuilder.CreateIndex(
                name: "IX_backup_runs_NextAttemptAt_Id",
                table: "backup_runs",
                columns: new[] { "NextAttemptAt", "Id" },
                filter: "\"Status\" = 8");

            migrationBuilder.CreateIndex(
                name: "IX_backup_runs_TenantId_JobId",
                table: "backup_runs",
                columns: new[] { "TenantId", "JobId" },
                unique: true,
                filter: "\"OverlapPolicy\" = 'Skip' AND \"Status\" IN (1,2,3,8)");

            migrationBuilder.AddCheckConstraint(
                name: "ck_backup_runs_attempt_budget",
                table: "backup_runs",
                sql: "\"CurrentAttemptNumber\" <= \"RetryMaxAttempts\"");

            migrationBuilder.AddCheckConstraint(
                name: "ck_backup_runs_overlap",
                table: "backup_runs",
                sql: "\"OverlapPolicy\" IN ('Skip','Queue')");

            migrationBuilder.AddCheckConstraint(
                name: "ck_backup_runs_retry_policy",
                table: "backup_runs",
                sql: "\"RetryMaxAttempts\" BETWEEN 1 AND 10 AND \"RetryInitialDelay\" >= interval '1 second' AND \"RetryMaxDelay\" >= \"RetryInitialDelay\" AND \"RetryMaxDelay\" <= interval '24 hours' AND \"RetryBackoff\" IN (1,2) AND \"RetryJitter\" BETWEEN 0 AND 1");

            migrationBuilder.AddCheckConstraint(
                name: "ck_backup_runs_retry_schedule",
                table: "backup_runs",
                sql: "(\"Status\" = 8) = (\"NextAttemptAt\" IS NOT NULL)");

            migrationBuilder.AddCheckConstraint(
                name: "ck_backup_runs_retry_state",
                table: "backup_runs",
                sql: "\"RetryState\" BETWEEN 0 AND 3 AND ((\"Status\" = 8) = (\"RetryState\" = 1))");

            migrationBuilder.AddCheckConstraint(
                name: "ck_backup_runs_status",
                table: "backup_runs",
                sql: "\"Status\" BETWEEN 1 AND 8");

            migrationBuilder.CreateIndex(
                name: "IX_backup_run_attempts_TenantId_AgentId_EligibleAt",
                table: "backup_run_attempts",
                columns: new[] { "TenantId", "AgentId", "EligibleAt" },
                filter: "\"Status\" = 1");

            migrationBuilder.CreateIndex(
                name: "IX_backup_run_attempts_TenantId_AgentId_Status",
                table: "backup_run_attempts",
                columns: new[] { "TenantId", "AgentId", "Status" });

            migrationBuilder.AddCheckConstraint(
                name: "ck_backup_job_policy",
                table: "backup_jobs",
                sql: "\"BackupType\" = 'Full' AND \"OverlapPolicy\" IN ('Skip','Queue')");

            migrationBuilder.AddCheckConstraint(
                name: "ck_backup_jobs_retry_policy",
                table: "backup_jobs",
                sql: "\"RetryMaxAttempts\" BETWEEN 1 AND 10 AND \"RetryInitialDelay\" >= interval '1 second' AND \"RetryMaxDelay\" >= \"RetryInitialDelay\" AND \"RetryMaxDelay\" <= interval '24 hours' AND \"RetryBackoff\" IN (1,2) AND \"RetryJitter\" BETWEEN 0 AND 1");

            // Terminal outcomes are immutable in the database, whatever the application does.
            migrationBuilder.Sql("""
                CREATE FUNCTION backup_attempt_terminal_guard() RETURNS trigger LANGUAGE plpgsql AS $$
                BEGIN
                    IF OLD."Status" IN (4,5,6,7) AND (NEW."Status" IS DISTINCT FROM OLD."Status"
                        OR NEW."TerminalReason" IS DISTINCT FROM OLD."TerminalReason"
                        OR NEW."CompletedAt" IS DISTINCT FROM OLD."CompletedAt") THEN
                        RAISE EXCEPTION 'A terminal backup attempt is immutable.' USING ERRCODE = '23514';
                    END IF;
                    RETURN NEW;
                END $$;
                CREATE TRIGGER trg_backup_attempt_terminal_guard BEFORE UPDATE ON backup_run_attempts
                    FOR EACH ROW EXECUTE FUNCTION backup_attempt_terminal_guard();
                """);
            // Succeeded and Cancelled runs never change state; Failed and AgentLost runs may only be reopened (Pending) by an explicit manual retry.
            migrationBuilder.Sql("""
                CREATE FUNCTION backup_run_terminal_guard() RETURNS trigger LANGUAGE plpgsql AS $$
                BEGIN
                    IF NEW."Status" IS DISTINCT FROM OLD."Status" AND (OLD."Status" IN (4,6) OR (OLD."Status" IN (5,7) AND NEW."Status" <> 1)) THEN
                        RAISE EXCEPTION 'A terminal backup run cannot change state.' USING ERRCODE = '23514';
                    END IF;
                    RETURN NEW;
                END $$;
                CREATE TRIGGER trg_backup_run_terminal_guard BEFORE UPDATE ON backup_runs
                    FOR EACH ROW EXECUTE FUNCTION backup_run_terminal_guard();
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("DROP TRIGGER trg_backup_run_terminal_guard ON backup_runs; DROP FUNCTION backup_run_terminal_guard();");
            migrationBuilder.Sql("DROP TRIGGER trg_backup_attempt_terminal_guard ON backup_run_attempts; DROP FUNCTION backup_attempt_terminal_guard();");

            migrationBuilder.DropIndex(
                name: "IX_backup_runs_NextAttemptAt_Id",
                table: "backup_runs");

            migrationBuilder.DropIndex(
                name: "IX_backup_runs_TenantId_JobId",
                table: "backup_runs");

            migrationBuilder.DropCheckConstraint(
                name: "ck_backup_runs_attempt_budget",
                table: "backup_runs");

            migrationBuilder.DropCheckConstraint(
                name: "ck_backup_runs_overlap",
                table: "backup_runs");

            migrationBuilder.DropCheckConstraint(
                name: "ck_backup_runs_retry_policy",
                table: "backup_runs");

            migrationBuilder.DropCheckConstraint(
                name: "ck_backup_runs_retry_schedule",
                table: "backup_runs");

            migrationBuilder.DropCheckConstraint(
                name: "ck_backup_runs_retry_state",
                table: "backup_runs");

            migrationBuilder.DropCheckConstraint(
                name: "ck_backup_runs_status",
                table: "backup_runs");

            migrationBuilder.DropIndex(
                name: "IX_backup_run_attempts_TenantId_AgentId_EligibleAt",
                table: "backup_run_attempts");

            migrationBuilder.DropIndex(
                name: "IX_backup_run_attempts_TenantId_AgentId_Status",
                table: "backup_run_attempts");

            migrationBuilder.DropCheckConstraint(
                name: "ck_backup_job_policy",
                table: "backup_jobs");

            migrationBuilder.DropCheckConstraint(
                name: "ck_backup_jobs_retry_policy",
                table: "backup_jobs");

            migrationBuilder.DropColumn(
                name: "NextAttemptAt",
                table: "backup_runs");

            migrationBuilder.DropColumn(
                name: "OverlapPolicy",
                table: "backup_runs");

            migrationBuilder.DropColumn(
                name: "RetryBackoff",
                table: "backup_runs");

            migrationBuilder.DropColumn(
                name: "RetryInitialDelay",
                table: "backup_runs");

            migrationBuilder.DropColumn(
                name: "RetryJitter",
                table: "backup_runs");

            migrationBuilder.DropColumn(
                name: "RetryMaxAttempts",
                table: "backup_runs");

            migrationBuilder.DropColumn(
                name: "RetryMaxDelay",
                table: "backup_runs");

            migrationBuilder.DropColumn(
                name: "RetryState",
                table: "backup_runs");

            migrationBuilder.DropColumn(
                name: "EligibleAt",
                table: "backup_run_attempts");

            migrationBuilder.DropColumn(
                name: "RetryBackoff",
                table: "backup_jobs");

            migrationBuilder.DropColumn(
                name: "RetryInitialDelay",
                table: "backup_jobs");

            migrationBuilder.DropColumn(
                name: "RetryJitter",
                table: "backup_jobs");

            migrationBuilder.DropColumn(
                name: "RetryMaxAttempts",
                table: "backup_jobs");

            migrationBuilder.DropColumn(
                name: "RetryMaxDelay",
                table: "backup_jobs");

            migrationBuilder.AlterColumn<string>(
                name: "OverlapPolicy",
                table: "backup_jobs",
                type: "text",
                nullable: false,
                oldClrType: typeof(string),
                oldType: "character varying(16)",
                oldMaxLength: 16);

            migrationBuilder.AddCheckConstraint(
                name: "ck_backup_runs_status",
                table: "backup_runs",
                sql: "\"Status\" BETWEEN 1 AND 7");

            migrationBuilder.CreateIndex(
                name: "IX_backup_run_attempts_TenantId_AgentId",
                table: "backup_run_attempts",
                columns: new[] { "TenantId", "AgentId" },
                unique: true,
                filter: "\"Status\" IN (2,3)");

            migrationBuilder.AddCheckConstraint(
                name: "ck_backup_job_policy",
                table: "backup_jobs",
                sql: "\"BackupType\" = 'Full' AND \"OverlapPolicy\" = 'Queue'");
        }
    }
}
