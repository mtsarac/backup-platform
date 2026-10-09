#!/usr/bin/env bash
# Local Linux Agent for the development stack: enrolls once (real CLI + one-time token), then runs against the dev API.
# Usage: scripts/dev/agent.sh start|stop|status [--no-build]
set +x
set -euo pipefail
# shellcheck source=common.sh
source "$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)/common.sh"

action=${1:-status}; shift || true
build=true
[[ ${1:-} != --no-build ]] || build=false
agent_dir="$BP_DEV_HOME/agent-bin"

case $action in
    stop) dev_kill agent; printf 'Agent stopped\n'; exit 0 ;;
    status) if dev_pid_alive agent; then printf 'agent: running (pid %s)\n' "$(<"$BP_DEV_HOME/run/agent.pid")"; else printf 'agent: stopped\n'; fi; exit 0 ;;
    start) ;;
    *) fail 'Usage: agent.sh start|stop|status [--no-build]' ;;
esac

dev_prepare_state
dev_pid_alive agent && { printf 'Agent already running\n'; exit 0; }
require_command dotnet
if [[ $build == true || ! -x $agent_dir/BackupPlatform.Agent.Host ]]; then
    run_redacted dotnet publish "$REPO_ROOT/backup-backend/src/hosts/agent" -c Release -r linux-x64 --self-contained true -p:AgentPlatform=Linux -o "$agent_dir" --nologo -v q >/dev/null
fi
export SSL_CERT_FILE="$BP_DEV_HOME/cert.pem"
if [[ -z $(ls -A "$BP_DEV_HOME/agent-identity") ]]; then
    token_file="$BP_DEV_HOME/run/enroll-token.json"
    python3 "$REPO_ROOT/scripts/dev/bootstrap.py" enrollment-token --accounts-file "$BP_DEV_ACCOUNTS_FILE" --server "$BP_DEV_API_TLS_URL" \
        --certificate "$BP_DEV_HOME/cert.pem" --output "$token_file.new"
    mv -f "$token_file.new" "$token_file"
    "$REPO_ROOT/scripts/integration/enroll-agent.sh" --host "$agent_dir/BackupPlatform.Agent.Host" --server "$BP_DEV_API_TLS_URL" \
        --token-file "$token_file" --store-directory "$BP_DEV_HOME/agent-identity" --name "dev-linux-agent" --certificate "$BP_DEV_HOME/cert.pem"
    rm -f "$token_file"
fi

export Agent__Backend__BaseUri=$BP_DEV_API_TLS_URL Agent__CredentialStoreDirectory="$BP_DEV_HOME/agent-identity"
export Agent__Connectivity__HeartbeatInterval=00:00:10 Agent__Connectivity__PollInterval=00:00:10 Agent__Connectivity__MinimumPollSpacing=00:00:01
export Agent__Backup__Staging__Mapping=Mapped Agent__Backup__Staging__ServerDirectory=/var/opt/mssql/staging Agent__Backup__Staging__AgentDirectory="$BP_DEV_HOME/mssql/staging"
mkdir -p "$BP_DEV_HOME/agent-local-storage"
export Agent__Backup__LocalStorage__RootDirectory="$BP_DEV_HOME/agent-local-storage"
export "BACKUP_AGENT_SECRET_$BP_DEV_SQL_CREDENTIAL_REFERENCE=$(python3 -c 'import json, sys; print(json.dumps({"userName": "sa", "password": sys.argv[1]}))' "$BP_DEV_MSSQL_SA_PASSWORD")"
cd -- "$agent_dir"
dev_spawn agent ./BackupPlatform.Agent.Host
sleep 3
dev_pid_alive agent || { tail -n 20 "$BP_DEV_HOME/log/agent.log" >&2; fail 'Agent exited during startup.'; }
printf 'Agent running (pid %s)\n' "$(<"$BP_DEV_HOME/run/agent.pid")"
