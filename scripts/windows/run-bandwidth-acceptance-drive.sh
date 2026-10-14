#!/usr/bin/env bash
# Faz 2A: upload bandwidth limits, dev Windows Agent (+ dev Linux Agent) -> Google Drive acceptance (see bandwidth-drive-acceptance.py).
# Needs the dev stack with BP_DEV_PUBLIC_HOST, scripts/windows/dev-windows-agent.sh install and (for the tenant scenario) scripts/dev/agent.sh start.
# Usage: run-bandwidth-acceptance-drive.sh --preflight | [--scenarios baseline,low,medium,tenant,cancel,retry] [--help for every size/limit/timeout option]
set +x
set -euo pipefail
# shellcheck source=../lib/common.sh
source "$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")/../lib" && pwd)/common.sh"
BP_MSSQL_PROFILE=windows load_local_env
require_command python3
require_command docker
run_redacted python3 -u "$REPO_ROOT/scripts/windows/bandwidth-drive-acceptance.py" "$@"
