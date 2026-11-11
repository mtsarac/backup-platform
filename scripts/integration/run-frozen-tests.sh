#!/usr/bin/env bash
# On-demand run of the tests of frozen features (Windows VSS, Linux snapshots, size anomaly, external secrets, SMB).
# The default runs exclude them (backup-backend/tests/default.runsettings); the integration part starts its own PostgreSQL with Testcontainers.
set +x
set -euo pipefail
# shellcheck source=scripts/lib/common.sh
source "$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")/../lib" && pwd)/common.sh"

build_args=()
while (($#)); do
    case "$1" in
        --no-build) build_args+=(--no-build) ;;
        -h|--help)
            printf '%s\n' 'Usage: run-frozen-tests.sh [--no-build]' \
                'Runs every Category=Frozen test of the unit, agent and integration projects in Release (Windows VSS: run tests/windows-tests on Windows with the same filter).'
            exit 0 ;;
        *) fail 'Unknown option; use --help.' ;;
    esac
    shift
done
load_local_env
require_command dotnet
cd -- "$REPO_ROOT/backup-backend"
for project in unit agent integration; do
    run_dotnet_tests "tests/${project}-tests" -c Release "${build_args[@]}" -p:IncludeFrozenTests=true --filter 'Category=Frozen' -- xUnit.MaxParallelThreads=2
done
