using System;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BackupPlatform.Infrastructure.Persistence.Migrations;

/// <summary>Failed-login counters move from each API process's memory into the database, so every API instance enforces one shared limit.</summary>
[DbContext(typeof(PlatformDbContext))]
[Migration("20261025110000_LoginThrottleCounters")]
public partial class LoginThrottleCounters : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateTable("login_throttle_entries",
            columns: table => new
            {
                Identifier = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                Failures = table.Column<int>(type: "integer", nullable: false),
                WindowStart = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
            },
            constraints: table => table.PrimaryKey("PK_login_throttle_entries", entry => entry.Identifier));
        migrationBuilder.CreateIndex("IX_login_throttle_entries_WindowStart", "login_throttle_entries", "WindowStart");
    }

    protected override void Down(MigrationBuilder migrationBuilder) => migrationBuilder.DropTable("login_throttle_entries");
}
