using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BackupPlatform.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class TransferLeases : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "backup_transfer_leases",
                columns: table => new
                {
                    LockId = table.Column<long>(type: "bigint", nullable: false),
                    OwnerId = table.Column<Guid>(type: "uuid", nullable: false),
                    ExpiresAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_backup_transfer_leases", x => x.LockId);
                });

            migrationBuilder.CreateIndex(
                name: "IX_backup_transfer_leases_ExpiresAt",
                table: "backup_transfer_leases",
                column: "ExpiresAt");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "backup_transfer_leases");
        }
    }
}
