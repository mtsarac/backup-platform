using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BackupPlatform.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class TwoRoleModelAndCentralAuthorization : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "Role",
                table: "users",
                type: "integer",
                nullable: false,
                defaultValue: 2);

            migrationBuilder.Sql("UPDATE users SET \"Role\" = 1 WHERE \"Id\" IN (SELECT \"UserId\" FROM platform_admins);");

            migrationBuilder.DropTable(
                name: "platform_admins");

            migrationBuilder.DropCheckConstraint(
                name: "ck_tenant_memberships_role",
                table: "tenant_memberships");

            migrationBuilder.DropColumn(
                name: "Role",
                table: "tenant_memberships");

            migrationBuilder.AddCheckConstraint(
                name: "ck_users_role",
                table: "users",
                sql: "\"Role\" IN (1, 2)");

            migrationBuilder.Sql("""
                CREATE FUNCTION public.tenant_membership_user_guard() RETURNS trigger
                LANGUAGE plpgsql AS $$
                BEGIN
                    IF EXISTS (SELECT 1 FROM users WHERE "Id" = NEW."UserId" AND "Role" = 1) THEN
                        RAISE EXCEPTION 'A PlatformAdmin cannot have tenant memberships.' USING ERRCODE = '23514';
                    END IF;
                    RETURN NEW;
                END $$;

                CREATE TRIGGER trg_tenant_membership_user_guard
                BEFORE INSERT OR UPDATE ON tenant_memberships
                FOR EACH ROW EXECUTE FUNCTION public.tenant_membership_user_guard();

                CREATE FUNCTION public.user_role_membership_guard() RETURNS trigger
                LANGUAGE plpgsql AS $$
                BEGIN
                    IF NEW."Role" = 1 AND EXISTS (SELECT 1 FROM tenant_memberships WHERE "UserId" = NEW."Id") THEN
                        RAISE EXCEPTION 'A PlatformAdmin cannot have tenant memberships.' USING ERRCODE = '23514';
                    END IF;
                    RETURN NEW;
                END $$;

                CREATE TRIGGER trg_user_role_membership_guard
                BEFORE UPDATE OF "Role" ON users
                FOR EACH ROW EXECUTE FUNCTION public.user_role_membership_guard();
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                DROP TRIGGER IF EXISTS trg_tenant_membership_user_guard ON tenant_memberships;
                DROP FUNCTION IF EXISTS public.tenant_membership_user_guard();
                DROP TRIGGER IF EXISTS trg_user_role_membership_guard ON users;
                DROP FUNCTION IF EXISTS public.user_role_membership_guard();
                """);

            migrationBuilder.DropCheckConstraint(
                name: "ck_users_role",
                table: "users");

            migrationBuilder.DropColumn(
                name: "Role",
                table: "users");
            migrationBuilder.AddColumn<int>(
                name: "Role",
                table: "tenant_memberships",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.CreateTable(
                name: "platform_admins",
                columns: table => new
                {
                    UserId = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_platform_admins", x => x.UserId);
                    table.CheckConstraint("ck_platform_admins_user_id_nonempty", "\"UserId\" <> '00000000-0000-0000-0000-000000000000'::uuid");
                    table.ForeignKey(
                        name: "FK_platform_admins_users_UserId",
                        column: x => x.UserId,
                        principalTable: "users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.AddCheckConstraint(
                name: "ck_tenant_memberships_role",
                table: "tenant_memberships",
                sql: "\"Role\" BETWEEN 1 AND 4");
        }
    }
}
