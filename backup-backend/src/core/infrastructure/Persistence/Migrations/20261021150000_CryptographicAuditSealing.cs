using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace BackupPlatform.Infrastructure.Persistence.Migrations;

[DbContext(typeof(PlatformDbContext))]
[Migration("20261021150000_CryptographicAuditSealing")]
public sealed class CryptographicAuditSealing : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder) => migrationBuilder.Sql("""
        ALTER TABLE audit_events ADD COLUMN "SealKeyId" character varying(128) NULL;
        ALTER TABLE audit_events ADD COLUMN "SealSignature" character varying(256) NULL;
        """);

    protected override void Down(MigrationBuilder migrationBuilder) => migrationBuilder.Sql("""
        ALTER TABLE audit_events DROP COLUMN "SealKeyId", DROP COLUMN "SealSignature";
        """);
}
