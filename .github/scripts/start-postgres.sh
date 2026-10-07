#!/usr/bin/env bash
# Starts a disposable PostgreSQL container for the integration tests and exports the connection convention the
# tests read (BACKUP_PLATFORM_TEST_CONNECTION_STRING) through $GITHUB_ENV. The password is generated here, masked in the
# log and lives only as long as the container; the tests create and drop their own uniquely named databases.
set -euo pipefail

name="${POSTGRES_CONTAINER:-bp-ci-postgres}"
port="${POSTGRES_HOST_PORT:-55432}"
image="${POSTGRES_IMAGE:-postgres:18}"

password="$(openssl rand -hex 24)"
echo "::add-mask::${password}"

# fsync/full_page_writes off: the data is disposable, durability only slows the suite down.
POSTGRES_PASSWORD="${password}" docker run --detach --name "${name}" \
  --env POSTGRES_PASSWORD \
  --publish "127.0.0.1:${port}:5432" \
  "${image}" \
  -c max_connections=300 -c fsync=off -c synchronous_commit=off -c full_page_writes=off >/dev/null

for _ in $(seq 1 60); do
  # TCP, not the socket: the entrypoint's temporary bootstrap server listens on the socket only.
  if docker exec "${name}" pg_isready --host 127.0.0.1 --username postgres >/dev/null 2>&1; then
    ready=true
    break
  fi
  sleep 1
done
if [ "${ready:-false}" != true ]; then
  echo "::error title=PostgreSQL did not become ready::see the container log below"
  docker logs "${name}" || true
  exit 1
fi

echo "BACKUP_PLATFORM_TEST_CONNECTION_STRING=Host=127.0.0.1;Port=${port};Username=postgres;Password=${password}" >> "${GITHUB_ENV}"
echo "BACKUP_PLATFORM_TEST_POSTGRES_PASSWORD=${password}" >> "${GITHUB_ENV}"
echo "PostgreSQL ${image} is ready on 127.0.0.1:${port}"
