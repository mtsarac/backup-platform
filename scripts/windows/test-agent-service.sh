#!/usr/bin/env bash
set +x
set -euo pipefail
# shellcheck source=scripts/lib/common.sh
source "$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")/../lib" && pwd)/common.sh"

usage() {
    printf '%s\n' 'Usage: test-agent-service.sh ACTION [OPTIONS]' \
        'Actions: status, start, stop, restart, install, uninstall, logs, check' \
        'install: --binary-directory REMOTE_DIR --backend-uri HTTPS_URI [--store-directory REMOTE_DIR]' \
        'check: [--require-agent] [--check-backend] [--backend-uri HTTPS_URI]' \
        'status: [--require-agent]   logs: [--max-events COUNT]' \
        'Install/uninstall reuse the repository deployment scripts; uninstall always preserves identity.'
}
[[ ${1:-} != -h && ${1:-} != --help ]] || { usage; exit 0; }
[[ $# -gt 0 ]] || { usage >&2; exit 1; }
action=$1
shift
case $action in status|start|stop|restart|install|uninstall|logs|check) ;; *) fail 'Unknown agent service action.' ;; esac
binary_directory='' backend_uri='' store_directory='' maximum_events=100
require_agent=false check_backend=false
while [[ $# -gt 0 ]]; do
    case $1 in
        --binary-directory|--backend-uri|--store-directory|--max-events)
            [[ $# -ge 2 && -n $2 ]] || fail "$1 requires a value."
            case $1 in
                --binary-directory) [[ $action == install ]] || fail '--binary-directory is only valid for install.'; binary_directory=$2 ;;
                --backend-uri) [[ $action == install || $action == check ]] || fail '--backend-uri is only valid for install/check.'; backend_uri=$2 ;;
                --store-directory) [[ $action == install ]] || fail '--store-directory is only valid for install; uninstall never removes identity.'; store_directory=$2 ;;
                --max-events) [[ $action == logs ]] || fail '--max-events is only valid for logs.'; maximum_events=$2 ;;
            esac
            shift 2 ;;
        --require-agent)
            [[ $action == check || $action == status ]] || fail '--require-agent is only valid for check/status.'
            require_agent=true; shift ;;
        --check-backend)
            [[ $action == check ]] || fail '--check-backend is only valid for check.'
            check_backend=true; shift ;;
        *) fail 'Unknown agent service option.' ;;
    esac
done
load_local_env
powershell="$REPO_ROOT/scripts/windows/run-powershell.sh"
case $action in
    install)
        [[ -n $binary_directory && -n $backend_uri ]] || fail 'install requires --binary-directory and --backend-uri.'
        python3 - "$backend_uri" <<'PY' || fail 'Backend URI must be an absolute HTTPS address without embedded credentials.'
import sys, urllib.parse
try:
    uri = urllib.parse.urlsplit(sys.argv[1])
    valid = uri.scheme.lower() == 'https' and bool(uri.hostname) and not uri.username and not uri.password
except ValueError:
    valid = False
sys.exit(0 if valid else 1)
PY
        arguments=(-BinaryDirectory "$binary_directory" -BackendUri "$backend_uri")
        [[ -z $store_directory ]] || arguments+=(-StoreDirectory "$store_directory")
        run_redacted "$powershell" --file "$REPO_ROOT/backup-backend/deploy/windows/Install-BackupAgentService.ps1" -- "${arguments[@]}" ;;
    uninstall)
        run_redacted "$powershell" --file "$REPO_ROOT/backup-backend/deploy/windows/Uninstall-BackupAgentService.ps1" ;;
    *)
        [[ $maximum_events =~ ^[1-9][0-9]{0,3}$ || $maximum_events == 10000 ]] || fail '--max-events must be between 1 and 10000.'
        arguments=(-Action "$action" -MaximumEvents "$maximum_events")
        [[ $require_agent == false ]] || arguments+=(-RequireAgent)
        [[ $check_backend == false ]] || arguments+=(-CheckBackend)
        [[ -z $backend_uri ]] || arguments+=(-BackendUri "$backend_uri")
        run_redacted "$powershell" --file "$REPO_ROOT/scripts/windows/Agent-Service.ps1" -- "${arguments[@]}" ;;
esac
