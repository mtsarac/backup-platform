#!/usr/bin/env bash
# Starts a disposable SQL Server (Linux) container for the real MSSQL integration tests and exports the environment those
# tests read through $GITHUB_ENV. The sa password is generated here, masked in the log and lives only as long as the container.
#
# Staging: BACKUP and the agent must see the same .bak files. A host directory is bind-mounted into the container, and the
# container runs as the CALLER's uid with the image's `mssql` group (gid 10001, which owns /var/opt/mssql). Files SQL Server writes
# are therefore owned by the user that runs the tests: the agent side can read and delete them without chmod tricks or sudo, exactly the
# "SQL Server writes, agent reads and deletes" contract of a Mapped staging area.
set -euo pipefail

name="${MSSQL_CONTAINER:-bp-ci-mssql}"
port="${MSSQL_HOST_PORT:-14333}"
image="${MSSQL_IMAGE:-mcr.microsoft.com/mssql/server:2025-latest}"
root="${MSSQL_WORK_DIR:-${RUNNER_TEMP:-/tmp}/bp-ci-mssql}"
server_dir=/var/opt/mssql/staging

mkdir -p "${root}/staging" "${root}/data"
chmod 0770 "${root}/staging"

# Complexity rules: upper, lower, digit and symbol; no quote or semicolon, safe in connection strings and T-SQL literals.
password="Bp1-$(openssl rand -hex 20)"
echo "::add-mask::${password}"

MSSQL_SA_PASSWORD="${password}" docker run --detach --name "${name}" \
  --user "$(id -u):10001" --env HOME=/var/opt/mssql \
  --env ACCEPT_EULA=Y --env MSSQL_PID=Developer --env MSSQL_SA_PASSWORD \
  --publish "127.0.0.1:${port}:1433" \
  --volume "${root}/staging:${server_dir}" \
  --volume "${root}/data:/var/opt/mssql/data" \
  "${image}" >/dev/null

for _ in $(seq 1 120); do
  if [ "$(docker inspect --format '{{.State.Running}}' "${name}")" != true ]; then
    break
  fi
  if docker logs "${name}" 2>&1 | grep -q 'SQL Server is now ready for client connections'; then
    ready=true
    break
  fi
  sleep 2
done
if [ "${ready:-false}" != true ]; then
  echo "::error title=SQL Server did not become ready::see the container log below"
  docker logs "${name}" || true
  exit 1
fi

# Prove the contract before any test relies on it: the server can write the staging directory and this user can read and delete it.
docker exec "${name}" bash -c "echo staging-probe > ${server_dir}/.probe"
if [ "$(cat "${root}/staging/.probe")" != staging-probe ]; then
  echo "::error title=Staging mount broken::the agent side cannot read what the server side wrote"
  exit 1
fi
rm "${root}/staging/.probe"

{
  echo "BACKUP_PLATFORM_TEST_MSSQL_HOST=127.0.0.1"
  echo "BACKUP_PLATFORM_TEST_MSSQL_PORT=${port}"
  echo "BACKUP_PLATFORM_TEST_MSSQL_USER=sa"
  echo "BACKUP_PLATFORM_TEST_MSSQL_PASSWORD=${password}"
  echo "BACKUP_PLATFORM_TEST_MSSQL_TRUST_SERVER_CERTIFICATE=true"
  echo "BACKUP_PLATFORM_TEST_MSSQL_STAGING_SERVER_DIR=${server_dir}"
  echo "BACKUP_PLATFORM_TEST_MSSQL_STAGING_AGENT_DIR=${root}/staging"
} >> "${GITHUB_ENV}"
echo "SQL Server ${image} is ready on 127.0.0.1:${port}; staging ${server_dir} (server) = ${root}/staging (agent)"
