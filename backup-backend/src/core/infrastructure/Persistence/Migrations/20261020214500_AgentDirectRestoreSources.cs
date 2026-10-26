using System;
using BackupPlatform.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BackupPlatform.Infrastructure.Persistence.Migrations;

[DbContext(typeof(PlatformDbContext))]
[Migration("20261020214500_AgentDirectRestoreSources")]
public partial class AgentDirectRestoreSources : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropCheckConstraint("ck_restore_operations_source", "restore_operations");
        migrationBuilder.AddCheckConstraint("ck_restore_operations_source", "restore_operations",
            "\"SourceAccess\" IN ('AgentLocal','CentralStorage','BackendRelayed','AgentDirect')");
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropCheckConstraint("ck_restore_operations_source", "restore_operations");
        migrationBuilder.AddCheckConstraint("ck_restore_operations_source", "restore_operations",
            "\"SourceAccess\" IN ('AgentLocal','CentralStorage','BackendRelayed')");
    }
}
