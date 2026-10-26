#!/usr/bin/env bash
set +x
set -euo pipefail
# shellcheck source=scripts/lib/common.sh
source "$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")/../lib" && pwd)/common.sh"

load_local_env
require_command python3
transport="$REPO_ROOT/scripts/windows/ssh-vm.sh"
case ${1:-} in
    -h|--help)
        printf '%s\n' 'Usage: run-powershell.sh --command STRING | --file LOCALFILE [-- ARG...]' 'Commands are UTF-16LE encoded. Files stream via stdin into a disposable remote .ps1.'
        exit 0 ;;
    --command)
        [[ $# == 2 ]] || fail '--command requires exactly one PowerShell command string.'
        encoded=$(python3 - "$2" <<'PY'
import base64, sys
script = "$ErrorActionPreference='Stop'; $global:LASTEXITCODE=0; try { & {\n" + sys.argv[1] + "\n}; if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE } } catch { [Console]::Error.WriteLine($_.ToString()); exit 1 }"
print(base64.b64encode(script.encode('utf-16le')).decode('ascii'))
PY
)
        run_redacted "$transport" --command "powershell.exe -NoLogo -NoProfile -NonInteractive -EncodedCommand $encoded" ;;
    --file)
        [[ $# -ge 2 && -f $2 && -r $2 ]] || fail '--file requires a readable local PowerShell script.'
        local_file=$2
        shift 2
        [[ ${1:-} != -- ]] || shift
        encoded=$(python3 - "$@" <<'PY'
import base64, re, sys
quote = lambda value: "'" + value.replace("'", "''") + "'"
args = ' '.join(value if re.fullmatch(r'-[A-Za-z][A-Za-z0-9]*', value) else quote(value) for value in sys.argv[1:])
script = """$ErrorActionPreference='Stop'; $global:LASTEXITCODE=0; $path=$null; $code=0
try {
    $path=Join-Path ([IO.Path]::GetTempPath()) ('backup-local-' + [Guid]::NewGuid().ToString('N') + '.ps1')
    # Windows OpenSSH does not reliably signal stdin EOF to Windows PowerShell.
    # The sender frames the payload as one Base64 line, so do not wait for EOF.
    $content=[Convert]::FromBase64String([Console]::In.ReadLine())
    [IO.File]::WriteAllBytes($path, $content)
    & $path %s
    if ($LASTEXITCODE -ne 0) { $code=$LASTEXITCODE }
} catch { [Console]::Error.WriteLine($_.ToString()); $code=1 }
finally { if ($path -and (Test-Path -LiteralPath $path)) { Remove-Item -LiteralPath $path -Force } }
exit $code
""" % args
print(base64.b64encode(script.encode('utf-16le')).decode('ascii'))
PY
)
        python3 -c 'import base64,sys; print(base64.b64encode(open(sys.argv[1], "rb").read()).decode("ascii"))' "$local_file" |
            run_redacted "$transport" --command "powershell.exe -NoLogo -NoProfile -NonInteractive -ExecutionPolicy Bypass -EncodedCommand $encoded" ;;
    *) fail 'Usage: run-powershell.sh --command STRING | --file LOCALFILE [-- ARG...]' ;;
esac
