using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BackupPlatform.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AgentEnrollmentAndCredentials : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "HostName",
                table: "agents",
                type: "character varying(255)",
                maxLength: 255,
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "InstallationId",
                table: "agents",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddUniqueConstraint(
                name: "ak_agents_id_tenant_id",
                table: "agents",
                columns: new[] { "Id", "TenantId" });

            migrationBuilder.CreateTable(
                name: "agent_credentials",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    AgentId = table.Column<Guid>(type: "uuid", nullable: false),
                    TenantId = table.Column<Guid>(type: "uuid", nullable: false),
                    Generation = table.Column<int>(type: "integer", nullable: false),
                    SecretHash = table.Column<byte[]>(type: "bytea", nullable: false),
                    Status = table.Column<int>(type: "integer", nullable: false),
                    IssuedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    ExpiresAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    ActivatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    RevokedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    RevocationReason = table.Column<int>(type: "integer", nullable: true),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_agent_credentials", x => x.Id);
                    table.CheckConstraint("ck_agent_credentials_generation", "\"Generation\" >= 1");
                    table.CheckConstraint("ck_agent_credentials_hash", "octet_length(\"SecretHash\") = 32");
                    table.CheckConstraint("ck_agent_credentials_id", "\"Id\" <> '00000000-0000-0000-0000-000000000000'::uuid");
                    table.CheckConstraint("ck_agent_credentials_state", "(\"Status\" = 1 AND \"ExpiresAt\" IS NOT NULL AND \"ActivatedAt\" IS NULL AND \"RevokedAt\" IS NULL AND \"RevocationReason\" IS NULL)\nOR (\"Status\" = 2 AND \"ExpiresAt\" IS NULL AND \"ActivatedAt\" IS NOT NULL AND \"RevokedAt\" IS NULL AND \"RevocationReason\" IS NULL)\nOR (\"Status\" = 3 AND \"RevokedAt\" IS NOT NULL AND \"RevocationReason\" BETWEEN 1 AND 3)");
                    table.ForeignKey(
                        name: "FK_agent_credentials_agents_AgentId_TenantId",
                        columns: x => new { x.AgentId, x.TenantId },
                        principalTable: "agents",
                        principalColumns: new[] { "Id", "TenantId" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "agent_enrollment_tokens",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    TenantId = table.Column<Guid>(type: "uuid", nullable: false),
                    SecretHash = table.Column<byte[]>(type: "bytea", nullable: false),
                    CreatedBy = table.Column<Guid>(type: "uuid", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    ExpiresAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    ConsumedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    ConsumedByAgentId = table.Column<Guid>(type: "uuid", nullable: true),
                    RevokedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    RevokedBy = table.Column<Guid>(type: "uuid", nullable: true),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_agent_enrollment_tokens", x => x.Id);
                    table.CheckConstraint("ck_agent_enrollment_tokens_consumption", "(\"ConsumedAt\" IS NULL) = (\"ConsumedByAgentId\" IS NULL)");
                    table.CheckConstraint("ck_agent_enrollment_tokens_expiry", "\"ExpiresAt\" > \"CreatedAt\"");
                    table.CheckConstraint("ck_agent_enrollment_tokens_hash", "octet_length(\"SecretHash\") = 32");
                    table.CheckConstraint("ck_agent_enrollment_tokens_id", "\"Id\" <> '00000000-0000-0000-0000-000000000000'::uuid");
                    table.CheckConstraint("ck_agent_enrollment_tokens_revocation", "(\"RevokedAt\" IS NULL) = (\"RevokedBy\" IS NULL)");
                    table.CheckConstraint("ck_agent_enrollment_tokens_tenant_id", "\"TenantId\" <> '00000000-0000-0000-0000-000000000000'::uuid");
                    table.CheckConstraint("ck_agent_enrollment_tokens_terminal", "\"ConsumedAt\" IS NULL OR \"RevokedAt\" IS NULL");
                    table.ForeignKey(
                        name: "FK_agent_enrollment_tokens_agents_ConsumedByAgentId_TenantId",
                        columns: x => new { x.ConsumedByAgentId, x.TenantId },
                        principalTable: "agents",
                        principalColumns: new[] { "Id", "TenantId" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_agent_enrollment_tokens_tenants_TenantId",
                        column: x => x.TenantId,
                        principalTable: "tenants",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_agent_enrollment_tokens_users_CreatedBy",
                        column: x => x.CreatedBy,
                        principalTable: "users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_agent_enrollment_tokens_users_RevokedBy",
                        column: x => x.RevokedBy,
                        principalTable: "users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "tenant_agent_enrollment_policies",
                columns: table => new
                {
                    TenantId = table.Column<Guid>(type: "uuid", nullable: false),
                    IsEnabled = table.Column<bool>(type: "boolean", nullable: false),
                    ChangedBy = table.Column<Guid>(type: "uuid", nullable: false),
                    ChangedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_tenant_agent_enrollment_policies", x => x.TenantId);
                    table.CheckConstraint("ck_tenant_agent_enrollment_policies_tenant_id", "\"TenantId\" <> '00000000-0000-0000-0000-000000000000'::uuid");
                    table.ForeignKey(
                        name: "FK_tenant_agent_enrollment_policies_tenants_TenantId",
                        column: x => x.TenantId,
                        principalTable: "tenants",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_tenant_agent_enrollment_policies_users_ChangedBy",
                        column: x => x.ChangedBy,
                        principalTable: "users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "ux_agents_tenant_installation_live",
                table: "agents",
                columns: new[] { "TenantId", "InstallationId" },
                unique: true,
                filter: "\"InstallationId\" IS NOT NULL AND \"Status\" <> 4");

            migrationBuilder.AddCheckConstraint(
                name: "ck_agents_host_name",
                table: "agents",
                sql: "\"HostName\" IS NULL OR \"HostName\" ~ '[^[:space:]]'");

            migrationBuilder.AddCheckConstraint(
                name: "ck_agents_installation_id",
                table: "agents",
                sql: "\"InstallationId\" IS NULL OR \"InstallationId\" <> '00000000-0000-0000-0000-000000000000'::uuid");

            migrationBuilder.CreateIndex(
                name: "IX_agent_credentials_AgentId_TenantId",
                table: "agent_credentials",
                columns: new[] { "AgentId", "TenantId" });

            migrationBuilder.CreateIndex(
                name: "ux_agent_credentials_one_active",
                table: "agent_credentials",
                column: "AgentId",
                unique: true,
                filter: "\"Status\" = 2");

            migrationBuilder.CreateIndex(
                name: "ux_agent_credentials_one_pending",
                table: "agent_credentials",
                column: "AgentId",
                unique: true,
                filter: "\"Status\" = 1");

            migrationBuilder.CreateIndex(
                name: "IX_agent_enrollment_tokens_ConsumedByAgentId_TenantId",
                table: "agent_enrollment_tokens",
                columns: new[] { "ConsumedByAgentId", "TenantId" });

            migrationBuilder.CreateIndex(
                name: "IX_agent_enrollment_tokens_CreatedBy",
                table: "agent_enrollment_tokens",
                column: "CreatedBy");

            migrationBuilder.CreateIndex(
                name: "IX_agent_enrollment_tokens_RevokedBy",
                table: "agent_enrollment_tokens",
                column: "RevokedBy");

            migrationBuilder.CreateIndex(
                name: "IX_agent_enrollment_tokens_TenantId",
                table: "agent_enrollment_tokens",
                column: "TenantId");

            migrationBuilder.CreateIndex(
                name: "IX_tenant_agent_enrollment_policies_ChangedBy",
                table: "tenant_agent_enrollment_policies",
                column: "ChangedBy");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "agent_credentials");

            migrationBuilder.DropTable(
                name: "agent_enrollment_tokens");

            migrationBuilder.DropTable(
                name: "tenant_agent_enrollment_policies");

            migrationBuilder.DropUniqueConstraint(
                name: "ak_agents_id_tenant_id",
                table: "agents");

            migrationBuilder.DropIndex(
                name: "ux_agents_tenant_installation_live",
                table: "agents");

            migrationBuilder.DropCheckConstraint(
                name: "ck_agents_host_name",
                table: "agents");

            migrationBuilder.DropCheckConstraint(
                name: "ck_agents_installation_id",
                table: "agents");

            migrationBuilder.DropColumn(
                name: "HostName",
                table: "agents");

            migrationBuilder.DropColumn(
                name: "InstallationId",
                table: "agents");
        }
    }
}
