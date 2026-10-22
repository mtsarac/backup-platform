#!/usr/bin/env bash
set -euo pipefail
umask 077
if [[ ${EUID} -ne 0 || $# -ne 2 ]]; then
  printf 'Usage (root): bash install-agent.sh /absolute/published-agent version\n' >&2
  exit 1
fi
payload=$1
version=$2
[[ "$payload" = /* && -x "$payload/BackupPlatform.Agent.Host" ]] || { printf 'A published Linux agent directory is required.\n' >&2; exit 1; }
[[ "$version" =~ ^(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)(\.(0|[1-9][0-9]*)){0,2}$ ]] || { printf 'Use a canonical numeric release version.\n' >&2; exit 1; }
command -v systemctl >/dev/null
command -v runuser >/dev/null
if ! getent group backup-agent >/dev/null; then groupadd --system backup-agent; fi
if ! getent passwd backup-agent >/dev/null; then
  useradd --system --gid backup-agent --home-dir /var/lib/backup-platform-agent --shell /usr/sbin/nologin backup-agent
fi
base=/opt/backup-platform-agent
release="$base/releases/$version"
[[ ! -L "$base" && ! -L "$base/releases" && ! -e "$release" && ! -L "$release" ]] || { printf 'Release already exists or installation root is unsafe; no files changed.\n' >&2; exit 1; }
script_dir=$(CDPATH= cd -- "$(dirname -- "$0")" && pwd)
install -d -m 0755 -o backup-agent -g backup-agent "$base"
runuser -u backup-agent -- install -d -m 0755 "$base/releases"
[[ ! -L /var/lib/backup-platform-agent ]] || { printf 'Unsafe state root.\n' >&2; exit 1; }
install -d -m 0750 -o root -g backup-agent /var/lib/backup-platform-agent
for directory in identity staging updates; do
  [[ ! -L "/var/lib/backup-platform-agent/$directory" ]] || { printf 'Unsafe state directory.\n' >&2; exit 1; }
  install -d -m 0700 -o backup-agent -g backup-agent "/var/lib/backup-platform-agent/$directory"
done
install -d -m 0750 -o root -g backup-agent /etc/backup-platform-agent
if [[ ! -e /etc/backup-platform-agent/agent.env ]]; then
  install -m 0640 -o root -g backup-agent "$script_dir/agent-environment.example" /etc/backup-platform-agent/agent.env
fi
for configuration in "$payload"/appsettings*.json; do
  [[ -f "$configuration" ]] || continue
  name=${configuration##*/}
  [[ ! -L "$configuration" && ! -L "/etc/backup-platform-agent/$name" ]] || { printf 'Unsafe configuration path.\n' >&2; exit 1; }
  if [[ ! -e "/etc/backup-platform-agent/$name" ]]; then
    install -m 0640 -o root -g backup-agent "$configuration" "/etc/backup-platform-agent/$name"
  fi
done
if [[ ! -e /etc/backup-platform-agent/update-settings.json ]]; then
  printf '%s\n' '{"Root":"/opt/backup-platform-agent","Staging":"/var/lib/backup-platform-agent/updates","Store":"/var/lib/backup-platform-agent/identity","RuntimeConfiguration":"/etc/backup-platform-agent/appsettings.json","TrustedKeys":{}}' > /etc/backup-platform-agent/update-settings.json
  chown root:backup-agent /etc/backup-platform-agent/update-settings.json
  chmod 0640 /etc/backup-platform-agent/update-settings.json
fi
if [[ ! -d "$base/supervisor" ]]; then
  install -d -m 0755 -o backup-agent -g backup-agent "$base/supervisor"
  runuser -u backup-agent -- cp -a --no-preserve=ownership -- "$payload/." "$base/supervisor/"
  runuser -u backup-agent -- chmod -R go-w "$base/supervisor"
fi
staged=$(runuser -u backup-agent -- mktemp -d "$base/releases/.install-XXXXXXXX")
trap 'runuser -u backup-agent -- rm -rf -- "$staged"' EXIT
runuser -u backup-agent -- cp -a --no-preserve=ownership -- "$payload/." "$staged/"
runuser -u backup-agent -- chmod -R go-w "$staged"
runuser -u backup-agent -- mv -T -- "$staged" "$release"
trap - EXIT
install -m 0644 -o root -g root "$script_dir/backup-platform-agent.service" /etc/systemd/system/backup-platform-agent.service
if systemctl is-active --quiet backup-platform-agent.service; then systemctl stop backup-platform-agent.service; fi
next="$base/.current-$$"
runuser -u backup-agent -- ln -s -- "releases/$version" "$next"
runuser -u backup-agent -- mv -Tf -- "$next" "$base/current"
systemctl daemon-reload
systemctl enable backup-platform-agent.service
if [[ -f /var/lib/backup-platform-agent/identity/agent-identity.json ]]; then
  systemctl start backup-platform-agent.service
else
  printf 'Installed. Enroll interactively as backup-agent, then start backup-platform-agent.service. No token was requested or stored by this installer.\n'
fi
