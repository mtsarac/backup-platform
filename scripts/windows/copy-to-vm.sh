#!/usr/bin/env bash
set +x
set -euo pipefail
# shellcheck source=../lib/common.sh
source "$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")/../lib" && pwd)/common.sh"

load_local_env
require_command python3
if [[ ${1:-} == -h || ${1:-} == --help ]]; then
    printf '%s\n' 'Usage: copy-to-vm.sh [--force] LOCALFILE REMOTEFILE' 'Copies one file over the shared SSH transport; the remote parent directory must exist.'
    exit 0
fi
force=false
if [[ ${1:-} == --force ]]; then force=true; shift; fi
[[ $# == 2 && -f $1 && -r $1 ]] || fail 'Usage: copy-to-vm.sh [--force] LOCALFILE REMOTEFILE'
local_file=$1
encoded=$(python3 - "$2" "$force" <<'PY'
import base64, sys
quote = lambda value: "'" + value.replace("'", "''") + "'"
script = """$ErrorActionPreference='Stop'; $temporary=$null
try {
    $destination=%s
    $parent=Split-Path -Parent $destination
    if (-not $parent -or -not (Test-Path -LiteralPath $parent -PathType Container)) { throw 'The destination parent directory must exist.' }
    if (Test-Path -LiteralPath $destination -PathType Container) { throw 'The destination must be a file, not a directory.' }
    if ((Test-Path -LiteralPath $destination) -and -not $%s) { throw 'Destination exists; use --force to replace it.' }
    $temporary=Join-Path $parent ('backup-copy-' + [Guid]::NewGuid().ToString('N') + '.tmp')
    [IO.File]::WriteAllBytes($temporary, [Convert]::FromBase64String([Console]::In.ReadToEnd()))
    Move-Item -LiteralPath $temporary -Destination $destination -Force:$%s
    Write-Output 'File copied.'
} catch { [Console]::Error.WriteLine($_.ToString()); exit 1 }
finally { if ($temporary -and (Test-Path -LiteralPath $temporary)) { Remove-Item -LiteralPath $temporary -Force } }
""" % (quote(sys.argv[1]), sys.argv[2], sys.argv[2])
print(base64.b64encode(script.encode('utf-16le')).decode('ascii'))
PY
)
python3 -c 'import base64,sys; base64.encode(open(sys.argv[1], "rb"), sys.stdout.buffer)' "$local_file" |
    run_redacted "$REPO_ROOT/scripts/windows/ssh-vm.sh" --command "powershell.exe -NoLogo -NoProfile -NonInteractive -EncodedCommand $encoded"
