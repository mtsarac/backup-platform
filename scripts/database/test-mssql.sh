#!/usr/bin/env bash
set +x
set -euo pipefail
# shellcheck source=scripts/lib/common.sh
source "$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")/../lib" && pwd)/common.sh"
[[ $# == 0 ]] || fail "Usage: test-mssql.sh (configuration comes from environment/private file)" 2
load_local_env
printf 'MSSQL environment configured\n'
run_database_probe mssql
