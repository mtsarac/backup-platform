using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BackupPlatform.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class DatabaseDiscovery : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "discovery_agent_states",
                columns: table => new
                {
                    AgentId = table.Column<Guid>(type: "uuid", nullable: false),
                    TenantId = table.Column<Guid>(type: "uuid", nullable: false),
                    Enabled = table.Column<bool>(type: "boolean", nullable: false),
                    ScanIntervalMinutes = table.Column<int>(type: "integer", nullable: false),
                    IncludeDocker = table.Column<bool>(type: "boolean", nullable: false),
                    ScanRequestedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    ScanRequestReason = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: true),
                    RequestedInstanceKey = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    ScanId = table.Column<Guid>(type: "uuid", nullable: true),
                    ScanReason = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: true),
                    ScanStartedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    NextScanDueAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    LastScanAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    LastSuccessfulScanAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    LastOutcome = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: true),
                    LastFailureCode = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    SourcesJson = table.Column<string>(type: "jsonb", nullable: false),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_discovery_agent_states", x => x.AgentId);
                    table.CheckConstraint("ck_discovery_agent_states_ids", "\"AgentId\" <> '00000000-0000-0000-0000-000000000000'::uuid AND \"TenantId\" <> '00000000-0000-0000-0000-000000000000'::uuid");
                    table.CheckConstraint("ck_discovery_agent_states_interval", "\"ScanIntervalMinutes\" BETWEEN 15 AND 10080");
                    table.ForeignKey(
                        name: "FK_discovery_agent_states_agents_AgentId_TenantId",
                        columns: x => new { x.AgentId, x.TenantId },
                        principalTable: "agents",
                        principalColumns: new[] { "Id", "TenantId" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "discovery_instances",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    TenantId = table.Column<Guid>(type: "uuid", nullable: false),
                    AgentId = table.Column<Guid>(type: "uuid", nullable: false),
                    InstanceKey = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    Engine = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    Source = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    DisplayName = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    InstanceName = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                    Host = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: false),
                    Port = table.Column<int>(type: "integer", nullable: true),
                    Version = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                    Edition = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                    HostPlatform = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                    ContainerName = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    ContainerImage = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    Presence = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    MissCount = table.Column<int>(type: "integer", nullable: false),
                    Reachability = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    Access = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    AccessReasonCode = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    FullBackup = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    FullBackupReasonCode = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    CredentialReference = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                    IntegratedSecurity = table.Column<bool>(type: "boolean", nullable: false),
                    Encrypt = table.Column<bool>(type: "boolean", nullable: false),
                    TrustServerCertificate = table.Column<bool>(type: "boolean", nullable: false),
                    FirstSeenAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    LastSeenAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    LastChangeKind = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: true),
                    LastChangeAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    DatabasesCheckedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_discovery_instances", x => x.Id);
                    table.UniqueConstraint("AK_discovery_instances_Id_TenantId", x => new { x.Id, x.TenantId });
                    table.CheckConstraint("ck_discovery_instances_binding", "NOT (\"IntegratedSecurity\" AND \"CredentialReference\" IS NOT NULL)");
                    table.CheckConstraint("ck_discovery_instances_engine", "\"Engine\" IN ('mssql','postgres')");
                    table.CheckConstraint("ck_discovery_instances_id", "\"Id\" <> '00000000-0000-0000-0000-000000000000'::uuid");
                    table.CheckConstraint("ck_discovery_instances_port", "\"Port\" IS NULL OR \"Port\" BETWEEN 1 AND 65535");
                    table.CheckConstraint("ck_discovery_instances_presence", "\"Presence\" IN ('Present','Missing')");
                    table.CheckConstraint("ck_discovery_instances_tenant", "\"TenantId\" <> '00000000-0000-0000-0000-000000000000'::uuid");
                    table.ForeignKey(
                        name: "FK_discovery_instances_agents_AgentId_TenantId",
                        columns: x => new { x.AgentId, x.TenantId },
                        principalTable: "agents",
                        principalColumns: new[] { "Id", "TenantId" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_discovery_instances_tenants_TenantId",
                        column: x => x.TenantId,
                        principalTable: "tenants",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "discovery_databases",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    TenantId = table.Column<Guid>(type: "uuid", nullable: false),
                    InstanceId = table.Column<Guid>(type: "uuid", nullable: false),
                    Name = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    IsSystem = table.Column<bool>(type: "boolean", nullable: false),
                    State = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    FullBackup = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    FullBackupReasonCode = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    Presence = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    FirstSeenAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    LastSeenAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_discovery_databases", x => x.Id);
                    table.UniqueConstraint("AK_discovery_databases_Id_TenantId", x => new { x.Id, x.TenantId });
                    table.CheckConstraint("ck_discovery_databases_id", "\"Id\" <> '00000000-0000-0000-0000-000000000000'::uuid");
                    table.CheckConstraint("ck_discovery_databases_presence", "\"Presence\" IN ('Present','Missing')");
                    table.CheckConstraint("ck_discovery_databases_tenant", "\"TenantId\" <> '00000000-0000-0000-0000-000000000000'::uuid");
                    table.ForeignKey(
                        name: "FK_discovery_databases_discovery_instances_InstanceId_TenantId",
                        columns: x => new { x.InstanceId, x.TenantId },
                        principalTable: "discovery_instances",
                        principalColumns: new[] { "Id", "TenantId" },
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_discovery_databases_tenants_TenantId",
                        column: x => x.TenantId,
                        principalTable: "tenants",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_discovery_agent_states_AgentId_TenantId",
                table: "discovery_agent_states",
                columns: new[] { "AgentId", "TenantId" });

            migrationBuilder.CreateIndex(
                name: "IX_discovery_agent_states_TenantId",
                table: "discovery_agent_states",
                column: "TenantId");

            migrationBuilder.CreateIndex(
                name: "IX_discovery_databases_InstanceId_TenantId",
                table: "discovery_databases",
                columns: new[] { "InstanceId", "TenantId" });

            migrationBuilder.CreateIndex(
                name: "IX_discovery_databases_TenantId",
                table: "discovery_databases",
                column: "TenantId");

            migrationBuilder.CreateIndex(
                name: "ux_discovery_databases_instance_name",
                table: "discovery_databases",
                columns: new[] { "InstanceId", "Name" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_discovery_instances_AgentId_TenantId",
                table: "discovery_instances",
                columns: new[] { "AgentId", "TenantId" });

            migrationBuilder.CreateIndex(
                name: "IX_discovery_instances_TenantId",
                table: "discovery_instances",
                column: "TenantId");

            migrationBuilder.CreateIndex(
                name: "ux_discovery_instances_agent_key",
                table: "discovery_instances",
                columns: new[] { "AgentId", "InstanceKey" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "discovery_agent_states");

            migrationBuilder.DropTable(
                name: "discovery_databases");

            migrationBuilder.DropTable(
                name: "discovery_instances");
        }
    }
}
