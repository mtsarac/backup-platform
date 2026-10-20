using System;
using BackupPlatform.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BackupPlatform.Infrastructure.Persistence.Migrations;

[DbContext(typeof(PlatformDbContext))]
[Migration("20261021100000_StorageCapacitySnapshots")]
public partial class StorageCapacitySnapshots : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateTable(
            name: "storage_capacity_snapshots",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "uuid", nullable: false),
                TenantId = table.Column<Guid>(type: "uuid", nullable: false),
                ConnectionId = table.Column<Guid>(type: "uuid", nullable: false),
                TotalBytes = table.Column<long>(type: "bigint", nullable: true),
                UsedBytes = table.Column<long>(type: "bigint", nullable: true),
                FreeBytes = table.Column<long>(type: "bigint", nullable: true),
                Status = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                ReportedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                ErrorMessage = table.Column<string>(type: "character varying(1024)", maxLength: 1024, nullable: true),
                xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_storage_capacity_snapshots", x => x.Id);
                table.UniqueConstraint("AK_storage_capacity_snapshots_Id_TenantId", x => new { x.Id, x.TenantId });
                table.CheckConstraint("ck_storage_capacity_snapshots_id", "\"Id\" <> '00000000-0000-0000-0000-000000000000'::uuid");
                table.CheckConstraint("ck_storage_capacity_snapshots_tenant", "\"TenantId\" <> '00000000-0000-0000-0000-000000000000'::uuid");
                table.CheckConstraint("ck_storage_capacity_snapshots_status", "\"Status\" IN ('Available','CapacityUnavailable')");
                table.ForeignKey(
                    name: "FK_storage_capacity_snapshots_storage_connections_ConnectionId",
                    column: x => x.ConnectionId,
                    principalTable: "storage_connections",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.Cascade);
                table.ForeignKey(
                    name: "FK_storage_capacity_snapshots_tenants_TenantId",
                    column: x => x.TenantId,
                    principalTable: "tenants",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.Restrict);
            });

        migrationBuilder.CreateIndex(
            name: "IX_storage_capacity_snapshots_ConnectionId",
            table: "storage_capacity_snapshots",
            column: "ConnectionId");

        migrationBuilder.CreateIndex(
            name: "IX_storage_capacity_snapshots_TenantId",
            table: "storage_capacity_snapshots",
            column: "TenantId");

        migrationBuilder.CreateIndex(
            name: "ix_storage_capacity_snapshots_connection",
            table: "storage_capacity_snapshots",
            columns: new[] { "TenantId", "ConnectionId", "ReportedAt" });
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable(
            name: "storage_capacity_snapshots");
    }
}
