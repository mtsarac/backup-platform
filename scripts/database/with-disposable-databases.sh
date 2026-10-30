#!/usr/bin/env bash
set +x
set -euo pipefail
# shellcheck source=scripts/lib/common.sh
source "$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")/../lib" && pwd)/common.sh"
restore=0
postgres_only=0
case ${1:-} in
    --restore) restore=1; shift ;;
    --postgres-only) postgres_only=1; shift ;;
esac
[[ ${1:-} == -- ]] || fail 'Usage: with-disposable-databases.sh [--restore | --postgres-only] -- COMMAND [ARG...]' 2
shift
(( $# > 0 )) || fail 'A command is required.' 2
require_command docker
require_command openssl
require_command python3
load_local_env
export POSTGRES_IMAGE="${POSTGRES_IMAGE:-postgres:18}"
export MSSQL_IMAGE="${MSSQL_IMAGE:-mcr.microsoft.com/mssql/server:2025-latest}"
# Do not silently pull multi-GB images on a slow local connection.
required_images=("$POSTGRES_IMAGE")
(( postgres_only )) || required_images+=("$MSSQL_IMAGE")
for image in "${required_images[@]}"; do
    docker image inspect "$image" >/dev/null 2>&1 || fail "Cached image required; explicitly pull it first: $image"
done
make_runtime_dir
work_dir=$RUNTIME_DIR
suffix=${work_dir##*/}
export POSTGRES_CONTAINER="bp-local-pg-${suffix}"
export MSSQL_CONTAINER="bp-local-sql-${suffix}"
export MSSQL_WORK_DIR="${work_dir}/mssql"
# Docker chooses and binds loopback ports atomically instead of probing and releasing them first.
export BACKUP_PLATFORM_TEST_POSTGRES_PASSWORD="$(openssl rand -hex 24)"
export POSTGRES_PASSWORD="$BACKUP_PLATFORM_TEST_POSTGRES_PASSWORD"
BP_CLEANUP_CONTAINERS+=("$POSTGRES_CONTAINER")
run_redacted docker run --detach --name "$POSTGRES_CONTAINER" \
    --env POSTGRES_PASSWORD --publish '127.0.0.1::5432' "$POSTGRES_IMAGE" \
    postgres -c fsync=off
unset POSTGRES_PASSWORD
ready=0
for (( attempt=0; attempt<120; attempt++ )); do
    [[ $(docker inspect --format '{{.State.Running}}' "$POSTGRES_CONTAINER") == true ]] || break
    if docker exec "$POSTGRES_CONTAINER" pg_isready -h 127.0.0.1 -U postgres >/dev/null 2>&1; then ready=1; break; fi
    sleep 1
done
(( ready )) || fail 'Disposable PostgreSQL did not become ready.'
POSTGRES_HOST_PORT=$(docker inspect --format '{{(index (index .NetworkSettings.Ports "5432/tcp") 0).HostPort}}' "$POSTGRES_CONTAINER")
export POSTGRES_HOST_PORT
export BACKUP_PLATFORM_TEST_CONNECTION_STRING="Host=127.0.0.1;Port=${POSTGRES_HOST_PORT};Username=postgres;Password=${BACKUP_PLATFORM_TEST_POSTGRES_PASSWORD}"

unset BACKUP_PLATFORM_TEST_MSSQL_INSTANCE BACKUP_PLATFORM_TEST_MSSQL_RESTORE_TARGET_PORT
if (( ! postgres_only )); then
    mkdir -p "${MSSQL_WORK_DIR}/staging" "${MSSQL_WORK_DIR}/data"
    export BACKUP_PLATFORM_TEST_MSSQL_HOST=127.0.0.1 BACKUP_PLATFORM_TEST_MSSQL_USER=sa
    export BACKUP_PLATFORM_TEST_MSSQL_PASSWORD="Aa1!$(openssl rand -hex 24)"
    export BACKUP_PLATFORM_TEST_MSSQL_TRUST_SERVER_CERTIFICATE=true
    export BACKUP_PLATFORM_TEST_MSSQL_STAGING_SERVER_DIR=/var/opt/mssql/staging
    export BACKUP_PLATFORM_TEST_MSSQL_STAGING_AGENT_DIR="${MSSQL_WORK_DIR}/staging"
    export MSSQL_SA_PASSWORD="$BACKUP_PLATFORM_TEST_MSSQL_PASSWORD"
    BP_CLEANUP_CONTAINERS+=("$MSSQL_CONTAINER")
    run_redacted docker run --detach --name "$MSSQL_CONTAINER" --user "$(id -u):10001" \
        --env HOME=/var/opt/mssql --env ACCEPT_EULA=Y --env MSSQL_PID=Developer --env MSSQL_SA_PASSWORD \
        --publish '127.0.0.1::1433' \
        --volume "${MSSQL_WORK_DIR}/staging:/var/opt/mssql/staging" \
        --volume "${MSSQL_WORK_DIR}/data:/var/opt/mssql/data" "$MSSQL_IMAGE"
    unset MSSQL_SA_PASSWORD
    ready=0
    for (( attempt=0; attempt<120; attempt++ )); do
        [[ $(docker inspect --format '{{.State.Running}}' "$MSSQL_CONTAINER") == true ]] || break
        logs=$(docker logs "$MSSQL_CONTAINER" 2>&1)
        if [[ $logs == *'SQL Server is now ready for client connections'* ]]; then ready=1; break; fi
        sleep 2
    done
    (( ready )) || fail 'Disposable SQL Server did not become ready.'
    MSSQL_HOST_PORT=$(docker inspect --format '{{(index (index .NetworkSettings.Ports "1433/tcp") 0).HostPort}}' "$MSSQL_CONTAINER")
    export MSSQL_HOST_PORT BACKUP_PLATFORM_TEST_MSSQL_PORT="$MSSQL_HOST_PORT"
fi
if (( restore )); then
    target="bp-local-sql-target-${suffix}"
    BP_CLEANUP_CONTAINERS+=("$target")
    mkdir -p "${work_dir}/target-data"
    export MSSQL_SA_PASSWORD="$BACKUP_PLATFORM_TEST_MSSQL_PASSWORD"
    run_redacted docker run --detach --name "$target" --user "$(id -u):10001" \
        --env HOME=/var/opt/mssql --env ACCEPT_EULA=Y --env MSSQL_PID=Developer --env MSSQL_SA_PASSWORD \
        --publish '127.0.0.1::1433' \
        --volume "${MSSQL_WORK_DIR}/staging:/var/opt/mssql/staging" \
        --volume "${work_dir}/target-data:/var/opt/mssql/data" "$MSSQL_IMAGE"
    unset MSSQL_SA_PASSWORD
    ready=0
    for (( attempt=0; attempt<120; attempt++ )); do
        [[ $(docker inspect --format '{{.State.Running}}' "$target") == true ]] || break
        logs=$(docker logs "$target" 2>&1)
        if [[ $logs == *'SQL Server is now ready for client connections'* ]]; then ready=1; break; fi
        sleep 2
    done
    (( ready )) || fail 'Restore target SQL Server did not become ready.'
    export BACKUP_PLATFORM_TEST_MSSQL_RESTORE_TARGET_PORT="$(docker inspect --format '{{(index (index .NetworkSettings.Ports "1433/tcp") 0).HostPort}}' "$target")"
fi
printf 'Disposable database environment configured.\n'
run_redacted "$@"
