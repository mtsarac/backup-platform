using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace BackupPlatform.Infrastructure.Persistence.Migrations;

[DbContext(typeof(PlatformDbContext))]
[Migration("20261124000000_AgentLogs")]
public sealed class AgentLogs : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder) => migrationBuilder.Sql("""
        CREATE TABLE agent_logs (
            "Id" uuid NOT NULL,
            "TenantId" uuid NOT NULL,
            "AgentId" uuid NOT NULL,
            "Timestamp" timestamptz NOT NULL,
            "ReceivedAt" timestamptz NOT NULL DEFAULT now(),
            "Code" varchar(40) NOT NULL,
            "Message" varchar(100) NOT NULL,
            "Level" varchar(20) NOT NULL,
            "JobId" uuid NULL,
            "RunId" uuid NULL,
            PRIMARY KEY ("TenantId", "AgentId", "Id")
        );
        CREATE INDEX "IX_agent_logs_tenant_time" ON agent_logs ("TenantId", "Timestamp" DESC);
        CREATE INDEX "IX_agent_logs_received" ON agent_logs ("ReceivedAt");
        """);

    protected override void Down(MigrationBuilder migrationBuilder) => migrationBuilder.Sql("DROP TABLE agent_logs");
}
