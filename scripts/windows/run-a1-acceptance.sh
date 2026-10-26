#!/usr/bin/env bash
# A1 Windows lifecycle acceptance against a running scripts/frontend/run-download-smoke.sh stack (see a1-windows-acceptance.py).
set +x
set -euo pipefail
# shellcheck source=scripts/lib/common.sh
source "$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")/../lib" && pwd)/common.sh"
[[ $# -ge 4 ]] || fail 'Usage: run-a1-acceptance.sh SERVER CERTIFICATE STATE_FILE WINDOWS_ROOT [extra args]'
server=$1 certificate=$2 state=$3 windows_root=$4
shift 4
BP_MSSQL_PROFILE=windows load_local_env
require_command python3
require_command docker
run_redacted python3 -u "$REPO_ROOT/scripts/windows/a1-windows-acceptance.py" --server "$server" \
    --certificate "$certificate" --state "$state" --windows-root "$windows_root" "$@"
