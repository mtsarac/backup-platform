#!/usr/bin/env bash
# The one Linux installer. Run as root.
#   install-agent.sh /absolute/published-agent version                    install a local build, no enrollment
#   install-agent.sh --server https://host --token T [--name N] [--language tr|en]
#                                                                         download the package for this CPU, install, enroll, start
# Layout: /opt/backup-platform-agent/{releases/<version>,current,supervisor}, state /var/lib/backup-platform-agent, config /etc/backup-platform-agent.
set -euo pipefail
umask 077
SERVER='' TOKEN='' AGENT_NAME='' LANGUAGE=en WORK=''
text() { if [[ "$LANGUAGE" == tr ]]; then printf '%s' "$2"; else printf '%s' "$1"; fi; }
fail() { text "$1" "${2:-$1}" >&2; printf '\n' >&2; exit 1; }
cleanup() { unset TOKEN BACKUP_AGENT_ENROLLMENT_TOKEN; [[ -z "$WORK" ]] || rm -rf -- "$WORK"; }
trap cleanup EXIT
if [[ ${1:-} == --* ]]; then
  while (( $# )); do
    case "$1" in
      --server|--token|--name|--language)
        (( $# >= 2 )) && [[ "$2" != --* ]] || fail 'Missing option value.' 'Parametre degeri eksik.'
        case "$1" in --server) SERVER=$2 ;; --token) TOKEN=$2 ;; --name) AGENT_NAME=$2 ;; --language) LANGUAGE=$2 ;; esac
        shift 2 ;;
      *) fail 'Unknown installer option.' 'Bilinmeyen kurulum parametresi.' ;;
    esac
  done
  [[ "$LANGUAGE" == en || "$LANGUAGE" == tr ]] || fail 'Language must be tr or en.' 'Dil tr veya en olmalidir.'
  AGENT_NAME=${AGENT_NAME:-$(uname -n)}
  [[ -n "$SERVER" && -n "$TOKEN" ]] || fail 'Server and enrollment token are required; the installer never prompts.' 'Sunucu ve kayit anahtari zorunludur; kurulum soru sormaz.'
  [[ "$TOKEN" =~ ^[[:xdigit:]]{32}\.[A-Za-z0-9_-]{43}$ && "$TOKEN" != 00000000000000000000000000000000.* ]] || fail 'Invalid enrollment token.' 'Kayit anahtari gecersiz.'
  [[ ${#AGENT_NAME} -le 255 && "$AGENT_NAME" =~ [^[:space:]] && ! "$AGENT_NAME" =~ [[:cntrl:]] ]] || fail 'Agent name must contain 1-255 characters without control characters.' 'Ajan adi denetim karakteri icermeyen 1-255 karakter olmalidir.'
  for command in python3 curl; do command -v "$command" >/dev/null || fail 'python3 and curl are required.' 'python3 ve curl gerekir.'; done
  python3 - "$SERVER" <<'PY' || fail 'Invalid HTTPS server address. Use HTTPS without credentials, query or fragment.' 'HTTPS sunucu adresi gecersiz. Kimlik bilgisi, sorgu veya parca kullanmayin.'
import sys, urllib.parse
value = sys.argv[1]
uri = urllib.parse.urlsplit(value)
bad = uri.scheme != 'https' or not uri.hostname or '@' in uri.netloc or any(c in value for c in '?#\\"') or any(c.isspace() or ord(c) < 32 for c in value)
sys.exit(1 if bad or (uri.port is not None and not 1 <= uri.port <= 65535) else 0)
PY
  SERVER=${SERVER%/}
  (( EUID == 0 )) || fail 'Run this installer as root (sudo bash).' 'Kurulumu root yetkisiyle calistirin (sudo bash).'
  case $(uname -m) in x86_64) arch=x64 ;; aarch64|arm64) arch=arm64 ;; *) fail 'Only Linux x64 and arm64 are supported.' 'Yalnizca Linux x64 ve arm64 desteklenir.' ;; esac
  [[ ! -e /etc/systemd/system/backup-agent.service && ! -e /opt/backup-agent ]] || fail 'A legacy install.sh agent (backup-agent.service, /opt/backup-agent) was found. Remove it first: systemctl disable --now backup-agent; rm -rf /opt/backup-agent /var/lib/backup-agent /etc/systemd/system/backup-agent.service' 'Eski install.sh ajani bulundu. Once kaldirin: systemctl disable --now backup-agent; rm -rf /opt/backup-agent /var/lib/backup-agent /etc/systemd/system/backup-agent.service'
  [[ ! -e /var/lib/backup-platform-agent/identity/agent-identity.json ]] || fail 'This computer is already enrolled; nothing was changed.' 'Bu bilgisayar zaten kayitli; hicbir sey degistirilmedi.'
  WORK=$(mktemp -d /tmp/backup-agent-install.XXXXXXXX)
  # The unprivileged backup-agent account copies the payload from here.
  chmod 755 "$WORK"; umask 022
  text 'Downloading and validating the agent package...' 'Ajan paketi indiriliyor ve dogrulaniyor...'; printf '\n'
  curl --fail --silent --proto '=https' --tlsv1.2 --max-redirs 0 "$SERVER/api/agent-dist/packages/linux-$arch" -o "$WORK/agent.tar.gz" || fail 'Package download failed; verify HTTPS trust and package availability.' 'Paket indirilemedi; HTTPS guvenini ve paket kullanilabilirligini denetleyin.'
  # Accept only the official release layout, regular files and directories. Never follow archive links.
  python3 - "$WORK" "$arch" <<'PY' || fail 'The package is invalid or unsafe; nothing was installed.' 'Paket gecersiz veya guvensiz; kurulum yapilmadi.'
import pathlib, re, shutil, sys, tarfile
work, arch = pathlib.Path(sys.argv[1]), sys.argv[2]
with tarfile.open(work / 'agent.tar.gz', 'r:gz') as archive:
    members = archive.getmembers()
    roots, seen = set(), set()
    for item in members:
        path = pathlib.PurePosixPath(item.name)
        if path.is_absolute() or '..' in path.parts or not path.parts or '\\' in item.name or not (item.isfile() or item.isdir()):
            raise ValueError('Unsafe archive entry')
        if path in seen: raise ValueError('Duplicate archive entry')
        seen.add(path)
        roots.add(path.parts[0])
    if len(roots) != 1: raise ValueError('Expected one archive root')
    root = roots.pop()
    match = re.fullmatch(rf'backup-agent-linux-{arch}-(\d+\.\d+\.\d+)', root)
    if not match: raise ValueError('Invalid archive root')
    files = {m.name for m in members if m.isfile()}
    for required in ('bin/BackupPlatform.Agent.Host', 'install/install-agent.sh', 'install/backup-platform-agent.service', 'install/agent-environment.example'):
        if f'{root}/{required}' not in files: raise ValueError('Missing ' + required)
    destination = work / 'package'
    destination.mkdir()
    for item in members:
        target = destination / item.name
        if item.isdir(): target.mkdir(parents=True, exist_ok=True)
        else:
            target.parent.mkdir(parents=True, exist_ok=True)
            with archive.extractfile(item) as source, target.open('xb') as output: shutil.copyfileobj(source, output)
            target.chmod(0o755 if item.mode & 0o111 else 0o644)
    (work / 'root').write_text(root)
    (work / 'version').write_text(match.group(1))
PY
  root=$(<"$WORK/root")
  export BACKUP_AGENT_ENROLLMENT_TOKEN="$TOKEN"
  unset TOKEN
  # The downloaded copy of this script is the one that matches the package; run it for the local install, then enroll.
  bash "$WORK/package/$root/install/install-agent.sh" "$WORK/package/$root/bin" "$(<"$WORK/version")"
  if ! runuser --user backup-agent --preserve-environment -- /opt/backup-platform-agent/current/BackupPlatform.Agent.Host enroll --server "$SERVER" --store-directory /var/lib/backup-platform-agent/identity --name "$AGENT_NAME" --non-interactive >"$WORK/enroll.log" 2>&1; then
    fail 'Enrollment failed. The enrollment key is single-use and may be consumed: create a new key in the web UI and run the command again.' 'Kayit basarisiz. Kayit anahtari tek kullanimliktir ve harcanmis olabilir: arayuzden yeni bir anahtar uretin ve komutu yeniden calistirin.'
  fi
  unset BACKUP_AGENT_ENROLLMENT_TOKEN
  systemctl start backup-platform-agent.service
  systemctl is-active --quiet backup-platform-agent.service || fail 'The service did not start; see journalctl -u backup-platform-agent.' 'Hizmet baslamadi; journalctl -u backup-platform-agent komutuna bakin.'
  text 'Agent installed; the service is active. Confirm Online in Agents after the heartbeat.' 'Ajan kuruldu; hizmet etkin. Yasam sinyalinden sonra Ajanlar sayfasinda cevrimici durumunu dogrulayin.'; printf '\n'
  exit 0
fi
(( EUID == 0 )) || fail 'Run this installer as root (sudo bash).' 'Kurulumu root yetkisiyle calistirin (sudo bash).'
if [[ $# -ne 2 ]]; then
  printf 'Usage (root): bash install-agent.sh /absolute/published-agent version | --server https://host --token T [--name N] [--language tr|en]\n' >&2
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
for directory in identity staging backups updates; do
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
# cp -a keeps the label of the source (user_tmp_t under /tmp), which systemd may not execute on SELinux hosts.
if command -v restorecon >/dev/null; then restorecon -RF /opt/backup-platform-agent /var/lib/backup-platform-agent /etc/backup-platform-agent /etc/systemd/system/backup-platform-agent.service; fi
if systemctl is-active --quiet backup-platform-agent.service; then systemctl stop backup-platform-agent.service; fi
next="$base/.current-$$"
runuser -u backup-agent -- ln -s -- "releases/$version" "$next"
runuser -u backup-agent -- mv -Tf -- "$next" "$base/current"
systemctl daemon-reload
systemctl enable backup-platform-agent.service
if [[ -f /var/lib/backup-platform-agent/identity/agent-identity.json ]]; then
  systemctl start backup-platform-agent.service
else
  [[ -n "${BACKUP_AGENT_ENROLLMENT_TOKEN:-}" ]] || printf 'Installed. Enroll interactively as backup-agent, then start backup-platform-agent.service. No token was requested or stored by this installer.\n'
fi
