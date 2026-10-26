#!/usr/bin/env bash
set -euo pipefail
[[ ${BACKUP_AGENT_DISPOSABLE_HOST:-} = 1 && ${EUID} -eq 0 && $# -eq 1 ]] || { printf 'Run only on a disposable systemd VM: BACKUP_AGENT_DISPOSABLE_HOST=1 bash test-installer.sh /absolute/publish\n' >&2; exit 1; }
[[ ! -e /opt/backup-platform-agent && ! -e /var/lib/backup-platform-agent ]] || { printf 'Refusing a host with an existing agent.\n' >&2; exit 1; }
script_dir=$(CDPATH= cd -- "$(dirname -- "$0")" && pwd)
bash "$script_dir/install-agent.sh" "$1" 0.0.1
[[ $(readlink /opt/backup-platform-agent/current) = releases/0.0.1 ]]
[[ $(systemctl show backup-platform-agent.service --property User --value) = backup-agent ]]
[[ $(systemctl show backup-platform-agent.service --property Restart --value) = always ]]
[[ $(stat -c '%a' /var/lib/backup-platform-agent/identity) = 700 ]]
[[ $(stat -c '%U' /var/lib/backup-platform-agent/identity) = backup-agent ]]
[[ $(stat -c '%a' /etc/backup-platform-agent/agent.env) = 640 ]]
[[ ! -e /var/lib/backup-platform-agent/identity/agent-identity.json ]]
runuser -u backup-agent -- touch /var/lib/backup-platform-agent/staging/preserve-across-upgrade
bash "$script_dir/install-agent.sh" "$1" 0.0.2
[[ $(readlink /opt/backup-platform-agent/current) = releases/0.0.2 ]]
[[ -f /var/lib/backup-platform-agent/staging/preserve-across-upgrade ]]
[[ -x /opt/backup-platform-agent/releases/0.0.1/BackupPlatform.Agent.Host ]]
if bash "$script_dir/install-agent.sh" "$1" 0.0.2; then printf 'Existing release was unexpectedly replaced.\n' >&2; exit 1; fi
printf 'Installer acceptance passed: unprivileged service, secure store, atomic version switch, preserved state, no enrollment side effect.\n'
