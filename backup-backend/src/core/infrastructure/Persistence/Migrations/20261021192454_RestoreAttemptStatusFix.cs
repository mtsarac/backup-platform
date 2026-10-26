using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BackupPlatform.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class RestoreAttemptStatusFix : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "ck_backup_run_attempts_status",
                table: "backup_run_attempts");

            migrationBuilder.AddCheckConstraint(
                name: "ck_backup_run_attempts_status",
                table: "backup_run_attempts",
                sql: "(\"Status\" BETWEEN 1 AND 7) OR \"Status\" = 9");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "ck_backup_run_attempts_status",
                table: "backup_run_attempts");

            migrationBuilder.AddCheckConstraint(
                name: "ck_backup_run_attempts_status",
                table: "backup_run_attempts",
                sql: "\"Status\" BETWEEN 1 AND 9");
        }
    }
}
