#!/usr/bin/env bash
# M6-only verification (support access); never runs the full backend/Agent/PostgreSQL suites.
set +x
set -euo pipefail
# shellcheck source=scripts/lib/common.sh
source "$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")/../lib" && pwd)/common.sh"
unit=false postgres=false selected=false
build_args=()
while (($#)); do
    case "$1" in
        --unit) unit=true; selected=true ;;
        --postgres) postgres=true; selected=true ;;
        --no-build) build_args+=(--no-build) ;;
        -h|--help)
            printf '%s\n' 'Usage: run-support-tests.sh [--unit] [--postgres] [--no-build]' \
                'Default: support session domain/unit, and the PostgreSQL support lifecycle, data-access and restore subsets only.'
            exit 0 ;;
        *) fail 'Unknown option; use --help.' ;;
    esac
    shift
done
if [[ $selected == false ]]; then unit=true; postgres=true; fi
load_local_env
require_command dotnet
if [[ $unit == true ]]; then
    run_dotnet_tests "$REPO_ROOT/backup-backend/tests/unit-tests" -c Release "${build_args[@]}" --filter 'FullyQualifiedName~SupportAccessSessionTests'
fi
if [[ $postgres == true ]]; then
    run_dotnet_tests "$REPO_ROOT/backup-backend/tests/integration-tests" -c Release "${build_args[@]}" \
        --filter 'FullyQualifiedName~IntegrationTests.SupportSession|FullyQualifiedName~IntegrationTests.SupportData|FullyQualifiedName~IntegrationTests.SupportRestore' -- xUnit.MaxParallelThreads=2
fi
