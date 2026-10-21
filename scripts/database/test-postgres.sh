#!/usr/bin/env bash
set +x
set -euo pipefail
# shellcheck source=scripts/lib/common.sh
source "$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")/../lib" && pwd)/common.sh"
[[ $# == 0 ]] || fail "Usage: test-postgres.sh (configuration comes from environment/private file)" 2
load_local_env
printf 'PostgreSQL environment configured\n'
run_database_probe postgres
