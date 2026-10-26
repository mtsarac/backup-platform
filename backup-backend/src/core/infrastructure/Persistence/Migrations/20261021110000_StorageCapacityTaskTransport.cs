using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BackupPlatform.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class StorageCapacityTaskTransport : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "ck_storage_capacity_snapshots_status",
                table: "storage_capacity_snapshots");

            migrationBuilder.CreateTable(
                name: "storage_capacity_tasks",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    TenantId = table.Column<Guid>(type: "uuid", nullable: false),
                    ConnectionId = table.Column<Guid>(type: "uuid", nullable: false),
                    AgentId = table.Column<Guid>(type: "uuid", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    ExpiresAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    LeaseToken = table.Column<Guid>(type: "uuid", nullable: true),
                    LeaseExpiresAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    ConnectionUpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    Attempts = table.Column<int>(type: "integer", nullable: false),
                    CompletedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    CompletionJson = table.Column<string>(type: "jsonb", nullable: true),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_storage_capacity_tasks", x => x.Id);
                    table.UniqueConstraint("AK_storage_capacity_tasks_Id_TenantId", x => new { x.Id, x.TenantId });
                    table.CheckConstraint("ck_storage_capacity_tasks_id", "\"Id\" <> '00000000-0000-0000-0000-000000000000'::uuid");
                    table.CheckConstraint("ck_storage_capacity_tasks_tenant", "\"TenantId\" <> '00000000-0000-0000-0000-000000000000'::uuid");
                    table.ForeignKey(
                        name: "FK_storage_capacity_tasks_storage_connections_ConnectionId_Ten~",
                        columns: x => new { x.ConnectionId, x.TenantId },
                        principalTable: "storage_connections",
                        principalColumns: new[] { "Id", "TenantId" },
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_storage_capacity_tasks_tenants_TenantId",
                        column: x => x.TenantId,
                        principalTable: "tenants",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.AddCheckConstraint(
                name: "ck_storage_capacity_snapshots_status",
                table: "storage_capacity_snapshots",
                sql: "\"Status\" IN ('Available','Warning','Critical','CapacityUnavailable')");

            migrationBuilder.CreateIndex(
                name: "IX_storage_capacity_tasks_ConnectionId_TenantId",
                table: "storage_capacity_tasks",
                columns: new[] { "ConnectionId", "TenantId" });

            migrationBuilder.CreateIndex(
                name: "IX_storage_capacity_tasks_TenantId",
                table: "storage_capacity_tasks",
                column: "TenantId");

            migrationBuilder.CreateIndex(
                name: "IX_storage_capacity_tasks_TenantId_AgentId_CreatedAt",
                table: "storage_capacity_tasks",
                columns: new[] { "TenantId", "AgentId", "CreatedAt" });

            migrationBuilder.CreateIndex(
                name: "ux_storage_capacity_tasks_pending",
                table: "storage_capacity_tasks",
                columns: new[] { "TenantId", "ConnectionId" },
                unique: true,
                filter: "\"CompletedAt\" IS NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "storage_capacity_tasks");

            migrationBuilder.DropCheckConstraint(
                name: "ck_storage_capacity_snapshots_status",
                table: "storage_capacity_snapshots");

            migrationBuilder.AddCheckConstraint(
                name: "ck_storage_capacity_snapshots_status",
                table: "storage_capacity_snapshots",
                sql: "\"Status\" IN ('Available','CapacityUnavailable')");
        }
    }
}
