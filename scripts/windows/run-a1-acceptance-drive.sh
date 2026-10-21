#!/usr/bin/env bash
# A1: dev Windows Agent -> Google Drive acceptance (see a1-windows-drive-acceptance.py). Needs the dev stack with BP_DEV_PUBLIC_HOST and scripts/windows/dev-windows-agent.sh install.
set +x
set -euo pipefail
# shellcheck source=scripts/lib/common.sh
source "$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")/../lib" && pwd)/common.sh"
BP_MSSQL_PROFILE=windows load_local_env
require_command python3
require_command docker
run_redacted python3 -u "$REPO_ROOT/scripts/windows/a1-windows-drive-acceptance.py" "$@"
