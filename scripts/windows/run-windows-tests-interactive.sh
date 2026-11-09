#!/usr/bin/env bash
# Runs backup-backend/tests/windows-tests on the Windows VM inside the INTERACTIVE console session.
# Over a plain SSH session DPAPI (CurrentUser) is refused, so DPAPI-backed tests fail for environmental reasons only.
# The tests are built here (Linux can compile the net10.0-windows project), shipped as a zip, and started by a one-shot scheduled task
# registered for the logged-on console user with -LogonType Interactive; that is what puts the process in the console session.
#
# Usage: run-windows-tests-interactive.sh [--filter VSTEST_FILTER] [--results-dir LOCALDIR] [--plain-ssh]
# Needs: the VM's console user logged on (check: ssh virtual-win11 query user), the SDK at C:\dotnet-sdk (dotnet test needs it; the VM
# only has the runtime in Program Files), ssh alias WINDOWS_SSH_TARGET (default virtual-win11), python3 (zip), dotnet.
# Everything it creates on the VM (C:\e2e\wintest-<id>, the scheduled task) is removed afterwards, also on failure.
set -euo pipefail
cd "$(dirname "${BASH_SOURCE[0]}")/../.."

filter=''
results=''
plain=0
while [[ $# -gt 0 ]]; do
    case $1 in
        --filter) filter=${2:?--filter needs a VSTest filter}; shift 2 ;;
        --results-dir) results=${2:?--results-dir needs a directory}; shift 2 ;;
        --plain-ssh) plain=1; shift ;;
        -h|--help) sed -n '2,11p' "${BASH_SOURCE[0]}"; exit 0 ;;
        *) echo "Unknown option: $1" >&2; exit 2 ;;
    esac
done

target=${WINDOWS_SSH_TARGET:-virtual-win11}
sdk='C:\dotnet-sdk\dotnet.exe'
id=$(python3 -c 'import uuid; print(uuid.uuid4().hex[:8])')
remote="C:\\e2e\\wintest-$id"
task="bp-wintest-$id"
work=$(mktemp -d)
vm() { ssh -o BatchMode=yes "$target" "$@"; }
ps() { vm "powershell -NoLogo -NoProfile -NonInteractive -ExecutionPolicy Bypass -File $remote\\$1"; }
cleanup() {
    vm "schtasks /delete /tn $task /f >nul 2>&1 & rmdir /s /q $remote" >/dev/null 2>&1 || true
    rm -rf "$work"
}
trap cleanup EXIT

dotnet build backup-backend/tests/windows-tests -c Release --nologo -v quiet
out=backup-backend/tests/windows-tests/bin/Release/net10.0-windows
python3 - "$out" "$work/tests.zip" <<'PY'
import os, sys, zipfile
src, dst = sys.argv[1:]
with zipfile.ZipFile(dst, 'w', zipfile.ZIP_DEFLATED) as z:
    for base, _, files in os.walk(src):
        for f in files:
            p = os.path.join(base, f)
            z.write(p, os.path.relpath(p, src).replace(os.sep, '/'))
PY

cat > "$work/run.ps1" <<PS
# No Stop here: native stderr redirected with *>> would otherwise abort the run before 'done' is written.
\$root = '$remote'
\$dll = Join-Path \$root 'tests\\BackupPlatform.Agent.Platform.Windows.Tests.dll'
\$session = (Get-Process -Id \$PID).SessionId
"whoami=\$(whoami) session=\$session elevated=\$(([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole('Administrator'))" | Out-File (Join-Path \$root 'test.log') -Encoding utf8
\$targs = @('test', \$dll, '--logger', 'trx;LogFileName=result.trx', '--results-directory', (Join-Path \$root 'results'), '--logger', 'console;verbosity=normal')
if ('$filter') { \$targs += @('--filter', '$filter') }
& '$sdk' @targs *>> (Join-Path \$root 'test.log')
"exit=\$LASTEXITCODE" | Out-File (Join-Path \$root 'done') -Encoding ascii
PS

cat > "$work/start.ps1" <<PS
\$ErrorActionPreference = 'Stop'
\$user = (Get-CimInstance Win32_ComputerSystem).UserName
if (-not \$user) { throw 'No interactive console user is logged on; log in on the VM console first.' }
\$a = New-ScheduledTaskAction -Execute powershell.exe -Argument '-NoProfile -ExecutionPolicy Bypass -File $remote\\run.ps1'
\$p = New-ScheduledTaskPrincipal -UserId \$user -LogonType Interactive -RunLevel Highest
Register-ScheduledTask -TaskName '$task' -Action \$a -Principal \$p -Force | Out-Null
Start-ScheduledTask -TaskName '$task'
\$end = (Get-Date).AddMinutes(15)
while (-not (Test-Path '$remote\\done') -and (Get-Date) -lt \$end) { Start-Sleep 2 }
if (-not (Test-Path '$remote\\done')) { throw 'Timed out waiting for the interactive test run.' }
PS

vm "mkdir $remote"
scp -q -o BatchMode=yes "$work/tests.zip" "$work/run.ps1" "$work/start.ps1" "$target:${remote//\\//}/"
vm "powershell -NoLogo -NoProfile -NonInteractive -Command Expand-Archive -LiteralPath $remote\\tests.zip -DestinationPath $remote\\tests -Force"
status=0
if (( plain )); then
    # Same dll, but in the SSH network session (no DPAPI): control run only.
    vm "$sdk test $remote\\tests\\BackupPlatform.Agent.Platform.Windows.Tests.dll --logger \"trx;LogFileName=result.trx\" --results-directory $remote\\results ${filter:+--filter \"$filter\"} > $remote\\test.log 2>&1 && echo exit=0 > $remote\\done || echo exit=1 > $remote\\done" || status=$?
else
    ps start.ps1 || status=$?
fi
vm "type $remote\\test.log" | tr -d '\r' || true
vm "type $remote\\done" | tr -d '\r' | tee "$work/done" || true
if [[ -n $results ]]; then
    mkdir -p "$results"
    scp -q -o BatchMode=yes "$target:${remote//\\//}/results/result.trx" "$results/" || true
fi
grep -qx 'exit=0' "$work/done" || status=1
exit "$status"
