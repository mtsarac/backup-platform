using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BackupPlatform.Infrastructure.Persistence.Migrations
{
    /// <summary>
    /// Job and run configuration snapshots keep their destinations only in <c>Destinations</c>. Rows that predate the list (or hold it empty) name their one
    /// destination in the single-destination keys: it becomes the primary entry with the same provider, connection (null stays null: the tenant default) and
    /// execution location, so no destination identity changes. The single-destination keys are then dropped from every row.
    /// </summary>
    public partial class CanonicalBackupDestinations : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                DO $$
                DECLARE snapshot_table text;
                BEGIN
                    FOREACH snapshot_table IN ARRAY ARRAY['backup_jobs', 'backup_runs'] LOOP
                        EXECUTE format($sql$
                            UPDATE %I SET "Configuration" = (
                                CASE WHEN jsonb_array_length(coalesce("Configuration"->'Destinations', '[]'::jsonb)) = 0
                                THEN jsonb_set("Configuration", '{Destinations}', jsonb_build_array(jsonb_build_object(
                                    'IsPrimary', true,
                                    'StorageProvider', "Configuration"->'StorageProvider',
                                    'StorageConnectionId', coalesce("Configuration"->'StorageConnectionId', 'null'::jsonb),
                                    'StorageExecutionLocation', coalesce("Configuration"->'StorageExecutionLocation',
                                        CASE WHEN "Configuration"->>'StorageProvider' = 'local' THEN '2'::jsonb ELSE '1'::jsonb END))), true)
                                ELSE "Configuration" END
                            ) - 'StorageProvider' - 'StorageConnectionId' - 'StorageExecutionLocation'
                            $sql$, snapshot_table);
                    END LOOP;
                END $$;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                DO $$
                DECLARE snapshot_table text;
                BEGIN
                    FOREACH snapshot_table IN ARRAY ARRAY['backup_jobs', 'backup_runs'] LOOP
                        EXECUTE format($sql$
                            UPDATE %I SET "Configuration" = "Configuration" || coalesce((
                                SELECT jsonb_build_object(
                                    'StorageProvider', destination->'StorageProvider',
                                    'StorageConnectionId', destination->'StorageConnectionId',
                                    'StorageExecutionLocation', destination->'StorageExecutionLocation')
                                FROM jsonb_array_elements("Configuration"->'Destinations') destination
                                WHERE (destination->>'IsPrimary')::boolean
                                LIMIT 1), '{}'::jsonb)
                            $sql$, snapshot_table);
                    END LOOP;
                END $$;
                """);
        }
    }
}
