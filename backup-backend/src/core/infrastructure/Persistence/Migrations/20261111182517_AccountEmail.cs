using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BackupPlatform.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AccountEmail : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "Email",
                table: "password_credentials",
                type: "character varying(254)",
                maxLength: 254,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "NormalizedEmail",
                table: "password_credentials",
                type: "character varying(254)",
                maxLength: 254,
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_password_credentials_NormalizedEmail",
                table: "password_credentials",
                column: "NormalizedEmail",
                unique: true);

            migrationBuilder.AddCheckConstraint(
                name: "ck_password_credentials_normalized_email",
                table: "password_credentials",
                sql: "(\"Email\" IS NULL AND \"NormalizedEmail\" IS NULL) OR (\"Email\" IS NOT NULL AND \"NormalizedEmail\" IS NOT NULL AND \"NormalizedEmail\" = upper(\"Email\" COLLATE \"C\"))");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_password_credentials_NormalizedEmail",
                table: "password_credentials");

            migrationBuilder.DropCheckConstraint(
                name: "ck_password_credentials_normalized_email",
                table: "password_credentials");

            migrationBuilder.DropColumn(
                name: "Email",
                table: "password_credentials");

            migrationBuilder.DropColumn(
                name: "NormalizedEmail",
                table: "password_credentials");
        }
    }
}
