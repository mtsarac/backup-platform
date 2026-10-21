using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BackupPlatform.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class BandwidthProfilesAndPacing : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "bandwidth_profile_assignments",
                columns: table => new
                {
                    TenantId = table.Column<Guid>(type: "uuid", nullable: false),
                    Scope = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    ScopeId = table.Column<Guid>(type: "uuid", nullable: false),
                    ProfileId = table.Column<Guid>(type: "uuid", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_bandwidth_profile_assignments", x => new { x.TenantId, x.Scope, x.ScopeId });
                });

            migrationBuilder.CreateTable(
                name: "bandwidth_profiles",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    TenantId = table.Column<Guid>(type: "uuid", nullable: false),
                    Name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    TimeZoneId = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_bandwidth_profiles", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "transfer_schedule_windows",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    ProfileId = table.Column<Guid>(type: "uuid", nullable: false),
                    DayOfWeek = table.Column<int>(type: "integer", nullable: false),
                    StartTime = table.Column<TimeOnly>(type: "time without time zone", nullable: false),
                    EndTime = table.Column<TimeOnly>(type: "time without time zone", nullable: false),
                    BytesPerSecond = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_transfer_schedule_windows", x => x.Id);
                    table.CheckConstraint("ck_transfer_schedule_windows_rate", "\"BytesPerSecond\" BETWEEN 16000 AND 100000000000");
                    table.ForeignKey(
                        name: "FK_transfer_schedule_windows_bandwidth_profiles_ProfileId",
                        column: x => x.ProfileId,
                        principalTable: "bandwidth_profiles",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_bandwidth_profile_assignments_ProfileId",
                table: "bandwidth_profile_assignments",
                column: "ProfileId");

            migrationBuilder.CreateIndex(
                name: "IX_bandwidth_profiles_TenantId_Name",
                table: "bandwidth_profiles",
                columns: new[] { "TenantId", "Name" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_transfer_schedule_windows_ProfileId",
                table: "transfer_schedule_windows",
                column: "ProfileId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "bandwidth_profile_assignments");

            migrationBuilder.DropTable(
                name: "transfer_schedule_windows");

            migrationBuilder.DropTable(
                name: "bandwidth_profiles");
        }
    }
}
