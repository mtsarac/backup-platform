#!/usr/bin/env bash
# M7d.3 real acceptance on the persistent dev stack: ONE MSSQL backup -> Central + SFTP + Google Drive replicas, then SFTP down (Degraded / InsufficientReplicas).
#   - starts a disposable SFTP container (removed at exit) and restarts ONLY the dev API/Agent with a Central root + the SFTP allowlist;
#   - runs scripts/dev/multi-destination-acceptance.py (HTTP API only; real Google Drive connection needed: scripts/dev/acceptance.py prerequisites);
#   - deletes only the artifacts it created, then restarts the API/Agent as start.sh configures them. PostgreSQL/MSSQL data is never reset.
# Usage: scripts/dev/multi-destination-acceptance.sh [--no-build]
set +x
set -euo pipefail
# shellcheck source=common.sh
source "$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)/common.sh"
build_args=()
[[ ${1:-} != --no-build ]] || build_args=(--no-build)
load_local_env
for tool in docker python3 ssh-keygen dotnet; do require_command "$tool"; done
dev_prepare_state
SFTP_IMAGE=${SFTP_IMAGE:-atmoz/sftp:debian}
docker image inspect "$SFTP_IMAGE" >/dev/null 2>&1 || fail "Cached SFTP image required; explicitly pull it first: $SFTP_IMAGE"
[[ $(docker inspect --format '{{.State.Running}}' "$BP_DEV_MSSQL_CONTAINER" 2>/dev/null) == true ]] || fail 'The dev stack is not running (scripts/dev/start.sh).'
"$REPO_ROOT/scripts/dev/mssql-test-db.sh" create >/dev/null

work="$BP_DEV_HOME/multi-acceptance"
suffix=$(python3 -c 'import uuid; print(uuid.uuid4().hex[:10])')
sftp_container="bp-dev-acc-sftp-$suffix"
BP_CLEANUP_CONTAINERS+=("$sftp_container")
rm -rf -- "$work"; mkdir -p "$work/remote" "$work/central"; chmod 0777 "$work/remote"; chmod 700 "$work"

restart_stack() { # NAME=value arguments, then start.sh options after "--": the extra environment is inherited by the API and the Agent that start.sh spawns
    local extra=() ; while (($#)) && [[ $1 != -- ]]; do extra+=("$1"); shift; done; shift || true
    dev_kill agent; dev_kill api
    env "${extra[@]}" "$REPO_ROOT/scripts/dev/start.sh" "$@" >/dev/null
}
finish() {
    local status=$?
    if [[ -f $work/state-restored ]]; then :; else
        python3 "$REPO_ROOT/scripts/dev/multi-destination-acceptance.py" cleanup || true
        restart_stack -- --no-build || true
    fi
    rm -rf -- "$work"
    cleanup_runtime "$status"
}
trap finish EXIT

ssh-keygen -q -t ed25519 -N '' -f "$work/ssh_host_ed25519_key"
password=$(python3 -c 'import secrets; print(secrets.token_hex(24))')
printf 'smoke:%s:1000:1000:upload\n' "$password" > "$work/users.conf"; chmod 600 "$work/users.conf"
cat >"$work/sshd_config" <<'EOF'
Port 22
HostKey /etc/ssh/ssh_host_ed25519_key
PasswordAuthentication yes
PubkeyAuthentication no
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
docker run --detach --name "$sftp_container" \
    --volume "$work/users.conf:/etc/sftp/users.conf:ro" --volume "$work/ssh_host_ed25519_key:/etc/ssh/ssh_host_ed25519_key:ro" \
    --volume "$work/sshd_config:/etc/ssh/sshd_config:ro" --volume "$work/remote:/home/smoke/upload" "$SFTP_IMAGE" >/dev/null
sftp_ip=$(docker inspect --format '{{range .NetworkSettings.Networks}}{{.IPAddress}}{{end}}' "$sftp_container")
python3 - "$sftp_ip" <<'PY'
import ipaddress, sys
ip = ipaddress.ip_address(sys.argv[1])
if not ip.is_private or ip.is_loopback or ip.is_link_local:
    sys.exit('FAIL: the disposable SFTP bridge must have a private non-loopback IPv4 address.')
PY
sftp_ready=false
for _ in $(seq 1 60); do
    if python3 - "$sftp_ip" <<'PY'
import socket, sys
try:
    with socket.create_connection((sys.argv[1], 22), timeout=1) as client:
        sys.exit(0 if client.recv(256).startswith(b'SSH-2.0-') else 1)
except OSError:
    sys.exit(1)
PY
    then sftp_ready=true; break; fi
    sleep 1
done
[[ $sftp_ready == true ]] || fail 'The disposable SFTP did not become ready.'
fingerprint=$(ssh-keygen -lf "$work/ssh_host_ed25519_key.pub" -E sha256); fingerprint=${fingerprint#* }; fingerprint=${fingerprint%% *}
python3 - "$work" "$sftp_ip" "$fingerprint" "$password" <<'PY'
import json, os, sys
from pathlib import Path
root, ip, fingerprint, password = Path(sys.argv[1]), *sys.argv[2:5]
(root / 'sftp.json').write_text(json.dumps({'password': password, 'configuration': {'host': ip, 'port': '22', 'username': 'smoke', 'authenticationType': 'password',
                                                                                    'remoteBaseDirectory': '/upload', 'hostKeyFingerprint': fingerprint}}))
os.chmod(root / 'sftp.json', 0o600)
PY
unset password

tenant_id=$(python3 "$REPO_ROOT/scripts/dev/multi-destination-acceptance.py" tenant-id)
scope="m7d3-$suffix"
# env(1), not export: the tenant id (with hyphens) is part of a configuration key and is not a valid shell identifier.
printf 'Restarting the dev API and Agent with a Central root and the SFTP allowlist (data untouched)\n'
restart_stack "Backups__Storage__Central__RootDirectory=$work/central" "Backups__Storage__Central__TenantScopes__${tenant_id}=$scope" \
    "Backups__Sftp__AllowedPrivateNetworks__0=$sftp_ip/32" "Agent__Backup__CentralStorage__RootDirectory=$work/central" "Agent__Backup__CentralStorage__TenantScope=$scope" \
    -- "${build_args[@]}"
# A newly started Agent declares its capabilities (including backup.multi-destination) on its first sync; wait for the dev tenant to see it.
sleep 20

acceptance=("$REPO_ROOT/scripts/dev/multi-destination-acceptance.py")
python3 "${acceptance[@]}" prepare "$work/sftp.json" "$sftp_container" "$work/remote" "$work/central"
python3 "${acceptance[@]}" delete-leftovers
python3 "${acceptance[@]}" run healthy 2
python3 "${acceptance[@]}" delete healthy
docker stop "$sftp_container" >/dev/null
python3 "${acceptance[@]}" run degraded 2
python3 "${acceptance[@]}" run insufficient 3
python3 "${acceptance[@]}" delete degraded insufficient
printf 'MULTI_DESTINATION_ACCEPTANCE_OK\n'
