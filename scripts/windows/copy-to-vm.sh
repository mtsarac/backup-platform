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
# PowerShell Console.ReadLine cannot reliably receive large stdin payloads on Windows OpenSSH.
# Native SFTP transfers bytes; PowerShell only validates and atomically commits the owned staging file.
metadata=$(python3 - "$local_file" "$2" "$force" <<'PY'
import base64, hashlib, json, sys, uuid
with open(sys.argv[1], 'rb') as handle:
    digest = hashlib.file_digest(handle, 'sha256').hexdigest()
value = {'destination': sys.argv[2], 'force': sys.argv[3] == 'true',
         'temporaryName': 'backup-copy-' + uuid.uuid4().hex + '.tmp', 'sha256': digest}
print(base64.b64encode(json.dumps(value).encode()).decode('ascii'))
PY
)
upload_path=$(python3 - "$metadata" <<'PY'
import base64, json, ntpath, sys
value = json.loads(base64.b64decode(sys.argv[1]))
print(ntpath.join(ntpath.dirname(value['destination']), value['temporaryName']).replace('\\', '/'))
PY
)
powershell="$REPO_ROOT/scripts/windows/run-powershell.sh"
state="\$state=[Text.Encoding]::UTF8.GetString([Convert]::FromBase64String('$metadata')) | ConvertFrom-Json"
run_redacted "$powershell" --command "$state
\$parent=Split-Path -Parent \$state.destination
if (-not \$parent -or -not (Test-Path -LiteralPath \$parent -PathType Container)) { throw 'The destination parent directory must exist.' }
if (Test-Path -LiteralPath \$state.destination -PathType Container) { throw 'The destination must be a file, not a directory.' }
if ((Test-Path -LiteralPath \$state.destination) -and -not \$state.force) { throw 'Destination exists; use --force to replace it.' }
if (Test-Path -LiteralPath (Join-Path \$parent \$state.temporaryName)) { throw 'The transfer staging path unexpectedly exists.' }"
if ! run_redacted "$REPO_ROOT/scripts/windows/ssh-vm.sh" --upload "$local_file" "$upload_path"; then
    run_redacted "$powershell" --command "$state
Remove-Item -LiteralPath (Join-Path (Split-Path -Parent \$state.destination) \$state.temporaryName) -Force -ErrorAction SilentlyContinue"
    fail 'Native SFTP upload failed; the destination was not changed.'
fi
run_redacted "$powershell" --command "$state
\$temporary=Join-Path (Split-Path -Parent \$state.destination) \$state.temporaryName
try {
    if ((Get-FileHash -LiteralPath \$temporary -Algorithm SHA256).Hash -ne \$state.sha256) { throw 'Transferred file checksum does not match; destination was not changed.' }
    if ((Test-Path -LiteralPath \$state.destination) -and -not \$state.force) { throw 'Destination appeared during transfer; use --force to replace it.' }
    Move-Item -LiteralPath \$temporary -Destination \$state.destination -Force:\$state.force
    Write-Output 'File copied and SHA256 verified.'
} finally { if (Test-Path -LiteralPath \$temporary) { Remove-Item -LiteralPath \$temporary -Force } }"
