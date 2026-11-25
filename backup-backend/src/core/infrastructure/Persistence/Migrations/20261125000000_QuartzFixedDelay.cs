using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace BackupPlatform.Infrastructure.Persistence.Migrations;

[DbContext(typeof(PlatformDbContext))]
[Migration("20261125000000_QuartzFixedDelay")]
public sealed class QuartzFixedDelay : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder) =>
        migrationBuilder.Sql("ALTER TABLE quartz.qrtz_triggers ADD COLUMN IF NOT EXISTS fixed_delay boolean NULL");

    protected override void Down(MigrationBuilder migrationBuilder) =>
        migrationBuilder.Sql("""
            DO $$
            BEGIN
                IF EXISTS (SELECT 1 FROM quartz.qrtz_triggers WHERE fixed_delay IS NOT NULL) THEN
                    RAISE EXCEPTION 'Cannot remove Quartz fixed_delay while it contains data';
                END IF;
                ALTER TABLE quartz.qrtz_triggers DROP COLUMN fixed_delay;
            END $$;
            """);
}
