#!/usr/bin/env bash
set +x
set -euo pipefail
# shellcheck source=scripts/lib/common.sh
source "$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")/../lib" && pwd)/common.sh"

connectivity=false
project=
filter=
build_args=()
while (($#)); do
    case "$1" in
        --connectivity) connectivity=true ;;
        --no-build) build_args+=(--no-build) ;;
        --project)
            (($# >= 2)) || fail '--project needs unit, architecture, or agent.'
            case "$2" in
                unit|architecture|agent) project=$2 ;;
                *) fail '--project must be unit, architecture, or agent.' ;;
            esac
            shift ;;
        --filter)
            (($# >= 2)) && [[ -n $2 && $2 != -* ]] || fail '--filter needs a nonempty dotnet test filter expression.'
            filter=$2
            shift ;;
        -h|--help)
            printf '%s\n' 'Usage: run-backend-tests.sh [--connectivity] [--no-build]' \
                '       run-backend-tests.sh --project unit|architecture|agent [--filter EXPRESSION] [--no-build]' \
                'Default fast feedback: all Release unit and architecture tests, plus agent tests excluding ConnectivityServiceTests; no PostgreSQL tests.' \
                '--connectivity includes the full agent suite in the default selection.' \
                '--project builds/tests only the named project and its dependencies; --filter is an explicit selection within that project.' \
                'A named agent project includes connectivity tests unless explicitly filtered; no automatic safety-test exclusions.' \
                '--no-build reuses existing Release output only when explicitly requested; the caller must ensure it is current.'
            exit 0 ;;
        *) fail 'Unknown option; use --help.' ;;
    esac
    shift
done
[[ -z $filter || -n $project ]] || fail '--filter requires --project so the selection is explicit.'
[[ -z $project || $connectivity == false ]] || fail '--connectivity applies to the default selection; --project agent already includes connectivity tests.'
load_local_env
require_command dotnet
cd -- "$REPO_ROOT/backup-backend"
projects=(unit architecture agent)
[[ -z $project ]] || projects=("$project")
for selected in "${projects[@]}"; do
    test_args=(-c Release "${build_args[@]}")
    if [[ -n $filter ]]; then
        test_args+=(--filter "$filter")
    elif [[ $selected == agent && -z $project && $connectivity == false ]]; then
        test_args+=(--filter 'FullyQualifiedName!~ConnectivityServiceTests')
    fi
    if [[ $selected == agent ]]; then
        test_args+=(-- xUnit.MaxParallelThreads=2)
    fi
    run_dotnet_tests "tests/${selected}-tests" "${test_args[@]}"
done
