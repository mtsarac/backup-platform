#!/usr/bin/env bash
# M7a smoke: disposable MSSQL data -> REAL verified Central backup (actual Agent) -> storage connection API checks -> the connection
# migration is rolled back and re-applied under the real replica (the upgrade path) -> HTTP download and real restore still work ->
# a disabled connection blocks new runs only -> enabled again, a second real backup succeeds.
set +x
set -euo pipefail
# shellcheck source=../lib/common.sh
source "$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")/../lib" && pwd)/common.sh"
if [[ ${1:-} != --configured ]]; then
    exec "$REPO_ROOT/scripts/database/with-disposable-databases.sh" -- "$0" --configured "$@"
fi
shift
stack_options=(--download)
if [[ ${1:-} == --no-build ]]; then stack_options+=(--no-build); shift; fi
(( $# == 0 )) || fail 'Usage: run-storage-connections-smoke.sh [--no-build]'
load_local_env
require_command dotnet
require_command python3
make_runtime_dir
work=$RUNTIME_DIR
suffix=$(python3 -c 'import uuid; print(uuid.uuid4().hex[:12])')
source_database="bp_m5_source_$suffix"
target_database="bp_m5_target_$suffix"
stack_pid='' agent_pid=''
cleanup_smoke() {
    if [[ ${1:-0} != 0 && -f "$work/agent.log" ]]; then
        printf 'Storage connections smoke Agent diagnostic (redacted):\n' >&2
        run_redacted cat "$work/agent.log" >&2
    fi
    for pid in "$agent_pid" "$stack_pid"; do [[ -z $pid ]] || kill "$pid" 2>/dev/null || true; done
    for pid in "$agent_pid" "$stack_pid"; do [[ -z $pid ]] || wait "$pid" 2>/dev/null || true; done
}
rc=0
trap 'rc=$?; cleanup_smoke "$rc" || rc=1; cleanup_runtime "$rc"' EXIT
export BP_SMOKE_POSTGRES_CONTAINER=$POSTGRES_CONTAINER
"$REPO_ROOT/scripts/frontend/browser-smoke-env.sh" "${stack_options[@]}" >"$work/stack.log" 2>&1 &
stack_pid=$!
ready=false
for _ in $(seq 1 240); do
    kill -0 "$stack_pid" 2>/dev/null || { cat "$work/stack.log"; fail 'Storage connections smoke stack exited.'; }
    if python3 - "$work/stack.log" <<'PY'
import sys
sys.exit(0 if '\nSMOKE_READY\n' in open(sys.argv[1]).read() else 1)
PY
    then ready=true; break; fi
    sleep 1
done
[[ $ready == true ]] || fail 'Storage connections smoke stack startup timed out.'
read -r server certificate state central_root credentials smoke_state < <(python3 - "$work/stack.log" <<'PY'
import sys
values = dict(line.strip().split('=', 1) for line in open(sys.argv[1]) if line.startswith('SMOKE_') and '=' in line)
print(*(values[key] for key in ('SMOKE_API_URL', 'SMOKE_CERTIFICATE_FILE', 'SMOKE_DOWNLOAD_STATE_FILE', 'SMOKE_CENTRAL_ROOT', 'SMOKE_CREDENTIALS_FILE', 'SMOKE_STATE_FILE')))
PY
)
scope=$(python3 -c 'import json, sys; print(json.load(open(sys.argv[1]))["tenantScope"])' "$state")
smoke_database=$(python3 -c 'import json, sys; print(json.load(open(sys.argv[1]))["database"])' "$smoke_state")
fixture=("$REPO_ROOT/scripts/frontend/storage-connections-smoke-fixture.py" --server "$server" --certificate "$certificate" --state "$state" --credentials-file "$credentials")
step() { printf '== %s\n' "$1"; }

python3 "$REPO_ROOT/scripts/frontend/restore-smoke-sql.py" setup --container "$MSSQL_CONTAINER" --database "$source_database"
run_redacted dotnet publish "$REPO_ROOT/backup-backend/src/hosts/agent" -c Release -r linux-x64 --self-contained true -p:AgentPlatform=Linux -o "$work/agent" >"$work/publish-agent.log"
mkdir -p "$work/identity"
"$REPO_ROOT/scripts/integration/enroll-agent.sh" --host "$work/agent/BackupPlatform.Agent.Host" --server "$server" \
    --token-file "$(dirname "$state")/central-token.json" --store-directory "$work/identity" --name "Connections smoke $suffix" --certificate "$certificate"
export SSL_CERT_FILE=$certificate
export Agent__Backend__BaseUri=$server Agent__CredentialStoreDirectory="$work/identity"
export Agent__Connectivity__HeartbeatInterval=00:00:05 Agent__Connectivity__PollInterval=00:00:05 Agent__Connectivity__MinimumPollSpacing=00:00:01
export Agent__Backup__Staging__Mapping=Mapped
export Agent__Backup__Staging__ServerDirectory=$BACKUP_PLATFORM_TEST_MSSQL_STAGING_SERVER_DIR
export Agent__Backup__Staging__AgentDirectory=$BACKUP_PLATFORM_TEST_MSSQL_STAGING_AGENT_DIR
export Agent__Backup__CentralStorage__RootDirectory=$central_root Agent__Backup__CentralStorage__TenantScope=$scope
(cd "$work/agent" && exec ./BackupPlatform.Agent.Host) >"$work/agent.log" 2>&1 &
agent_pid=$!

step 'real Central backup (Agent -> MSSQL -> Central root)'
python3 "$REPO_ROOT/scripts/frontend/download-smoke-fixture.py" --server "$server" --certificate "$certificate" --state "$state" backup \
    --provider central --agent-name "Connections smoke $suffix" --sql-host "$BACKUP_PLATFORM_TEST_MSSQL_HOST" --sql-port "$BACKUP_PLATFORM_TEST_MSSQL_PORT" --database "$source_database"

step 'connections before the upgrade'
python3 "${fixture[@]}" inspect --stage before
python3 "${fixture[@]}" checks

step 'upgrade path: roll the connection migration back and apply it again under the real replica'
admin=$BACKUP_PLATFORM_TEST_CONNECTION_STRING
BACKUP_PLATFORM_CONNECTION_STRING=$(python3 - "$admin" "$smoke_database" <<'PY'
import sys
parts = [p for p in sys.argv[1].split(';') if p.strip() and not p.strip().lower().startswith('database=')]
print(';'.join(parts + ['Database="%s"' % sys.argv[2]]))
PY
)
export BACKUP_PLATFORM_CONNECTION_STRING
(
    cd "$REPO_ROOT/backup-backend"
    run_redacted dotnet tool restore >/dev/null
    run_redacted dotnet ef database update 20261009072143_SupportAccess --project src/core/infrastructure --configuration Release --no-build >/dev/null
    printf 'Rolled back to the schema before storage connections\n'
    run_redacted dotnet ef database update --project src/core/infrastructure --configuration Release --no-build >/dev/null
    printf 'Storage connections migration re-applied (backfilled the real replica)\n'
)
unset BACKUP_PLATFORM_CONNECTION_STRING
python3 "${fixture[@]}" inspect --stage after-upgrade

step 'download and real restore after the upgrade'
python3 "$REPO_ROOT/scripts/frontend/download-smoke-fixture.py" --server "$server" --certificate "$certificate" --state "$state" verify \
    --provider central --output "$work/download-1.bak"
python3 "${fixture[@]}" restore --database "$target_database" --sql-host "$BACKUP_PLATFORM_TEST_MSSQL_HOST" --sql-port "$BACKUP_PLATFORM_TEST_MSSQL_PORT"
python3 "$REPO_ROOT/scripts/frontend/restore-smoke-sql.py" verify --container "$MSSQL_CONTAINER" --database "$target_database"

step 'disabled connection: new runs blocked, stored backup untouched, still downloadable and restorable'
python3 "${fixture[@]}" toggle --disable
python3 "${fixture[@]}" disabled-semantics
python3 "$REPO_ROOT/scripts/frontend/download-smoke-fixture.py" --server "$server" --certificate "$certificate" --state "$state" verify \
    --provider central --output "$work/download-2.bak"
python3 "${fixture[@]}" restore --database "${target_database}_restored" --sql-host "$BACKUP_PLATFORM_TEST_MSSQL_HOST" --sql-port "$BACKUP_PLATFORM_TEST_MSSQL_PORT"
python3 "$REPO_ROOT/scripts/frontend/restore-smoke-sql.py" verify --container "$MSSQL_CONTAINER" --database "${target_database}_restored"

step 'enabled again: a second real backup succeeds'
python3 "${fixture[@]}" toggle
python3 "$REPO_ROOT/scripts/frontend/download-smoke-fixture.py" --server "$server" --certificate "$certificate" --state "$state" backup \
    --provider central --agent-name "Connections smoke $suffix" --sql-host "$BACKUP_PLATFORM_TEST_MSSQL_HOST" --sql-port "$BACKUP_PLATFORM_TEST_MSSQL_PORT" --database "$source_database"
printf 'STORAGE_CONNECTIONS_SMOKE_OK\n'
