using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BackupPlatform.Infrastructure.Persistence.Migrations;

public partial class GoogleDriveStorageProvider : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateTable(
            name: "google_drive_oauth_states",
            columns: table => new
            {
                State = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                TenantId = table.Column<Guid>(type: "uuid", nullable: false),
                ConnectionId = table.Column<Guid>(type: "uuid", nullable: false),
                UserId = table.Column<Guid>(type: "uuid", nullable: false),
                CodeVerifier = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                RedirectUri = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: false),
                CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                ExpiresAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                Used = table.Column<bool>(type: "boolean", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_google_drive_oauth_states", x => x.State);
            });

        migrationBuilder.CreateTable(
            name: "google_drive_file_metadata",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "uuid", nullable: false),
                TenantId = table.Column<Guid>(type: "uuid", nullable: false),
                ConnectionId = table.Column<Guid>(type: "uuid", nullable: false),
                StorageKey = table.Column<string>(type: "character varying(1024)", maxLength: 1024, nullable: false),
                FileId = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                ParentFolderId = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                FileName = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                SizeBytes = table.Column<long>(type: "bigint", nullable: false),
                Checksum = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                Md5Checksum = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_google_drive_file_metadata", x => x.Id);
            });

        migrationBuilder.CreateIndex(
            name: "ix_google_drive_oauth_states_connection",
            table: "google_drive_oauth_states",
            columns: new[] { "TenantId", "ConnectionId" });

        migrationBuilder.CreateIndex(
            name: "ix_google_drive_file_metadata_key",
            table: "google_drive_file_metadata",
            columns: new[] { "TenantId", "ConnectionId", "StorageKey" },
            unique: true);

        migrationBuilder.CreateIndex(
            name: "ix_google_drive_file_metadata_file_id",
            table: "google_drive_file_metadata",
            columns: new[] { "ConnectionId", "FileId" });
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable(name: "google_drive_file_metadata");
        migrationBuilder.DropTable(name: "google_drive_oauth_states");
    }
}
