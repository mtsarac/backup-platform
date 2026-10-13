using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BackupPlatform.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class RepairQuotaReservation : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<long>(
                name: "ReservedQuotaBytes",
                table: "replica_repair_operations",
                type: "bigint",
                nullable: false,
                defaultValue: 0L);

            migrationBuilder.AddCheckConstraint(
                name: "ck_replica_repair_operations_reserved",
                table: "replica_repair_operations",
                sql: "\"ReservedQuotaBytes\" >= 0");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "ck_replica_repair_operations_reserved",
                table: "replica_repair_operations");

            migrationBuilder.DropColumn(
                name: "ReservedQuotaBytes",
                table: "replica_repair_operations");
        }
    }
}
