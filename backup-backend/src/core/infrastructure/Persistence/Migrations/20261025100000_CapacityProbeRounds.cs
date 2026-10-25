using System;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BackupPlatform.Infrastructure.Persistence.Migrations;

/// <summary>
/// A connection that jobs of several agents write to is probed once per online agent, so a probe round holds one pending task per agent
/// (at most one per agent and connection) and its results are reduced to the lowest free space.
/// </summary>
[DbContext(typeof(PlatformDbContext))]
[Migration("20261025100000_CapacityProbeRounds")]
public partial class CapacityProbeRounds : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<Guid>("RoundId", "storage_capacity_tasks", type: "uuid", nullable: false, defaultValue: Guid.Empty);
        migrationBuilder.Sql("""UPDATE storage_capacity_tasks SET "RoundId" = "Id";""");
        migrationBuilder.DropIndex("ux_storage_capacity_tasks_pending", "storage_capacity_tasks");
        migrationBuilder.CreateIndex("ux_storage_capacity_tasks_pending", "storage_capacity_tasks", new[] { "TenantId", "ConnectionId", "AgentId" },
            unique: true, filter: "\"CompletedAt\" IS NULL");
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        // Several pending tasks of one connection cannot coexist under the single-task index: keep the oldest.
        migrationBuilder.Sql("""
            DELETE FROM storage_capacity_tasks t USING storage_capacity_tasks newer
            WHERE t."CompletedAt" IS NULL AND newer."CompletedAt" IS NULL AND t."TenantId" = newer."TenantId" AND t."ConnectionId" = newer."ConnectionId"
              AND (newer."CreatedAt", newer."Id") < (t."CreatedAt", t."Id");
            """);
        migrationBuilder.DropIndex("ux_storage_capacity_tasks_pending", "storage_capacity_tasks");
        migrationBuilder.CreateIndex("ux_storage_capacity_tasks_pending", "storage_capacity_tasks", new[] { "TenantId", "ConnectionId" },
            unique: true, filter: "\"CompletedAt\" IS NULL");
        migrationBuilder.DropColumn("RoundId", "storage_capacity_tasks");
    }
}
