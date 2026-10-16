#!/usr/bin/env bash
# Disposable MSSQL data -> real verified Central backup -> browser Restore UI.
set +x
set -euo pipefail
source "$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")/../lib" && pwd)/common.sh"
if [[ ${1:-} != --configured ]]; then
    exec "$REPO_ROOT/scripts/database/with-disposable-databases.sh" -- "$0" --configured "$@"
fi
shift
stack_options=(--download)
if [[ ${1:-} == --no-build ]]; then stack_options+=(--no-build); shift; fi
(( $# == 0 )) || fail 'Usage: run-restore-smoke.sh [--no-build]'
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
    local result=0
    if [[ ${1:-0} != 0 && -f "$work/agent.log" ]]; then
        printf 'Restore smoke Agent diagnostic (redacted):\n' >&2
        run_redacted cat "$work/agent.log" >&2
    fi
    for pid in "$agent_pid" "$stack_pid"; do [[ -z $pid ]] || kill "$pid" 2>/dev/null || true; done
    for pid in "$agent_pid" "$stack_pid"; do [[ -z $pid ]] || wait "$pid" 2>/dev/null || true; done
    return "$result"
}
rc=0
trap 'rc=$?; cleanup_smoke "$rc" || rc=1; cleanup_runtime "$rc"' EXIT
export BP_SMOKE_POSTGRES_CONTAINER=$POSTGRES_CONTAINER
"$REPO_ROOT/scripts/frontend/browser-smoke-env.sh" "${stack_options[@]}" >"$work/stack.log" 2>&1 &
stack_pid=$!
ready=false
for _ in $(seq 1 240); do
    kill -0 "$stack_pid" 2>/dev/null || { cat "$work/stack.log"; fail 'Restore smoke stack exited.'; }
    if python3 - "$work/stack.log" <<'PY'
import sys
sys.exit(0 if '\nSMOKE_READY\n' in open(sys.argv[1]).read() else 1)
PY
    then ready=true; break; fi
    sleep 1
done
[[ $ready == true ]] || fail 'Restore smoke stack startup timed out.'
read -r server certificate state central_root frontend credentials < <(python3 - "$work/stack.log" <<'PY'
import sys
values = dict(line.strip().split('=', 1) for line in open(sys.argv[1]) if line.startswith('SMOKE_') and '=' in line)
print(*(values[key] for key in ('SMOKE_API_URL', 'SMOKE_CERTIFICATE_FILE', 'SMOKE_DOWNLOAD_STATE_FILE', 'SMOKE_CENTRAL_ROOT', 'SMOKE_FRONTEND_URL', 'SMOKE_CREDENTIALS_FILE')))
PY
)
scope=$(python3 - "$state" <<'PY'
import json, sys
print(json.load(open(sys.argv[1]))['tenantScope'])
PY
)
python3 "$REPO_ROOT/scripts/frontend/restore-smoke-sql.py" setup --container "$MSSQL_CONTAINER" --database "$source_database"
python3 "$REPO_ROOT/scripts/frontend/restore-smoke-fixture.py" --server "$server" --certificate "$certificate" --state "$state" --credentials-file "$credentials" setup --source-database "$source_database" --target-database "$target_database"
run_redacted dotnet publish "$REPO_ROOT/backup-backend/src/hosts/agent" -c Release -r linux-x64 --self-contained true -p:AgentPlatform=Linux -o "$work/agent" >"$work/publish-agent.log"
mkdir -p "$work/identity"
"$REPO_ROOT/scripts/integration/enroll-agent.sh" --host "$work/agent/BackupPlatform.Agent.Host" --server "$server" \
    --token-file "$(dirname "$state")/central-token.json" --store-directory "$work/identity" --name "Restore browser smoke $suffix" --certificate "$certificate"
export SSL_CERT_FILE=$certificate
export Agent__Backend__BaseUri=$server Agent__CredentialStoreDirectory="$work/identity"
export Agent__Connectivity__HeartbeatInterval=00:00:05 Agent__Connectivity__PollInterval=00:00:05 Agent__Connectivity__MinimumPollSpacing=00:00:01
export Agent__Backup__Staging__Mapping=Mapped
export Agent__Backup__Staging__ServerDirectory=$BACKUP_PLATFORM_TEST_MSSQL_STAGING_SERVER_DIR
export Agent__Backup__Staging__AgentDirectory=$BACKUP_PLATFORM_TEST_MSSQL_STAGING_AGENT_DIR
export Agent__Backup__CentralStorage__RootDirectory=$central_root Agent__Backup__CentralStorage__TenantScope=$scope
(cd "$work/agent" && exec ./BackupPlatform.Agent.Host) >"$work/agent.log" 2>&1 &
agent_pid=$!
python3 "$REPO_ROOT/scripts/frontend/download-smoke-fixture.py" --server "$server" --certificate "$certificate" --state "$state" backup \
    --provider central --agent-name "Restore browser smoke $suffix" --sql-host "$BACKUP_PLATFORM_TEST_MSSQL_HOST" --sql-port "$BACKUP_PLATFORM_TEST_MSSQL_PORT" --database "$source_database"
python3 "$REPO_ROOT/scripts/frontend/restore-smoke-fixture.py" --server "$server" --certificate "$certificate" --state "$state" --credentials-file "$credentials" verify --sql-host "$BACKUP_PLATFORM_TEST_MSSQL_HOST" --sql-port "$BACKUP_PLATFORM_TEST_MSSQL_PORT"
printf 'SMOKE_FRONTEND_URL=%s\nSMOKE_API_URL=%s\nSMOKE_CERTIFICATE_FILE=%s\nSMOKE_RESTORE_STATE_FILE=%s\nSMOKE_CREDENTIALS_FILE=%s\nSMOKE_MSSQL_CONTAINER=%s\nSMOKE_SOURCE_DATABASE=%s\nSMOKE_TARGET_DATABASE=%s\nSMOKE_RESTORE_RUNNER_PID=%s\n' "$frontend" "$server" "$certificate" "$state" "$credentials" "$MSSQL_CONTAINER" "$source_database" "$target_database" "$$"
printf 'RESTORE_SMOKE_READY\n'
while kill -0 "$agent_pid" 2>/dev/null && kill -0 "$stack_pid" 2>/dev/null; do sleep 1; done
fail 'A restore smoke process exited.'
