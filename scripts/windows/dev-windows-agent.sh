#!/usr/bin/env bash
# Installs / removes a real Windows Agent service on the Windows VM, enrolled into the persistent dev stack's tenant
# (scripts/dev/start.sh with BP_DEV_PUBLIC_HOST=<libvirt host IPv4>). Reuses the owned smoke helpers: Onboarding-Smoke.ps1 and Download-Smoke.ps1.
#   dev-windows-agent.sh install     publish win-x64, enroll as 'dev-windows-agent', configure SQL reference sql_smoke + local storage
#   dev-windows-agent.sh uninstall   remove the owned service, identity, trusted dev certificate and files
set +x
set -euo pipefail
# shellcheck source=scripts/dev/common.sh
source "$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")/../dev" && pwd)/common.sh"
action=${1:-}
[[ $action == install || $action == uninstall ]] || fail 'Usage: dev-windows-agent.sh install|uninstall'
: "${BP_DEV_PUBLIC_HOST:?Start the dev stack with BP_DEV_PUBLIC_HOST=<IPv4 reachable from the VM>.}"
BP_MSSQL_PROFILE=windows load_local_env
dev_prepare_state
state="$BP_DEV_HOME/dev-windows-agent.json"
ps="$REPO_ROOT/scripts/windows/run-powershell.sh"
smoke="$REPO_ROOT/scripts/windows/Onboarding-Smoke.ps1"
copy="$REPO_ROOT/scripts/windows/copy-to-vm.sh"
case $action in
    uninstall)
        [[ -f $state ]] || fail 'No dev Windows agent state; nothing to remove.'
        root=$(python3 -c 'import json,sys; print(json.load(open(sys.argv[1]))["root"])' "$state")
        "$ps" --file "$smoke" -Action uninstall -RootDirectory "$root"
        "$ps" --file "$smoke" -Action cleanup -RootDirectory "$root"
        rm -f -- "$state"
        printf 'Dev Windows agent removed.\n' ;;
    install)
        [[ ! -f $state ]] || fail 'A dev Windows agent is already installed; uninstall it first.'
        make_runtime_dir
        work=$RUNTIME_DIR
        root="C:\\ProgramData\\BackupAgentSmoke-$(python3 -c 'import uuid; print(uuid.uuid4().hex[:12])')"
        run_redacted dotnet publish "$REPO_ROOT/backup-backend/src/hosts/agent" -c Release -r win-x64 --self-contained true -p:AgentPlatform=Windows -o "$work/agent" >/dev/null
        python3 - "$work/agent" "$work/agent.zip" <<'PY'
from pathlib import Path
import sys, zipfile
root = Path(sys.argv[1])
with zipfile.ZipFile(sys.argv[2], 'w', zipfile.ZIP_DEFLATED) as archive:
    for file in root.rglob('*'):
        if file.is_file(): archive.write(file, file.relative_to(root))
PY
        python3 - "$state" "$root" <<'PY'
import json, os, sys
fd = os.open(sys.argv[1], os.O_WRONLY | os.O_CREAT | os.O_EXCL, 0o600)
with os.fdopen(fd, 'w') as handle: json.dump({'root': sys.argv[2]}, handle)
PY
        "$ps" --file "$smoke" -Action prepare -RootDirectory "$root"
        "$copy" "$work/agent.zip" "$root\\agent.zip"
        "$copy" "$BP_DEV_HOME/cert.pem" "$root\\certificate.pem"
        "$ps" --file "$smoke" -Action stage -RootDirectory "$root"
        python3 "$REPO_ROOT/scripts/dev/bootstrap.py" enrollment-token --accounts-file "$BP_DEV_ACCOUNTS_FILE" --server "$BP_DEV_API_TLS_URL" \
            --certificate "$BP_DEV_HOME/cert.pem" --output "$work/token.json"
        "$copy" "$work/token.json" "$root\\token.json"
        "$ps" --file "$smoke" -Action install -RootDirectory "$root" -BackendUri "https://$BP_DEV_PUBLIC_HOST:$BP_DEV_API_HTTPS_PORT" \
            -TokenFile "$root\\token.json" -AgentName dev-windows-agent
        "$ps" --file "$REPO_ROOT/scripts/windows/Download-Smoke.ps1" -RootDirectory "$root"
        printf 'Dev Windows agent installed (root %s).\n' "$root" ;;
esac
