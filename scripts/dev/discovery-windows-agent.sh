#!/usr/bin/env bash
# Disposable Windows console Agent on the VM for the Database Auto Discovery acceptance, enrolled into the stack of discovery-stack.sh
# (which must listen on BP_DISC_PUBLIC_HOST, default 192.168.122.1). The VM's own BackupAgent service and identity are never touched: the agent is a hidden
# console process inside its own owned BackupAgentSmoke-<hex> root (scripts/windows/R1-Console-Agent.ps1), removed by 'down' together with the temporary
# trust anchor. The VM's SQL login is entered through the API (database-logins), never injected into the agent.
#   discovery-windows-agent.sh up | down | stop | start | log
set +x
set -euo pipefail
# shellcheck source=scripts/lib/common.sh
source "$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")/../lib" && pwd)/common.sh"
H=${BP_DISC_HOME:-/tmp/bp-discovery-acceptance}
API_PORT=${BP_DISC_API_PORT:-7391}
PUBLIC_HOST=${BP_DISC_PUBLIC_HOST:-192.168.122.1}
SERVER="https://$PUBLIC_HOST:$API_PORT"
STATE="$H/windows.json"
ps="$REPO_ROOT/scripts/windows/run-powershell.sh"
console=("$ps" --file "$REPO_ROOT/scripts/windows/R1-Console-Agent.ps1")
copy="$REPO_ROOT/scripts/windows/copy-to-vm.sh"
root() { python3 -c 'import json,sys; print(json.load(open(sys.argv[1]))["root"])' "$STATE"; }

case ${1:-} in
up)
    [[ -f $H/cert.pem && ! -f $STATE ]] || fail 'Start discovery-stack.sh first, and remove the previous Windows agent with down.'
    BP_MSSQL_PROFILE=windows load_local_env
    work=$RUNTIME_DIR
    "$ps" --command "Set-Date -Date '$(python3 -c 'import datetime; print(datetime.datetime.now().strftime("%Y-%m-%d %H:%M:%S"))')'" >/dev/null || true
    run_redacted dotnet publish "$REPO_ROOT/backup-backend/src/hosts/agent" -c Release -r win-x64 --self-contained true -p:AgentPlatform=Windows -o "$work/agent" --nologo -v q >/dev/null
    python3 - "$work/agent" "$work/agent.zip" <<'PY'
from pathlib import Path
import sys, zipfile
root = Path(sys.argv[1])
with zipfile.ZipFile(sys.argv[2], 'w', zipfile.ZIP_DEFLATED) as archive:
    for file in root.rglob('*'):
        if file.is_file(): archive.write(file, file.relative_to(root))
PY
    windows_root="C:\\ProgramData\\BackupAgentSmoke-$(python3 -c 'import uuid; print(uuid.uuid4().hex[:12])')"
    python3 - "$STATE" "$windows_root" <<'PY'
import json, os, sys
fd = os.open(sys.argv[1], os.O_WRONLY | os.O_CREAT | os.O_EXCL, 0o600)
with os.fdopen(fd, 'w') as handle: json.dump({'root': sys.argv[2]}, handle)
PY
    "${console[@]}" -Action prepare -RootDirectory "$windows_root"
    "$copy" "$work/agent.zip" "$windows_root\\agent.zip"
    "$copy" "$H/cert.pem" "$windows_root\\certificate.pem"
    "${console[@]}" -Action stage -RootDirectory "$windows_root"
    python3 "$REPO_ROOT/scripts/dev/bootstrap.py" enrollment-token --accounts-file "$H/accounts.yaml" --server "https://127.0.0.1:$API_PORT" --certificate "$H/cert.pem" --output "$work/token.json"
    "$copy" "$work/token.json" "$windows_root\\token.json"
    "${console[@]}" -Action enroll -RootDirectory "$windows_root" -BackendUri "$SERVER" -TokenFile "$windows_root\\token.json" -AgentName disc-windows-agent
    python3 - "$work/sql.json" <<'PY'
import importlib.util, json, os, sys
from pathlib import Path
root = Path(os.environ['REPO_ROOT'])
spec = importlib.util.spec_from_file_location('local_env', root / 'scripts/lib/local-env.py')
module = importlib.util.module_from_spec(spec); spec.loader.exec_module(module)
os.environ['BP_MSSQL_PROFILE'] = 'windows'  # the VM's SQL login, not the local container's
values = module.configuration(module.read_sections(Path(os.environ['BP_CREDENTIAL_FILE'])))
fd = os.open(sys.argv[1], os.O_WRONLY | os.O_CREAT | os.O_EXCL, 0o600)
with os.fdopen(fd, 'w') as target:
    json.dump({'userName': values['BACKUP_PLATFORM_TEST_MSSQL_USER'], 'password': values['BACKUP_PLATFORM_TEST_MSSQL_PASSWORD']}, target)
PY
    "$copy" "$work/sql.json" "$windows_root\\sql-secret.json"
    "${console[@]}" -Action start -RootDirectory "$windows_root" -BackendUri "$SERVER"
    printf 'Windows discovery agent running (root %s)\n' "$windows_root" ;;
stop) "${console[@]}" -Action stop -RootDirectory "$(root)" ;;
refresh-secret)  # replaces the SQL login file of an existing root and restarts the agent
    BP_MSSQL_PROFILE=windows load_local_env
    "$0" stop
    python3 - "$RUNTIME_DIR/sql.json" <<'PY'
import importlib.util, json, os, sys
from pathlib import Path
root = Path(os.environ['REPO_ROOT'])
spec = importlib.util.spec_from_file_location('local_env', root / 'scripts/lib/local-env.py')
module = importlib.util.module_from_spec(spec); spec.loader.exec_module(module)
os.environ['BP_MSSQL_PROFILE'] = 'windows'
values = module.configuration(module.read_sections(Path(os.environ['BP_CREDENTIAL_FILE'])))
fd = os.open(sys.argv[1], os.O_WRONLY | os.O_CREAT | os.O_EXCL, 0o600)
with os.fdopen(fd, 'w') as target:
    json.dump({'userName': values['BACKUP_PLATFORM_TEST_MSSQL_USER'], 'password': values['BACKUP_PLATFORM_TEST_MSSQL_PASSWORD']}, target)
PY
    "$ps" --command "Remove-Item -LiteralPath '$(root)\\sql-secret.json' -Force" >/dev/null
    "$copy" "$RUNTIME_DIR/sql.json" "$(root)\\sql-secret.json"
    "$0" start ;;
start) "${console[@]}" -Action start -RootDirectory "$(root)" -BackendUri "$SERVER" ;;
log) "$ps" --command "\$r='$(root)'; foreach (\$f in 'agent.log','agent-err.log') { if (Test-Path \"\$r\\\$f\") { Get-Content \"\$r\\\$f\" -Tail 40 } }" ;;
down)
    [[ -f $STATE ]] || fail 'No Windows discovery agent state.'
    "${console[@]}" -Action cleanup -RootDirectory "$(root)"
    rm -f -- "$STATE"; printf 'Windows discovery agent removed\n' ;;
*) fail 'Usage: discovery-windows-agent.sh up|down|stop|start|log' ;;
esac
