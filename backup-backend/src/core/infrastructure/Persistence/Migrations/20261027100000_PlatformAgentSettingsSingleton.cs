using System;
using BackupPlatform.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BackupPlatform.Infrastructure.Persistence.Migrations;

/// <summary>
/// Platform-wide agent installer and enrollment token lifetime settings, managed once by a platform administrator.
/// </summary>
[DbContext(typeof(PlatformDbContext))]
[Migration("20261027100000_PlatformAgentSettingsSingleton")]
public sealed class PlatformAgentSettingsSingleton : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateTable(
            name: "platform_agent_settings",
            columns: table => new
            {
                Id = table.Column<int>(type: "integer", nullable: false),
                ServerUrl = table.Column<string>(type: "character varying(2048)", maxLength: 2048, nullable: true),
                EnrollmentTokenLifetimeMinutes = table.Column<int>(type: "integer", nullable: false),
                UpdatedBy = table.Column<Guid>(type: "uuid", nullable: false),
                UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_platform_agent_settings", x => x.Id);
                table.CheckConstraint("ck_platform_agent_settings_singleton", "\"Id\" = 1");
            });
    }

    protected override void Down(MigrationBuilder migrationBuilder) =>
        migrationBuilder.DropTable("platform_agent_settings");
}
