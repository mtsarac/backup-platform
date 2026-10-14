#!/usr/bin/env bash
# Disposable real SFTP + PostgreSQL/MSSQL -> actual Linux Agent -> download/restore/retention.
set +x
set -euo pipefail
source "$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")/../lib" && pwd)/common.sh"
if [[ ${1:-} != --configured ]]; then
    exec "$REPO_ROOT/scripts/database/with-disposable-databases.sh" -- "$0" --configured "$@"
fi
shift
stack_options=(--download --retention-smoke)
provider_only=false
while (($#)); do
    case "$1" in
        --no-build) stack_options+=(--no-build) ;;
        --provider-only) provider_only=true ;;
        *) fail 'Usage: run-sftp-smoke.sh [--no-build] [--provider-only]' ;;
    esac
    shift
done
load_local_env
for command in docker dotnet python3 ssh-keygen; do require_command "$command"; done
SFTP_IMAGE=${SFTP_IMAGE:-atmoz/sftp:debian}
docker image inspect "$SFTP_IMAGE" >/dev/null 2>&1 || fail "Cached SFTP image required; explicitly pull it first: $SFTP_IMAGE"
make_runtime_dir
work=$RUNTIME_DIR
suffix=$(python3 -c 'import uuid; print(uuid.uuid4().hex[:12])')
source_database="bp_m5_source_$suffix"
target_database="bp_m5_target_$suffix"
sftp_container="bp-sftp-$suffix"
BP_CLEANUP_CONTAINERS+=("$sftp_container")
stack_pid='' agent_pid=''
cleanup_smoke() {
    if [[ ${1:-0} != 0 ]]; then
        smoke_dir=$(dirname "${state:-}" 2>/dev/null || true)
        for log in "$smoke_dir/api.log" "$work/stack.log" "$work/agent.log"; do
            if [[ -f "$log" ]]; then
                printf '=== %s tail ===\n' "$log" >&2
                tail -n 60 "$log" >&2
            fi
        done
    fi
    for pid in "$agent_pid" "$stack_pid"; do [[ -z $pid ]] || kill "$pid" 2>/dev/null || true; done
    for pid in "$agent_pid" "$stack_pid"; do [[ -z $pid ]] || wait "$pid" 2>/dev/null || true; done
}
rc=0
trap 'rc=$?; cleanup_smoke "$rc" || rc=1; cleanup_runtime "$rc"' EXIT
mkdir -p "$work/remote" "$work/public-keys"
# Uploaded backup bytes are not secrets; host UID must be able to inspect/delete owned files.
chmod 0777 "$work/remote"
ssh-keygen -q -t ed25519 -N '' -f "$work/ssh_host_ed25519_key"
python3 - "$work" <<'PY'
import json, os, secrets, sys
from pathlib import Path
root = Path(sys.argv[1])
password, passphrase = secrets.token_hex(24), secrets.token_hex(24)
(root / 'users.conf').write_text('smoke:' + password + ':1000:1000:upload\n')
(root / 'passphrase').write_text(passphrase)
(root / 'sftp.json').write_text(json.dumps({'password': password, 'passphrase': passphrase}))
for name in ('users.conf', 'passphrase', 'sftp.json'):
    os.chmod(root / name, 0o600)
PY
passphrase=$(<"$work/passphrase")
ssh-keygen -q -t rsa -b 2048 -N "$passphrase" -f "$work/client_key"
unset passphrase
cp "$work/client_key.pub" "$work/public-keys/client.pub"
cat >"$work/sshd_config" <<'EOF'
Port 22
HostKey /etc/ssh/ssh_host_ed25519_key
PasswordAuthentication yes
PubkeyAuthentication yes
PermitRootLogin no
UsePAM no
Subsystem sftp internal-sftp
Match User smoke
    ChrootDirectory %h
    ForceCommand internal-sftp
    AllowTcpForwarding no
    X11Forwarding no
    PermitTTY no
EOF
# No ports are published, and only this container's exact bridge address is allowlisted.
docker run --detach --name "$sftp_container" \
    --volume "$work/users.conf:/etc/sftp/users.conf:ro" \
    --volume "$work/ssh_host_ed25519_key:/etc/ssh/ssh_host_ed25519_key:ro" \
    --volume "$work/sshd_config:/etc/ssh/sshd_config:ro" \
    --volume "$work/public-keys:/home/smoke/.ssh/keys:ro" \
    --volume "$work/remote:/home/smoke/upload" "$SFTP_IMAGE" >"$work/container-id"
sftp_ip=$(docker inspect --format '{{range .NetworkSettings.Networks}}{{.IPAddress}}{{end}}' "$sftp_container")
python3 - "$sftp_ip" <<'PY'
import ipaddress, sys
ip = ipaddress.ip_address(sys.argv[1])
if not ip.is_private or ip.is_loopback or ip.is_link_local:
    sys.exit('FAIL: Disposable SFTP bridge must have a private non-loopback IPv4 address.')
PY
export Backups__Sftp__AllowedPrivateNetworks__0="$sftp_ip/32"
ready=false
for _ in $(seq 1 60); do
    [[ $(docker inspect --format '{{.State.Running}}' "$sftp_container") == true ]] || fail 'Disposable SFTP exited.'
    if python3 - "$sftp_ip" <<'PY'
import socket, sys
try:
    with socket.create_connection((sys.argv[1], 22), timeout=1) as client:
        sys.exit(0 if client.recv(256).startswith(b'SSH-2.0-') else 1)
except OSError:
    sys.exit(1)
PY
    then ready=true; break; fi
    sleep 1
done
[[ $ready == true ]] || fail 'Disposable SFTP readiness timed out.'
fingerprint=$(ssh-keygen -lf "$work/ssh_host_ed25519_key.pub" -E sha256)
fingerprint=${fingerprint#* }
fingerprint=${fingerprint%% *}
python3 - "$work" "$sftp_ip" "$fingerprint" <<'PY'
import json, sys
from pathlib import Path
root = Path(sys.argv[1])
state = json.loads((root / 'sftp.json').read_text())
state['privateKey'] = (root / 'client_key').read_text()
state['configuration'] = {'host': sys.argv[2], 'port': '22', 'username': 'smoke', 'authenticationType': 'password',
                          'remoteBaseDirectory': '/upload', 'hostKeyFingerprint': sys.argv[3]}
(root / 'sftp.json').write_text(json.dumps(state))
PY
export BACKUP_PLATFORM_TEST_SFTP_ENABLED=true BACKUP_PLATFORM_TEST_SFTP_HOST="$sftp_ip" BACKUP_PLATFORM_TEST_SFTP_PORT=22
export BACKUP_PLATFORM_TEST_SFTP_USER=smoke BACKUP_PLATFORM_TEST_SFTP_FINGERPRINT="$fingerprint" BACKUP_PLATFORM_TEST_SFTP_BASE_DIR=/upload
BACKUP_PLATFORM_TEST_SFTP_PASSWORD=$(python3 -c 'import json,sys; print(json.load(open(sys.argv[1]))["password"])' "$work/sftp.json")
export BACKUP_PLATFORM_TEST_SFTP_PASSWORD
run_dotnet_tests "$REPO_ROOT/backup-backend/tests/integration-tests" -c Release \
    --filter 'FullyQualifiedName~SftpStorageProviderIntegrationTests' -- xUnit.MaxParallelThreads=1
unset BACKUP_PLATFORM_TEST_SFTP_PASSWORD BACKUP_PLATFORM_TEST_SFTP_ENABLED
if [[ "$provider_only" == true ]]; then
    printf 'SFTP_PROVIDER_OK\n'
    exit 0
fi
export BP_SMOKE_POSTGRES_CONTAINER=$POSTGRES_CONTAINER
"$REPO_ROOT/scripts/frontend/browser-smoke-env.sh" "${stack_options[@]}" >"$work/stack.log" 2>&1 &
stack_pid=$!
ready=false
for _ in $(seq 1 240); do
    kill -0 "$stack_pid" 2>/dev/null || fail 'SFTP smoke stack exited (runtime diagnostics withheld).'
    if python3 - "$work/stack.log" <<'PY'
import sys
sys.exit(0 if '\nSMOKE_READY\n' in open(sys.argv[1]).read() else 1)
PY
    then ready=true; break; fi
    sleep 1
done
[[ $ready == true ]] || fail 'SFTP smoke stack startup timed out.'
read -r server certificate state credentials < <(python3 - "$work/stack.log" <<'PY'
import sys
values = dict(line.strip().split('=', 1) for line in open(sys.argv[1]) if line.startswith('SMOKE_') and '=' in line)
print(*(values[key] for key in ('SMOKE_API_URL', 'SMOKE_CERTIFICATE_FILE', 'SMOKE_DOWNLOAD_STATE_FILE', 'SMOKE_CREDENTIALS_FILE')))
PY
)
fixture=("$REPO_ROOT/scripts/frontend/sftp-smoke-fixture.py" --server "$server" --certificate "$certificate" --state "$state" --credentials-file "$credentials" --sftp-file "$work/sftp.json" --remote-root "$work/remote")
download=("$REPO_ROOT/scripts/frontend/download-smoke-fixture.py" --server "$server" --certificate "$certificate" --state "$state")
python3 "${fixture[@]}" setup
connection_id=$(python3 -c 'import json,sys; print(json.load(open(sys.argv[1]))["sftpConnectionId"])' "$state")
python3 "$REPO_ROOT/scripts/frontend/restore-smoke-sql.py" setup --container "$MSSQL_CONTAINER" --database "$source_database"
run_redacted dotnet publish "$REPO_ROOT/backup-backend/src/hosts/agent" -c Release -r linux-x64 --self-contained true -p:AgentPlatform=Linux -o "$work/agent" >"$work/publish-agent.log"
mkdir -p "$work/identity"
"$REPO_ROOT/scripts/integration/enroll-agent.sh" --host "$work/agent/BackupPlatform.Agent.Host" --server "$server" \
    --token-file "$(dirname "$state")/central-token.json" --store-directory "$work/identity" --name "SFTP smoke $suffix" --certificate "$certificate"
export SSL_CERT_FILE=$certificate
export Agent__Backend__BaseUri=$server Agent__CredentialStoreDirectory="$work/identity"
export Agent__Connectivity__HeartbeatInterval=00:00:05 Agent__Connectivity__PollInterval=00:00:05 Agent__Connectivity__MinimumPollSpacing=00:00:01
export Agent__Backup__Staging__Mapping=Mapped
export Agent__Backup__Staging__ServerDirectory=$BACKUP_PLATFORM_TEST_MSSQL_STAGING_SERVER_DIR
export Agent__Backup__Staging__AgentDirectory=$BACKUP_PLATFORM_TEST_MSSQL_STAGING_AGENT_DIR
BACKUP_AGENT_SECRET_sql_smoke=$(python3 -c 'import json, os; print(json.dumps({"userName":os.environ["BACKUP_PLATFORM_TEST_MSSQL_USER"],"password":os.environ["BACKUP_PLATFORM_TEST_MSSQL_PASSWORD"]}))')
export BACKUP_AGENT_SECRET_sql_smoke
(cd "$work/agent" && exec ./BackupPlatform.Agent.Host) >"$work/agent.log" 2>&1 &
agent_pid=$!
python3 "${download[@]}" backup --provider sftp --storage-connection-id "$connection_id" --agent-name "SFTP smoke $suffix" \
    --sql-host "$BACKUP_PLATFORM_TEST_MSSQL_HOST" --sql-port "$BACKUP_PLATFORM_TEST_MSSQL_PORT" --database "$source_database" \
    ${BP_SMOKE_UPLOAD_LIMIT:+--upload-limit "$BP_SMOKE_UPLOAD_LIMIT"}
python3 "${fixture[@]}" inspect
python3 "${download[@]}" verify --provider sftp --output "$work/download.bak"
for mode in disable lock; do
    python3 "${fixture[@]}" access --mode "$mode"
    python3 "${download[@]}" verify --provider sftp --output "$work/download-$mode.bak"
    db_name="${target_database}"
    if [[ "$mode" == "lock" ]]; then db_name="${target_database}_restored"; fi
    python3 "$REPO_ROOT/scripts/frontend/storage-connections-smoke-fixture.py" --server "$server" --certificate "$certificate" --state "$state" --credentials-file "$credentials" \
        restore --provider sftp --database "$db_name" --sql-host "$BACKUP_PLATFORM_TEST_MSSQL_HOST" --sql-port "$BACKUP_PLATFORM_TEST_MSSQL_PORT"
    python3 "$REPO_ROOT/scripts/frontend/restore-smoke-sql.py" verify --container "$MSSQL_CONTAINER" --database "$db_name"
done
python3 "${fixture[@]}" access --mode reject
python3 "${fixture[@]}" access --mode rotate
python3 "${download[@]}" verify --provider sftp --output "$work/download-rotated.bak"
python3 "${fixture[@]}" delete
printf 'SFTP_SMOKE_OK\n'
