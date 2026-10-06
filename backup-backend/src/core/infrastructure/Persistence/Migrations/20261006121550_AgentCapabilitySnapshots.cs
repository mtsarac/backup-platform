using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BackupPlatform.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AgentCapabilitySnapshots : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "agent_capabilities",
                columns: table => new
                {
                    AgentId = table.Column<Guid>(type: "uuid", nullable: false),
                    TenantId = table.Column<Guid>(type: "uuid", nullable: false),
                    Identifier = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    Support = table.Column<int>(type: "integer", nullable: false),
                    MetadataJson = table.Column<string>(type: "character varying(8192)", maxLength: 8192, nullable: true),
                    Revision = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    ReportedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_agent_capabilities", x => new { x.AgentId, x.TenantId, x.Identifier });
                    table.CheckConstraint("ck_agent_capabilities_support", "\"Support\" IN (0, 1, 2)");
                    table.CheckConstraint("ck_agent_capabilities_tenant_id", "\"TenantId\" <> '00000000-0000-0000-0000-000000000000'::uuid");
                    table.CheckConstraint("ck_agent_capabilities_text", "length(\"Identifier\") > 0 AND length(\"Revision\") > 0");
                    table.ForeignKey(
                        name: "FK_agent_capabilities_agents_AgentId_TenantId",
                        columns: x => new { x.AgentId, x.TenantId },
                        principalTable: "agents",
                        principalColumns: new[] { "Id", "TenantId" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_agent_capabilities_TenantId_AgentId",
                table: "agent_capabilities",
                columns: new[] { "TenantId", "AgentId" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "agent_capabilities");
        }
    }
}
