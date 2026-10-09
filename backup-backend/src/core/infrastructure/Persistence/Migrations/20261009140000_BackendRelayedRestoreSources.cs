using System;
using BackupPlatform.Infrastructure.Persistence;

using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BackupPlatform.Infrastructure.Persistence.Migrations;

public partial class BackendRelayedRestoreSources : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropCheckConstraint("ck_restore_operations_source", "restore_operations");
        migrationBuilder.AddCheckConstraint("ck_restore_operations_source", "restore_operations",
            "\"SourceAccess\" IN ('AgentLocal','CentralStorage','BackendRelayed')");
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        // A downgrade cannot reinterpret a relayed replica as local or mounted storage.
        // Existing relayed operations must be removed before restoring the older constraint.
        migrationBuilder.DropCheckConstraint("ck_restore_operations_source", "restore_operations");
        migrationBuilder.AddCheckConstraint("ck_restore_operations_source", "restore_operations",
            "\"SourceAccess\" IN ('AgentLocal','CentralStorage')");
    }
}
