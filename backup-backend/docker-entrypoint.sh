#!/bin/sh
set -e

EFBUNDLE="${EFBUNDLE:-/app/efbundle}"

# Wait for PostgreSQL and apply migrations; never start the API against a database that was not migrated.
if [ -n "$BACKUP_PLATFORM_CONNECTION_STRING" ] && [ -f "$EFBUNDLE" ]; then
    echo "Waiting for database and applying EF Core migrations..."
    migrated=
    for i in $(seq 1 30); do
        if "$EFBUNDLE"; then
            echo "Database migrations applied successfully."
            migrated=1
            break
        fi
        echo "Waiting for database to be ready ($i/30)..."
        sleep 2
    done
    if [ -z "$migrated" ]; then
        echo "Database migrations did not succeed after 30 attempts; refusing to start the API." >&2
        exit 1
    fi
fi


echo "Starting BackupPlatform API..."
exec dotnet BackupPlatform.Api.dll "$@"
