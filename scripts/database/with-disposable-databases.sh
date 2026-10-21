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
export GITHUB_ENV="${work_dir}/ci-environment"
: > "$GITHUB_ENV"
# Ask the kernel for unused loopback ports. Docker binding still fails safely if another process wins a race.
read -r POSTGRES_HOST_PORT MSSQL_HOST_PORT restore_port < <(python3 -c '
import socket
sockets = [socket.socket() for _ in range(3)]
for sock in sockets: sock.bind(("127.0.0.1", 0))
print(*(sock.getsockname()[1] for sock in sockets))
')
export POSTGRES_HOST_PORT MSSQL_HOST_PORT
# Names are unique and registered before startup so partial startup is also cleaned.
BP_CLEANUP_CONTAINERS+=("$POSTGRES_CONTAINER" "$MSSQL_CONTAINER")
run_redacted bash "${REPO_ROOT}/.github/scripts/start-postgres.sh"
(( postgres_only )) || run_redacted bash "${REPO_ROOT}/.github/scripts/start-mssql.sh"
while IFS='=' read -r key value; do
    [[ $key == BACKUP_PLATFORM_TEST_* && $key =~ ^[A-Z_]+$ ]] || fail 'Unexpected CI environment key.'
    export "$key=$value"
done < "$GITHUB_ENV"
rm -- "$GITHUB_ENV"
unset BACKUP_PLATFORM_TEST_MSSQL_INSTANCE BACKUP_PLATFORM_TEST_MSSQL_RESTORE_TARGET_PORT
if (( restore )); then
    target="bp-local-sql-target-${suffix}"
    BP_CLEANUP_CONTAINERS+=("$target")
    mkdir -p "${work_dir}/target-data"
    export MSSQL_SA_PASSWORD="$BACKUP_PLATFORM_TEST_MSSQL_PASSWORD"
    run_redacted docker run --detach --name "$target" --user "$(id -u):10001" \
        --env HOME=/var/opt/mssql --env ACCEPT_EULA=Y --env MSSQL_PID=Developer --env MSSQL_SA_PASSWORD \
        --publish "127.0.0.1:${restore_port}:1433" \
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
    export BACKUP_PLATFORM_TEST_MSSQL_RESTORE_TARGET_PORT=$restore_port
fi
printf 'Disposable PostgreSQL/MSSQL environment and mapped staging configured.\n'
run_redacted "$@"
