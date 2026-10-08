#!/usr/bin/env bash
# Real MSSQL -> Central (Linux Agent) and Agent-Local (Windows service) -> HTTP downloads.
# Keeps the owned browser stack alive for native browser download verification.
set +x
set -euo pipefail
source "$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")/../lib" && pwd)/common.sh"
if [[ ${1:-} != --configured ]]; then
    exec "$REPO_ROOT/scripts/database/with-disposable-databases.sh" -- "$0" --configured
fi
load_local_env
require_command dotnet
require_command python3
make_runtime_dir
work=$RUNTIME_DIR
suffix=$(python3 -c 'import uuid; print(uuid.uuid4().hex[:12])')
windows_root="C:\\ProgramData\\BackupAgentSmoke-$suffix"
stack_pid='' agent_pid='' windows_prepared=false windows_installed=false
cleanup_smoke() {
    local result=0
    if [[ ${1:-0} != 0 && -f "$work/linux-agent.log" ]]; then
        printf 'Linux Agent diagnostic (redacted):\n' >&2
        run_redacted cat "$work/linux-agent.log" >&2
    fi
    if [[ $windows_installed == true ]]; then
        "$REPO_ROOT/scripts/windows/run-powershell.sh" --file "$REPO_ROOT/scripts/windows/Onboarding-Smoke.ps1" -Action uninstall -RootDirectory "$windows_root" || result=1
    fi
    if [[ $windows_prepared == true && $result == 0 ]]; then
        "$REPO_ROOT/scripts/windows/run-powershell.sh" --file "$REPO_ROOT/scripts/windows/Onboarding-Smoke.ps1" -Action cleanup -RootDirectory "$windows_root" || result=1
    fi
    for pid in "$agent_pid" "$stack_pid"; do
        [[ -z $pid ]] || kill "$pid" 2>/dev/null || true
    done
    for pid in "$agent_pid" "$stack_pid"; do
        [[ -z $pid ]] || wait "$pid" 2>/dev/null || true
    done
    return "$result"
}
trap 'rc=$?; if ! cleanup_smoke "$rc"; then if (( rc == 0 )); then rc=1; fi; fi; cleanup_runtime "$rc"' EXIT
export BP_SMOKE_PUBLIC_HOST=${BP_SMOKE_PUBLIC_HOST:-192.168.122.1}
export BP_SMOKE_POSTGRES_CONTAINER=${POSTGRES_CONTAINER:-postgres}
"$REPO_ROOT/scripts/frontend/browser-smoke-env.sh" --download >"$work/stack.log" 2>&1 &
stack_pid=$!
ready=false
for _ in $(seq 1 240); do
    kill -0 "$stack_pid" 2>/dev/null || { cat "$work/stack.log"; fail 'Download smoke stack exited.'; }
    if python3 - "$work/stack.log" <<'PY'
import sys
sys.exit(0 if '\nSMOKE_READY\n' in open(sys.argv[1]).read() else 1)
PY
    then ready=true; break; fi
    sleep 1
done
[[ $ready == true ]] || fail 'Download smoke stack startup timed out.'
read -r server agent_server certificate state central_root frontend < <(python3 - "$work/stack.log" <<'PY'
import sys
values = dict(line.strip().split('=', 1) for line in open(sys.argv[1]) if line.startswith('SMOKE_') and '=' in line)
print(*(values[key] for key in ('SMOKE_API_URL', 'SMOKE_AGENT_URL', 'SMOKE_CERTIFICATE_FILE', 'SMOKE_DOWNLOAD_STATE_FILE', 'SMOKE_CENTRAL_ROOT', 'SMOKE_FRONTEND_URL')))
PY
)
scope=$(python3 - "$state" <<'PY'
import json, sys
print(json.load(open(sys.argv[1]))['tenantScope'])
PY
)
run_redacted dotnet publish "$REPO_ROOT/backup-backend/src/hosts/agent" -c Release -r linux-x64 --self-contained true -p:AgentPlatform=Linux -o "$work/linux-agent" >"$work/publish-linux.log"
run_redacted dotnet publish "$REPO_ROOT/backup-backend/src/hosts/agent" -c Release -r win-x64 --self-contained true -p:AgentPlatform=Windows -o "$work/windows-agent" >"$work/publish-windows.log"
mkdir -p "$work/linux-identity"
"$REPO_ROOT/scripts/integration/enroll-agent.sh" --host "$work/linux-agent/BackupPlatform.Agent.Host" --server "$server" \
    --token-file "$(dirname "$state")/central-token.json" --store-directory "$work/linux-identity" --name "Central download smoke $suffix" --certificate "$certificate"
export SSL_CERT_FILE=$certificate
export Agent__Backend__BaseUri=$server Agent__CredentialStoreDirectory="$work/linux-identity"
export Agent__Connectivity__HeartbeatInterval=00:00:05 Agent__Connectivity__PollInterval=00:00:05 Agent__Connectivity__MinimumPollSpacing=00:00:01
export Agent__Backup__Staging__Mapping=Mapped
export Agent__Backup__Staging__ServerDirectory=$BACKUP_PLATFORM_TEST_MSSQL_STAGING_SERVER_DIR
export Agent__Backup__Staging__AgentDirectory=$BACKUP_PLATFORM_TEST_MSSQL_STAGING_AGENT_DIR
export Agent__Backup__CentralStorage__RootDirectory=$central_root Agent__Backup__CentralStorage__TenantScope=$scope
BACKUP_AGENT_SECRET_sql_smoke=$(python3 -c 'import json, os; print(json.dumps({"userName":os.environ["BACKUP_PLATFORM_TEST_MSSQL_USER"],"password":os.environ["BACKUP_PLATFORM_TEST_MSSQL_PASSWORD"]}))')
export BACKUP_AGENT_SECRET_sql_smoke
(cd "$work/linux-agent" && exec ./BackupPlatform.Agent.Host) >"$work/linux-agent.log" 2>&1 &
agent_pid=$!
fixture=(python3 "$REPO_ROOT/scripts/frontend/download-smoke-fixture.py" --server "$server" --certificate "$certificate" --state "$state")
"${fixture[@]}" backup --provider central --agent-name "Central download smoke $suffix" --sql-host "$BACKUP_PLATFORM_TEST_MSSQL_HOST" --sql-port "${BACKUP_PLATFORM_TEST_MSSQL_PORT:-1433}"
"${fixture[@]}" verify --provider central --output "$work/central.bak"
python3 - "$work/windows-agent" "$work/agent.zip" <<'PY'
from pathlib import Path
import sys, zipfile
root = Path(sys.argv[1])
with zipfile.ZipFile(sys.argv[2], 'w', zipfile.ZIP_DEFLATED) as archive:
    for file in root.rglob('*'):
        if file.is_file(): archive.write(file, file.relative_to(root))
PY
"$REPO_ROOT/scripts/windows/run-powershell.sh" --file "$REPO_ROOT/scripts/windows/Onboarding-Smoke.ps1" -Action prepare -RootDirectory "$windows_root"
windows_prepared=true
"$REPO_ROOT/scripts/windows/copy-to-vm.sh" "$work/agent.zip" "$windows_root\\agent.zip"
"$REPO_ROOT/scripts/windows/copy-to-vm.sh" "$certificate" "$windows_root\\certificate.pem"
"$REPO_ROOT/scripts/windows/run-powershell.sh" --file "$REPO_ROOT/scripts/windows/Onboarding-Smoke.ps1" -Action stage -RootDirectory "$windows_root"
"$REPO_ROOT/scripts/windows/copy-to-vm.sh" "$(dirname "$state")/local-token.json" "$windows_root\\token.json"
# Mark before enrollment, so a partial service install is also safely uninstalled.
windows_installed=true
"$REPO_ROOT/scripts/windows/run-powershell.sh" --file "$REPO_ROOT/scripts/windows/Onboarding-Smoke.ps1" -Action install -RootDirectory "$windows_root" -BackendUri "$agent_server" -TokenFile "$windows_root\\token.json" -AgentName "Local download smoke $suffix"
read -r windows_sql_host windows_sql_port < <(python3 - "$REPO_ROOT/scripts/lib/local-env.py" "$BP_CREDENTIAL_FILE" "$work/windows-sql.json" <<'PY'
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
"$REPO_ROOT/scripts/windows/copy-to-vm.sh" "$work/windows-sql.json" "$windows_root\\sql-secret.json"
"$REPO_ROOT/scripts/windows/run-powershell.sh" --file "$REPO_ROOT/scripts/windows/Download-Smoke.ps1" -RootDirectory "$windows_root" -DatabaseSecretFile "$windows_root\\sql-secret.json"
rm -- "$work/windows-sql.json"
"${fixture[@]}" backup --provider local --agent-name "Local download smoke $suffix" --sql-host "$windows_sql_host" --sql-port "$windows_sql_port"
"${fixture[@]}" verify --provider local --output "$work/local.bak"
printf 'SMOKE_FRONTEND_URL=%s\nSMOKE_API_URL=%s\nSMOKE_CERTIFICATE_FILE=%s\nSMOKE_DOWNLOAD_STATE_FILE=%s\nSMOKE_DOWNLOAD_OUTPUT_DIRECTORY=%s\nSMOKE_WINDOWS_ROOT=%s\n' "$frontend" "$server" "$certificate" "$state" "$work" "$windows_root"
printf 'DOWNLOAD_SMOKE_READY\n'
while kill -0 "$agent_pid" 2>/dev/null && kill -0 "$stack_pid" 2>/dev/null; do sleep 1; done
fail 'A download smoke process exited.'
