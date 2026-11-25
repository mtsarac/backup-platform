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
        throw new NotSupportedException("Rolling back the Quartz fixed-delay column could discard scheduler data.");
}
