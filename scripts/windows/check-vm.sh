#!/usr/bin/env bash
set +x
set -euo pipefail
# shellcheck source=../lib/common.sh
source "$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")/../lib" && pwd)/common.sh"

arguments=(check)
while [[ $# -gt 0 ]]; do
    case $1 in
        --require-agent|--check-backend) arguments+=("$1"); shift ;;
        --backend-uri)
            [[ $# -ge 2 && -n $2 ]] || fail '--backend-uri requires an HTTPS URI.'
            arguments+=("$1" "$2"); shift 2 ;;
        -h|--help)
            printf '%s\n' 'Usage: check-vm.sh [--require-agent] [--check-backend] [--backend-uri HTTPS_URI]' \
                'Checks SSH, .NET runtime, SQL engine services and agent status without modifying the VM.'
            exit 0 ;;
        *) fail 'Unknown VM check option.' ;;
    esac
done
load_local_env
run_redacted "$REPO_ROOT/scripts/windows/run-powershell.sh" --command '
Write-Output "SSH reachable; Windows PowerShell version: $($PSVersionTable.PSVersion)"
if (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) { throw "dotnet is not available in the SSH session PATH." }
& dotnet --info
if ($LASTEXITCODE -ne 0) { throw "dotnet --info failed with exit code $LASTEXITCODE." }
$runtimes = @(& dotnet --list-runtimes)
if ($LASTEXITCODE -ne 0) { throw "dotnet --list-runtimes failed with exit code $LASTEXITCODE." }
$runtimes | Write-Output
if (-not ($runtimes | Where-Object { $_ -match "^Microsoft.NETCore.App " })) { throw "No Microsoft.NETCore.App runtime is installed." }
$sqlServices = @(Get-Service | Where-Object { $_.Name -eq "MSSQLSERVER" -or $_.Name.StartsWith("MSSQL$") })
if ($sqlServices.Count -eq 0) { throw "No SQL Server database engine service is installed." }
$sqlServices | Select-Object Name, Status | Format-Table -AutoSize
if (-not ($sqlServices | Where-Object { $_.Status -eq "Running" })) { throw "No SQL Server database engine service is running." }
'
run_redacted "$REPO_ROOT/scripts/windows/test-agent-service.sh" "${arguments[@]}"
