#!/usr/bin/env bash
# R1 isolated restore verification: disposable MSSQL + PostgreSQL -> real Linux Agents -> real central storage -> API acceptance
# (scripts/frontend/r1-acceptance.py), then (without --acceptance-only) the owned stack stays up for the browser smoke.
# Only containers and databases created by this run are touched. Agent processes run under a supervisor so a SIGKILL is a real crash.
set +x
set -euo pipefail
source "$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")/../lib" && pwd)/common.sh"
if [[ ${1:-} != --configured ]]; then
    exec "$REPO_ROOT/scripts/database/with-disposable-databases.sh" -- "$0" --configured "$@"
fi
shift
stack_options=(--download)
acceptance_only=false
windows=false
only=()
while (($#)); do
    case $1 in
        --no-build) stack_options+=(--no-build) ;;
        --acceptance-only) acceptance_only=true ;;
        --windows) windows=true ;;
        --only) shift; while (($#)) && [[ $1 != --* ]]; do only+=("$1"); shift; done; continue ;;
        *) fail 'Usage: run-r1-smoke.sh [--no-build] [--acceptance-only] [--windows] [--only SCENARIO...]' ;;
    esac
    shift
done
load_local_env
require_command dotnet
require_command python3
require_command unshare
make_runtime_dir
work=$RUNTIME_DIR
mkdir -p "$work/agents"
pg_tools=${BACKUP_PLATFORM_TEST_POSTGRES_TOOLS_DIRECTORY:-$HOME/.local/pg18/bin}
pg_libs=${BACKUP_PLATFORM_TEST_POSTGRES_LIBRARY_DIRECTORY:-$HOME/.local/pg18/lib}
[[ -x $pg_tools/pg_dump && -x $pg_tools/pg_restore ]] || fail 'PostgreSQL client tools are required (BACKUP_PLATFORM_TEST_POSTGRES_TOOLS_DIRECTORY).'
stack_pid='' windows_root='' windows_prepared=false 
cleanup_smoke() {
    local windows_result=0
    : > "$work/agents/stop" 2>/dev/null || true
    for name in mssql pg pg-small; do
        [[ -f $work/agents/$name/pid ]] && kill -9 "$(<"$work/agents/$name/pid")" 2>/dev/null || true
        [[ -f $work/agents/$name/supervisor.pid ]] && kill "$(<"$work/agents/$name/supervisor.pid")" 2>/dev/null || true
    done
    if [[ $windows_prepared == true ]]; then
        if (( rc != 0 )); then
            printf "Windows Agent diagnostic:\n" >&2
            "$REPO_ROOT/scripts/windows/run-powershell.sh" --command "if (Test-Path '$windows_root\\agent.log') { Get-Content '$windows_root\\agent.log' -Tail 60 } if (Test-Path '$windows_root\\agent-err.log') { Get-Content '$windows_root\\agent-err.log' -Tail 60 }" >&2 || true
        fi
        "$REPO_ROOT/scripts/windows/run-powershell.sh" --file "$REPO_ROOT/scripts/windows/R1-Console-Agent.ps1" -Action cleanup -RootDirectory "$windows_root" || windows_result=1
    fi
    [[ ! -f $work/api.pid ]] || kill "$(<"$work/api.pid")" 2>/dev/null || true
    [[ -z $stack_pid ]] || kill "$stack_pid" 2>/dev/null || true
    [[ -z $stack_pid ]] || wait "$stack_pid" 2>/dev/null || true
    return "$windows_result"
}
rc=0
trap 'rc=$?; cleanup_rc=0; if (( rc != 0 )) && [[ -n ${BP_R1_LOG_DIR:-} ]]; then mkdir -p "$BP_R1_LOG_DIR"; for log in "$work"/agents/*/agent.log; do cp "$log" "$BP_R1_LOG_DIR/$(basename "$(dirname "$log")").log" 2>/dev/null || true; done; fi; if (( rc != 0 )); then for log in "$work"/agents/*/agent.log; do [[ -f $log ]] && { printf "Agent diagnostic %s (redacted):\n" "$log" >&2; run_redacted tail -n 40 "$log" >&2; }; done; fi; cleanup_smoke || cleanup_rc=1; if (( rc == 0 && cleanup_rc != 0 )); then rc=1; fi; cleanup_runtime "$rc"' EXIT
export BP_SMOKE_POSTGRES_CONTAINER=$POSTGRES_CONTAINER
# The VM must reach the API: opt in to the libvirt host address (adds it to the disposable certificate and binds the listener to all interfaces).
$windows && export BP_SMOKE_PUBLIC_HOST=${BP_SMOKE_PUBLIC_HOST:-192.168.122.1}
# Only this owned stack shortens the guard on a replaced database's rollback copy, so the removal rules can be exercised in minutes instead of days.
export Restores__RollbackRetention=00:00:45
"$REPO_ROOT/scripts/frontend/browser-smoke-env.sh" "${stack_options[@]}" >"$work/stack.log" 2>&1 &
stack_pid=$!
ready=false
for _ in $(seq 1 240); do
    kill -0 "$stack_pid" 2>/dev/null || { cat "$work/stack.log"; fail 'R1 smoke stack exited.'; }
    if python3 - "$work/stack.log" <<'PY'
import sys
sys.exit(0 if '\nSMOKE_READY\n' in open(sys.argv[1]).read() else 1)
PY
    then ready=true; break; fi
    sleep 1
done
[[ $ready == true ]] || fail 'R1 smoke stack startup timed out.'
read -r server agent_server certificate state central_root frontend credentials < <(python3 - "$work/stack.log" <<'PY'
import sys
values = dict(line.strip().split('=', 1) for line in open(sys.argv[1]) if line.startswith('SMOKE_') and '=' in line)
print(*(values[key] for key in ('SMOKE_API_URL', 'SMOKE_AGENT_URL', 'SMOKE_CERTIFICATE_FILE', 'SMOKE_DOWNLOAD_STATE_FILE', 'SMOKE_CENTRAL_ROOT', 'SMOKE_FRONTEND_URL', 'SMOKE_CREDENTIALS_FILE')))
PY
)
scope=$(python3 -c 'import json, sys; print(json.load(open(sys.argv[1]))["tenantScope"])' "$state")
suffix=$(python3 -c 'import uuid; print(uuid.uuid4().hex[:8])')
# Roles and a foreign tenant for the isolation scenarios (the M5 restore fixture already creates them).
python3 "$REPO_ROOT/scripts/frontend/restore-smoke-fixture.py" --server "$server" --certificate "$certificate" --state "$state" --credentials-file "$credentials" \
    setup --source-database "bp_m5_source_000000000000" --target-database "bp_m5_target_000000000000" >/dev/null
driver=(python3 "$REPO_ROOT/scripts/frontend/r1-acceptance.py")
common=(--server "$server" --certificate "$certificate" --state "$state" --work "$work")
"${driver[@]}" tokens "${common[@]}" --names mssql pg pg-small win

run_redacted dotnet publish "$REPO_ROOT/backup-backend/src/hosts/agent" -c Release -r linux-x64 --self-contained true -p:AgentPlatform=Linux -o "$work/agent-bin" >"$work/publish.log"

# A least-privilege PostgreSQL login: CREATEDB only, no superuser. Verification runs as this role; backups use the superuser.
verifier_password=$(python3 -c 'import secrets; print(secrets.token_hex(16))')
docker exec -i -e PGPASSWORD="$BACKUP_PLATFORM_TEST_POSTGRES_PASSWORD" "$POSTGRES_CONTAINER" psql -U postgres -X -q -v ON_ERROR_STOP=1 \
    -c "CREATE ROLE bp_r1_verifier LOGIN CREATEDB NOSUPERUSER NOCREATEROLE NOREPLICATION PASSWORD '$verifier_password'" >/dev/null
pg_host=${BACKUP_PLATFORM_TEST_POSTGRES_HOST:-127.0.0.1}
pg_port=$(python3 - <<'PY'
import os, re
match = re.search(r'Port="?(\d+)', os.environ['BACKUP_PLATFORM_TEST_CONNECTION_STRING'])
print(match.group(1))
PY
)

# start_agent NAME STAGING_AGENT_DIR [tmpfs-size]: enrolls once, then runs under a supervisor that restarts it after a crash unless a hold file exists.
start_agent() {
    local name=$1 staging=$2 tmpfs=${3:-} dir="$work/agents/$1"
    mkdir -p "$dir/identity" "$staging"
    "$REPO_ROOT/scripts/integration/enroll-agent.sh" --host "$work/agent-bin/BackupPlatform.Agent.Host" --server "$server" --token-file "$work/$name-token.json" \
        --store-directory "$dir/identity" --name "R1 $name $suffix" --certificate "$certificate" >/dev/null
    cat > "$dir/supervise.sh" <<EOF
#!/usr/bin/env bash
set +x
cd "$work/agent-bin"
$( [[ -n $tmpfs ]] && printf 'mount -t tmpfs -o size=%s tmpfs "%s"\n' "$tmpfs" "$staging" )
while [[ ! -e "$work/agents/stop" ]]; do
    while [[ -e "$dir/hold" ]]; do sleep 1; done
    ./BackupPlatform.Agent.Host >>"$dir/agent.log" 2>&1 &
    echo \$! > "$dir/pid"
    wait \$! || true
    sleep 1
done
EOF
    chmod +x "$dir/supervise.sh"
    if [[ -n $tmpfs ]]; then
        (exec unshare -Urm "$dir/supervise.sh") >/dev/null 2>&1 &
    else
        (exec "$dir/supervise.sh") >/dev/null 2>&1 &
    fi
    echo $! > "$dir/supervisor.pid"
    for _ in $(seq 1 60); do [[ -s $dir/pid ]] && break; sleep 0.5; done
    [[ -s $dir/pid ]] || fail "Agent $name did not start."
}
export SSL_CERT_FILE=$certificate
export Agent__Backend__BaseUri=$server
export Agent__Connectivity__HeartbeatInterval=00:00:05 Agent__Connectivity__PollInterval=00:00:05 Agent__Connectivity__MinimumPollSpacing=00:00:01
export Agent__Backup__CentralStorage__RootDirectory=$central_root Agent__Backup__CentralStorage__TenantScope=$scope
# The client libraries must never reach the agent itself (their OpenSSL would replace the one .NET uses): only the tools get them, through wrappers.
mkdir -p "$work/pg-tools"
for tool in pg_dump pg_restore; do
    printf '#!/usr/bin/env bash\nLD_LIBRARY_PATH=%q exec %q "$@"\n' "$pg_libs" "$pg_tools/$tool" > "$work/pg-tools/$tool"
    chmod +x "$work/pg-tools/$tool"
done
export Agent__Backup__PostgreSql__ClientToolsDirectory=$work/pg-tools
sql_secret() { python3 -c 'import json, sys; print(json.dumps({"userName": sys.argv[1], "password": sys.argv[2]}))' "$1" "$2"; }

( # MSSQL agent: mapped staging shared with the disposable container; a good and a bad credential.
  export Agent__CredentialStoreDirectory="$work/agents/mssql/identity"
  export Agent__Backup__Staging__Mapping=Mapped Agent__Backup__Staging__ServerDirectory=$BACKUP_PLATFORM_TEST_MSSQL_STAGING_SERVER_DIR Agent__Backup__Staging__AgentDirectory=$BACKUP_PLATFORM_TEST_MSSQL_STAGING_AGENT_DIR
  export BACKUP_AGENT_SECRET_sql_smoke="$(sql_secret "$BACKUP_PLATFORM_TEST_MSSQL_USER" "$BACKUP_PLATFORM_TEST_MSSQL_PASSWORD")"
  export BACKUP_AGENT_SECRET_sql_bad="$(sql_secret "$BACKUP_PLATFORM_TEST_MSSQL_USER" "wrong-$verifier_password")"
  start_agent mssql "$BACKUP_PLATFORM_TEST_MSSQL_STAGING_AGENT_DIR" )
mssql_agent_dir=$work/agents/mssql
( # PostgreSQL agent: SameHost staging; superuser for backups, the CREATEDB-only role for verification, and a wrong password.
  export Agent__CredentialStoreDirectory="$work/agents/pg/identity"
  export Agent__Backup__Staging__Mapping=SameHost Agent__Backup__Staging__AgentDirectory="$work/pg-staging"
  export BACKUP_AGENT_SECRET_pg_smoke="$(sql_secret postgres "$BACKUP_PLATFORM_TEST_POSTGRES_PASSWORD")"
  export BACKUP_AGENT_SECRET_pg_verify="$(sql_secret bp_r1_verifier "$verifier_password")"
  export BACKUP_AGENT_SECRET_pg_bad="$(sql_secret bp_r1_verifier "wrong-$verifier_password")"
  start_agent pg "$work/pg-staging" )
( # The same PostgreSQL credentials, but a 6 MB staging volume: a real insufficient-capacity agent.
  export Agent__CredentialStoreDirectory="$work/agents/pg-small/identity"
  export Agent__Backup__Staging__Mapping=SameHost Agent__Backup__Staging__AgentDirectory="$work/pg-small-staging"
  export BACKUP_AGENT_SECRET_pg_verify="$(sql_secret bp_r1_verifier "$verifier_password")"
  start_agent pg-small "$work/pg-small-staging" 6m )

win_args=()
if $windows; then
    "$REPO_ROOT/scripts/windows/run-powershell.sh" --command "Set-Date -Date '$(python3 -c 'import datetime; print(datetime.datetime.now().strftime("%Y-%m-%d %H:%M:%S"))')'" >/dev/null || true
    console_agent=("$REPO_ROOT/scripts/windows/run-powershell.sh" --file "$REPO_ROOT/scripts/windows/R1-Console-Agent.ps1")
    run_redacted dotnet publish "$REPO_ROOT/backup-backend/src/hosts/agent" -c Release -r win-x64 --self-contained true -p:AgentPlatform=Windows -o "$work/windows-agent" >"$work/publish-windows.log"
    windows_root="C:\\ProgramData\\BackupAgentSmoke-$(python3 -c 'import uuid; print(uuid.uuid4().hex[:12])')"
    python3 - "$work/windows-agent" "$work/agent.zip" <<'PY'
from pathlib import Path
import sys, zipfile
root = Path(sys.argv[1])
with zipfile.ZipFile(sys.argv[2], 'w', zipfile.ZIP_DEFLATED) as archive:
    for file in root.rglob('*'):
        if file.is_file(): archive.write(file, file.relative_to(root))
PY
    # The VM keeps its own BackupAgent service untouched: the acceptance agent is a hidden console process inside its own owned root.
    windows_prepared=true
    "${console_agent[@]}" -Action prepare -RootDirectory "$windows_root"
    "$REPO_ROOT/scripts/windows/copy-to-vm.sh" "$work/agent.zip" "$windows_root\\agent.zip"
    "$REPO_ROOT/scripts/windows/copy-to-vm.sh" "$certificate" "$windows_root\\certificate.pem"
    "${console_agent[@]}" -Action stage -RootDirectory "$windows_root"
    "$REPO_ROOT/scripts/windows/copy-to-vm.sh" "$work/win-token.json" "$windows_root\\token.json"
    "${console_agent[@]}" -Action enroll -RootDirectory "$windows_root" -BackendUri "$agent_server" -TokenFile "$windows_root\\token.json" -AgentName "R1 win $suffix"
    read -r windows_sql_host windows_sql_port < <(python3 - "$REPO_ROOT/scripts/lib/local-env.py" "$BP_CREDENTIAL_FILE" "$work/windows-sql.keep.json" <<'PY'
import importlib.util, json, os, sys
from pathlib import Path
spec = importlib.util.spec_from_file_location('local_env', sys.argv[1])
module = importlib.util.module_from_spec(spec); spec.loader.exec_module(module)
os.environ['BP_MSSQL_PROFILE'] = 'windows'
values = module.configuration(module.read_sections(Path(sys.argv[2])))
fd = os.open(sys.argv[3], os.O_WRONLY | os.O_CREAT | os.O_EXCL, 0o600)
with os.fdopen(fd, 'w') as target:
    json.dump({'userName': values['BACKUP_PLATFORM_TEST_MSSQL_USER'], 'password': values['BACKUP_PLATFORM_TEST_MSSQL_PASSWORD']}, target)
print(values['BACKUP_PLATFORM_TEST_MSSQL_HOST'], values['BACKUP_PLATFORM_TEST_MSSQL_PORT'])
PY
)
    "$REPO_ROOT/scripts/windows/copy-to-vm.sh" "$work/windows-sql.keep.json" "$windows_root\\sql-secret.json"
    "${console_agent[@]}" -Action start -RootDirectory "$windows_root" -BackendUri "$agent_server"
    win_args=(--windows-root "$windows_root" --agent-win "R1 win $suffix" --win-sql-host "$windows_sql_host" --win-sql-port "$windows_sql_port" --win-sql-secret "$work/windows-sql.keep.json" --win-backend "$agent_server")
fi

mssql_port=$BACKUP_PLATFORM_TEST_MSSQL_PORT
args=("${driver[@]}" run "${common[@]}" --credentials-file "$credentials" --central-root "$central_root" --mssql-container "$MSSQL_CONTAINER" --pg-container "$POSTGRES_CONTAINER"
    --mssql-host "$BACKUP_PLATFORM_TEST_MSSQL_HOST" --mssql-port "$mssql_port" --pg-host "$pg_host" --pg-port "$pg_port"
    --agent-mssql "R1 mssql $suffix" --agent-pg "R1 pg $suffix" --agent-pg-small "R1 pg-small $suffix")
((${#only[@]} == 0)) || args+=(--only "${only[@]}")
((${#win_args[@]} == 0)) || args+=("${win_args[@]}")
$acceptance_only || args+=(--keep)
set +e
run_redacted "${args[@]}"
accept_rc=$?
set -e
printf 'SMOKE_FRONTEND_URL=%s\nSMOKE_API_URL=%s\nSMOKE_CERTIFICATE_FILE=%s\nSMOKE_STATE_FILE=%s\nSMOKE_CREDENTIALS_FILE=%s\nSMOKE_R1_WORK=%s\nSMOKE_R1_ACCEPTANCE_EXIT=%s\n' "$frontend" "$server" "$certificate" "$state" "$credentials" "$work" "$accept_rc"
if $acceptance_only; then exit "$accept_rc"; fi
printf 'R1_SMOKE_READY\n'
while kill -0 "$stack_pid" 2>/dev/null; do sleep 1; done
fail 'The R1 smoke stack exited.'
