using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace BackupPlatform.Infrastructure.Persistence.Migrations;

[DbContext(typeof(PlatformDbContext))]
[Migration("20261124010000_TenantNameUnique")]
public sealed class TenantNameUnique : Migration
{
    // Earlier duplicates keep the oldest-looking row (lowest Id) as is; the others get their own id prefix appended so the new name cannot collide.
    protected override void Up(MigrationBuilder migrationBuilder) => migrationBuilder.Sql("""
        UPDATE tenants t SET "Name" = t."Name" || ' (' || left(t."Id"::text, 8) || ')'
        FROM (SELECT "Id", row_number() OVER (PARTITION BY lower("Name") ORDER BY "Id") AS n FROM tenants) d
        WHERE d."Id" = t."Id" AND d.n > 1;
        CREATE UNIQUE INDEX ux_tenants_name ON tenants (lower("Name"));
        """);

    protected override void Down(MigrationBuilder migrationBuilder) => migrationBuilder.Sql("DROP INDEX ux_tenants_name");
}
