#!/usr/bin/env bash
set +x
set -euo pipefail
# shellcheck source=../lib/common.sh
source "$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")/../lib" && pwd)/common.sh"

connectivity=false
build_args=()
while (($#)); do
    case "$1" in
        --connectivity) connectivity=true ;;
        --no-build) build_args+=(--no-build) ;;
        -h|--help)
            printf '%s\n' 'Usage: run-backend-tests.sh [--connectivity] [--no-build]' 'Runs Release unit, architecture, and fast agent tests; --connectivity runs the full agent suite.'
            exit 0 ;;
        *) fail 'Unknown option; use --help.' ;;
    esac
    shift
done
load_local_env
require_command dotnet
cd -- "$REPO_ROOT/backup-backend"
run_redacted dotnet test tests/unit-tests -c Release "${build_args[@]}"
run_redacted dotnet test tests/architecture-tests -c Release "${build_args[@]}"
agent_filter=()
if [[ "$connectivity" == false ]]; then
    agent_filter=(--filter 'FullyQualifiedName!~ConnectivityServiceTests')
fi
run_redacted dotnet test tests/agent-tests -c Release "${build_args[@]}" "${agent_filter[@]}" -- xUnit.MaxParallelThreads=2
