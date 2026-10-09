using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BackupPlatform.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class GoogleDriveStorageReliability : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "DriftDetectedAt",
                table: "google_drive_file_metadata",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "DriftKind",
                table: "google_drive_file_metadata",
                type: "character varying(32)",
                maxLength: 32,
                nullable: true);

            migrationBuilder.CreateTable(
                name: "google_drive_staging_reservations",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    LeaseToken = table.Column<Guid>(type: "uuid", nullable: false),
                    VolumeId = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    NodeId = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    SharedVolume = table.Column<bool>(type: "boolean", nullable: false),
                    DirectoryPath = table.Column<string>(type: "character varying(4096)", maxLength: 4096, nullable: false),
                    FileName = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    ExpectedBytes = table.Column<long>(type: "bigint", nullable: false),
                    WrittenBytes = table.Column<long>(type: "bigint", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    LeaseExpiresAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_google_drive_staging_reservations", x => x.Id);
                    table.CheckConstraint("ck_google_drive_staging_bytes", "\"ExpectedBytes\" > 0 AND \"WrittenBytes\" >= 0 AND \"WrittenBytes\" <= \"ExpectedBytes\"");
                });

            migrationBuilder.CreateIndex(
                name: "ix_google_drive_staging_cleanup",
                table: "google_drive_staging_reservations",
                columns: new[] { "VolumeId", "NodeId", "LeaseExpiresAt" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "google_drive_staging_reservations");

            migrationBuilder.DropColumn(
                name: "DriftDetectedAt",
                table: "google_drive_file_metadata");

            migrationBuilder.DropColumn(
                name: "DriftKind",
                table: "google_drive_file_metadata");
        }
    }
}
