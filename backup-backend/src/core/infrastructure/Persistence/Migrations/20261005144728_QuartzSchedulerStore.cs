using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BackupPlatform.Infrastructure.Persistence.Migrations
{
    /// <summary>
    /// Provisions the Quartz.NET job store in its own <c>quartz</c> PostgreSQL schema. It changes no domain table and is not part of the EF model:
    /// the DDL is Quartz's official script, shipped verbatim (plus IF NOT EXISTS) as an embedded resource, so provisioning is explicit,
    /// versioned and repeatable like every other schema change. The scheduler only verifies that the schema exists; it never creates it.
    /// </summary>
    public partial class QuartzSchedulerStore : Migration
    {
        private const string Script = "BackupPlatform.Infrastructure.Scheduling.Schema.quartz-4.3.0-postgres.sql";

        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            using var stream = typeof(QuartzSchedulerStore).Assembly.GetManifestResourceStream(Script)
                ?? throw new InvalidOperationException("The embedded Quartz schema script is missing.");
            using var reader = new StreamReader(stream);
            migrationBuilder.Sql(reader.ReadToEnd());
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder) => migrationBuilder.Sql("DROP SCHEMA IF EXISTS quartz CASCADE;");
    }
}
