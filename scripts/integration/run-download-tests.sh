#!/usr/bin/env bash
# M4-only verification; never runs the full backend/Agent/PostgreSQL suites.
set +x
set -euo pipefail
# shellcheck source=scripts/lib/common.sh
source "$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")/../lib" && pwd)/common.sh"
unit=false agent=false postgres=false selected=false
build_args=()
while (($#)); do
    case "$1" in
        --unit) unit=true; selected=true ;;
        --agent) agent=true; selected=true ;;
        --postgres) postgres=true; selected=true ;;
        --no-build) build_args+=(--no-build) ;;
        -h|--help)
            printf '%s\n' 'Usage: run-download-tests.sh [--unit] [--agent] [--postgres] [--no-build]' \
                'Default: download domain/unit, Agent download transport/worker, and PostgreSQL download + existing restore/Agent retention pin regression subsets only.'
            exit 0 ;;
        *) fail 'Unknown option; use --help.' ;;
    esac
    shift
done
if [[ $selected == false ]]; then unit=true; agent=true; postgres=true; fi
load_local_env
require_command dotnet
if [[ $unit == true ]]; then
    run_dotnet_tests "$REPO_ROOT/backup-backend/tests/unit-tests" -c Release "${build_args[@]}" --filter 'FullyQualifiedName~Download'
fi
if [[ $agent == true ]]; then
    run_dotnet_tests "$REPO_ROOT/backup-backend/tests/agent-tests" -c Release "${build_args[@]}" --filter 'FullyQualifiedName~Download|FullyQualifiedName~AgentCapabilityCatalogTests|FullyQualifiedName~DeleteReplicaCompositionTests' -- xUnit.MaxParallelThreads=2
fi
if [[ $postgres == true ]]; then
    run_dotnet_tests "$REPO_ROOT/backup-backend/tests/integration-tests" -c Release "${build_args[@]}" \
        --filter 'FullyQualifiedName~Downloads|FullyQualifiedName~RestoreRetentionPinTests|FullyQualifiedName~AgentReplicaDeletionRaceTests' -- xUnit.MaxParallelThreads=2
fi
