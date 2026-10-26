#!/usr/bin/env bash
# Calls the real CLI, loading a one-time token from a protected disposable JSON file.
set +x
set -euo pipefail
# shellcheck source=scripts/lib/common.sh
source "$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")/../lib" && pwd)/common.sh"
usage() {
    printf '%s\n' 'Usage: enroll-agent.sh --host EXECUTABLE --server HTTPS_ORIGIN --token-file JSON --store-directory DIRECTORY [--name NAME] [--certificate PEM]' \
        'Token JSON must be current-user-owned mode 0600. No secret is printed. Certificate adds an explicit test trust anchor via SSL_CERT_FILE; validation is not disabled.'
}
host='' server='' token_file='' store='' name='' certificate=''
while (($#)); do
    case "$1" in
        --host|--server|--token-file|--store-directory|--name|--certificate)
            [[ $# -ge 2 && -n $2 ]] || fail "$1 requires a value."
            case "$1" in
                --host) host=$2 ;; --server) server=$2 ;; --token-file) token_file=$2 ;;
                --store-directory) store=$2 ;; --name) name=$2 ;; --certificate) certificate=$2 ;;
            esac
            shift 2 ;;
        -h|--help) usage; exit 0 ;;
        *) fail 'Unknown option; use --help.' ;;
    esac
done
[[ -x $host && -n $server && -n $token_file && -n $store ]] || { usage >&2; exit 2; }
require_command python3
token=$(python3 - "$token_file" <<'PY'
import json, os, stat, sys
try:
    fd = os.open(sys.argv[1], os.O_RDONLY | os.O_NOFOLLOW)
    with os.fdopen(fd) as handle:
        info = os.fstat(handle.fileno())
        if not stat.S_ISREG(info.st_mode) or info.st_uid != os.getuid() or stat.S_IMODE(info.st_mode) != 0o600:
            raise ValueError()
        value = json.load(handle)['token']
        if not isinstance(value, str) or not value or any(c.isspace() for c in value):
            raise ValueError()
        print(value)
except (OSError, ValueError, KeyError, TypeError):
    print('Token file must be an owner-only regular JSON file containing a token (value withheld).', file=sys.stderr)
    sys.exit(2)
PY
)
export BP_SMOKE_ENROLLMENT_TOKEN=$token
if [[ -n $certificate ]]; then
    [[ -r $certificate ]] || fail 'Certificate file is not readable.'
    export SSL_CERT_FILE=$certificate
fi
arguments=(enroll --server "$server" --token "$token" --store-directory "$store" --non-interactive)
[[ -z $name ]] || arguments+=(--name "$name")
run_redacted "$host" "${arguments[@]}"
unset token BP_SMOKE_ENROLLMENT_TOKEN
