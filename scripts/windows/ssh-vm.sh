#!/usr/bin/env bash
set +x
set -euo pipefail
# shellcheck source=../lib/common.sh
source "$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")/../lib" && pwd)/common.sh"

load_local_env
require_command ssh
options=(-o BatchMode=yes -o StrictHostKeyChecking=yes -o "ConnectTimeout=${WINDOWS_SSH_CONNECT_TIMEOUT:-10}" -p "${WINDOWS_SSH_PORT:-22}")
while [[ ${1:-} == --ssh-option ]]; do
    [[ $# -ge 2 ]] || fail '--ssh-option requires an OpenSSH option.'
    case $2 in
        BatchMode=*|StrictHostKeyChecking=*|UserKnownHostsFile=*|GlobalKnownHostsFile=*|PasswordAuthentication=*|KbdInteractiveAuthentication=*)
            fail 'Authentication and host-key safety options cannot be overridden.' ;;
    esac
    options+=(-o "$2")
    shift 2
done
# Prefer the configured OpenSSH alias/key; an explicit user selects the host fallback.
target=${WINDOWS_SSH_TARGET:-virtual-win11}
if [[ $target == virtual-win11 && -n ${WINDOWS_SSH_USER:-} ]]; then
    target="${WINDOWS_SSH_USER}@${WINDOWS_SSH_HOST:-192.168.122.218}"
fi
[[ $target != -* && $target != *$'\n'* ]] || fail 'Invalid WINDOWS_SSH_TARGET.'
case ${1:-} in
    -h|--help)
        printf '%s\n' 'Usage: ssh-vm.sh [--ssh-option KEY=VALUE] [--command RAW_COMMAND | -- ARGV...]' 'No command opens an interactive SSH session. ARGV executes through PowerShell with literal arguments.'
        exit 0 ;;
    --command)
        [[ $# == 2 ]] || fail '--command requires exactly one remote command string.'
        run_redacted ssh "${options[@]}" "$target" "$2" ;;
    '') run_redacted ssh "${options[@]}" "$target" ;;
    *)
        [[ $1 != -- ]] || shift
        [[ $# -gt 0 ]] || fail 'Provide a command after --.'
        require_command python3
        encoded=$(python3 - "$@" <<'PY'
import base64, sys
quote = lambda value: "'" + value.replace("'", "''") + "'"
command = "$ErrorActionPreference='Stop'; $global:LASTEXITCODE=0; try { & " + ' '.join(map(quote, sys.argv[1:])) + "; if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE } } catch { [Console]::Error.WriteLine($_.ToString()); exit 1 }"
print(base64.b64encode(command.encode('utf-16le')).decode('ascii'))
PY
)
        run_redacted ssh "${options[@]}" "$target" "powershell.exe -NoLogo -NoProfile -NonInteractive -EncodedCommand $encoded" ;;
esac
