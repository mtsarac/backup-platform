using Microsoft.EntityFrameworkCore.Migrations;

namespace BackupPlatform.Infrastructure.Persistence.Migrations;

public sealed partial class ScheduledTransferPause : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder) => migrationBuilder.Sql("""
        ALTER TABLE transfer_schedule_windows DROP CONSTRAINT ck_transfer_schedule_windows_rate;
        ALTER TABLE transfer_schedule_windows ADD CONSTRAINT ck_transfer_schedule_windows_rate
            CHECK ("BytesPerSecond" = 0 OR "BytesPerSecond" BETWEEN 16000 AND 100000000000);
        """);

    protected override void Down(MigrationBuilder migrationBuilder) => migrationBuilder.Sql("""
        ALTER TABLE transfer_schedule_windows DROP CONSTRAINT ck_transfer_schedule_windows_rate;
        ALTER TABLE transfer_schedule_windows ADD CONSTRAINT ck_transfer_schedule_windows_rate
            CHECK ("BytesPerSecond" BETWEEN 16000 AND 100000000000);
        """);
}
