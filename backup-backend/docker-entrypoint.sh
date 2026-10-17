#!/bin/sh
set -e

# Wait for PostgreSQL and apply migrations
if [ -n "$BACKUP_PLATFORM_CONNECTION_STRING" ] && [ -f "/app/efbundle" ]; then
    echo "Waiting for database and applying EF Core migrations..."
    for i in $(seq 1 30); do
        if /app/efbundle; then
            echo "Database migrations applied successfully."
            break
        fi
        echo "Waiting for database to be ready ($i/30)..."
        sleep 2
    done
fi

# Optional admin user bootstrap
if [ -n "$BACKUP_PLATFORM_BOOTSTRAP_LOGIN" ] && [ -n "$BACKUP_PLATFORM_BOOTSTRAP_PASSWORD" ]; then
    echo "Ensuring initial platform admin user..."
    BACKUP_PLATFORM_BOOTSTRAP_DISPLAY_NAME="${BACKUP_PLATFORM_BOOTSTRAP_DISPLAY_NAME:-Administrator}" \
        dotnet BackupPlatform.Api.dll --bootstrap-user || echo "Bootstrap admin already initialized or skipped."
fi

echo "Starting BackupPlatform API..."
exec dotnet BackupPlatform.Api.dll "$@"
