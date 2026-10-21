using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BackupPlatform.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class BackupSizeAnomalyDetection : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<long>(
                name: "SizeAnomalyBaselineBytes",
                table: "backup_runs",
                type: "bigint",
                nullable: true);

            migrationBuilder.AddColumn<double>(
                name: "SizeAnomalyDeviationRatio",
                table: "backup_runs",
                type: "double precision",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "SizeAnomalyStatus",
                table: "backup_runs",
                type: "character varying(32)",
                maxLength: 32,
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_backup_runs_TenantId_SizeAnomalyStatus_CompletedAt",
                table: "backup_runs",
                columns: new[] { "TenantId", "SizeAnomalyStatus", "CompletedAt" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_backup_runs_TenantId_SizeAnomalyStatus_CompletedAt",
                table: "backup_runs");

            migrationBuilder.DropColumn(
                name: "SizeAnomalyBaselineBytes",
                table: "backup_runs");

            migrationBuilder.DropColumn(
                name: "SizeAnomalyDeviationRatio",
                table: "backup_runs");

            migrationBuilder.DropColumn(
                name: "SizeAnomalyStatus",
                table: "backup_runs");
        }
    }
}
