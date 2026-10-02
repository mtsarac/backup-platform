using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BackupPlatform.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AgentConnectivity : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "agent_connectivity",
                columns: table => new
                {
                    AgentId = table.Column<Guid>(type: "uuid", nullable: false),
                    TenantId = table.Column<Guid>(type: "uuid", nullable: false),
                    LastSeenAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    LastHeartbeatAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    LastConnectedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    ReportedAgentVersion = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    ReportedProtocolMajor = table.Column<int>(type: "integer", nullable: true),
                    ReportedProtocolMinor = table.Column<int>(type: "integer", nullable: true),
                    ReportedPlatform = table.Column<int>(type: "integer", nullable: true),
                    ReportedOsDescription = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    ReportedRuntimeVersion = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    CapabilityRevision = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_agent_connectivity", x => x.AgentId);
                    table.CheckConstraint("ck_agent_connectivity_protocol", "(\"ReportedProtocolMajor\" IS NULL) = (\"ReportedProtocolMinor\" IS NULL) AND (\"ReportedProtocolMajor\" IS NULL OR (\"ReportedProtocolMajor\" >= 1 AND \"ReportedProtocolMinor\" >= 0))");
                    table.CheckConstraint("ck_agent_connectivity_tenant_id", "\"TenantId\" <> '00000000-0000-0000-0000-000000000000'::uuid");
                    table.ForeignKey(
                        name: "FK_agent_connectivity_agents_AgentId_TenantId",
                        columns: x => new { x.AgentId, x.TenantId },
                        principalTable: "agents",
                        principalColumns: new[] { "Id", "TenantId" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_agent_connectivity_AgentId_TenantId",
                table: "agent_connectivity",
                columns: new[] { "AgentId", "TenantId" });

            migrationBuilder.CreateIndex(
                name: "IX_agent_connectivity_TenantId_LastSeenAt",
                table: "agent_connectivity",
                columns: new[] { "TenantId", "LastSeenAt" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "agent_connectivity");
        }
    }
}
