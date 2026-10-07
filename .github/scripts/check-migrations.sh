#!/usr/bin/env bash
# Generate the same idempotent schema script shipped in the backend archive and apply it twice to a disposable database.
# Run from backup-backend after the Release build and start-postgres.sh.
set -euo pipefail

container="${POSTGRES_CONTAINER:-bp-ci-postgres}"
database="bp_ci_migrations"
work="$(mktemp -d)"
cleanup() {
  docker exec "${container}" dropdb --username postgres --if-exists "${database}"
  rm -rf "${work}"
}
trap cleanup EXIT

dotnet tool restore
BACKUP_PLATFORM_CONNECTION_STRING='Host=unused;Database=unused' \
  dotnet ef migrations script --idempotent --configuration Release --no-build \
  --project src/core/infrastructure --output "${work}/migrations.sql"
docker exec "${container}" createdb --username postgres "${database}"

history() {
  docker exec "${container}" psql --username postgres --dbname "${database}" --tuples-only --no-align \
    --command 'SELECT "MigrationId", "ProductVersion" FROM "__EFMigrationsHistory" ORDER BY "MigrationId";'
}

for pass in 1 2; do
  docker exec --interactive "${container}" psql --username postgres --dbname "${database}" \
    --set ON_ERROR_STOP=1 --file - < "${work}/migrations.sql" > "${work}/apply-${pass}.log"
  history > "${work}/history-${pass}"
  echo "Migration script application ${pass} completed."
done

diff --unified "${work}/history-1" "${work}/history-2"
echo 'Idempotent migration script applied twice; migration history is unchanged.'
