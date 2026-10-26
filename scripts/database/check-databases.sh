#!/usr/bin/env bash
set +x
set -euo pipefail
# shellcheck source=scripts/lib/common.sh
source "$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")/../lib" && pwd)/common.sh"
[[ $# == 0 ]] || fail 'Usage: check-databases.sh' 2
"${REPO_ROOT}/scripts/database/test-postgres.sh"
"${REPO_ROOT}/scripts/database/test-mssql.sh"
