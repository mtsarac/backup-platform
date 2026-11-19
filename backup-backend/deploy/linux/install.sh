#!/usr/bin/env bash
# Run elevated. HTTPS certificates must be trusted by both curl and the agent.
# An existing installation (backup-agent.service, backup-agent user/group, /opt/backup-agent, /var/lib/backup-agent) is only replaced after the
# new package was downloaded and validated, and only with consent: with a terminal the script asks (default no); without one it refuses
# unless --replace-existing (yes without asking) is given. Replacing removes the service, binaries and the agent identity (a new token is
# enrolled); backups already stored in backup storage locations are not deleted.
set -euo pipefail
SERVER='' TOKEN='' AGENT_NAME='' LANGUAGE=en MODE=auto REPLACE=0 EXISTING=0
INSTALL_DIR=/opt/backup-agent
STATE_DIR=/var/lib/backup-agent
STORE_DIR=$STATE_DIR/identity
UNIT=/etc/systemd/system/backup-agent.service
WORK=''
declare -A SEEN=()
text() { if [[ "$LANGUAGE" == tr ]]; then printf '%s' "$2"; else printf '%s' "$1"; fi; }
fail() { text "$1" "$2" >&2; printf '\n' >&2; exit 1; }
# The same sentence as install.ps1 (a unit test compares them); declining or refusing an existing install ends here.
refuse_existing() { fail 'An existing agent installation was found; nothing was replaced. Uninstall the existing agent first, then run this installer again. Alternatively run it in a terminal to be asked, or add --replace-existing to remove the existing agent and reinstall without asking.' 'Mevcut bir ajan kurulumu bulundu; hicbir sey degistirilmedi. Once mevcut ajani kaldirin, ardindan bu kurulumu yeniden calistirin. Ya da sorulmasi icin bir terminalde calistirin, veya sormadan kaldirip yeniden kurmak icin --replace-existing ekleyin.'; }
# Only the agent's own fixed paths, service and account are touched; checked before anything is deleted.
remove_existing() {
    local path
    for path in "$INSTALL_DIR" "$STATE_DIR"; do
        [[ ! -L "$path" ]] || fail 'An installation path is a symbolic link; nothing was removed.' 'Bir kurulum yolu sembolik baglanti; hicbir sey silinmedi.'
    done
    if systemctl cat backup-agent.service >/dev/null 2>&1; then
        systemctl disable --now backup-agent.service || fail 'The existing service could not be stopped; nothing else was removed.' 'Mevcut hizmet durdurulamadi; baska hicbir sey silinmedi.'
    fi
    rm -f -- "$UNIT"
    systemctl daemon-reload
    systemctl reset-failed backup-agent.service 2>/dev/null || true
    rm -rf -- "$INSTALL_DIR" "$STATE_DIR"
    if getent passwd backup-agent >/dev/null; then userdel backup-agent || fail 'The backup-agent account could not be removed.' 'backup-agent hesabi kaldirilamadi.'; fi
    if getent group backup-agent >/dev/null; then groupdel backup-agent || fail 'The backup-agent group could not be removed.' 'backup-agent grubu kaldirilamadi.'; fi
}
cleanup() { unset TOKEN BACKUP_AGENT_ENROLLMENT_TOKEN; [[ -z "$WORK" ]] || rm -rf -- "$WORK"; }
trap cleanup EXIT
trap 'exit 130' INT
trap 'exit 143' TERM
while (( $# )); do
    [[ -z "${SEEN[$1]+set}" ]] || fail 'Duplicate installer option.' 'Yinelenen kurulum parametresi.'
    SEEN[$1]=1
    case "$1" in
        --server|--token|--name|--language)
            (( $# >= 2 )) && [[ "$2" != --* ]] || fail 'Missing option value.' 'Parametre degeri eksik.'
            case "$1" in
                --server) SERVER=$2 ;; --token) TOKEN=$2 ;; --name) AGENT_NAME=$2 ;; --language) LANGUAGE=$2 ;;
            esac
            shift 2 ;;
        --replace-existing) REPLACE=1; shift ;;
        --interactive|--non-interactive)
            [[ "$MODE" == auto ]] || fail 'Choose only one interaction mode.' 'Yalnizca bir etkilesim kipi secin.'
            MODE=${1#--}; shift ;;
        --help|-h)
            printf '%s\n' 'install.sh [--server HTTPS_URL] [--token TOKEN] [--name NAME] [--interactive|--non-interactive] [--replace-existing] [--language tr|en]'; exit 0 ;;
        *) fail 'Unknown installer option.' 'Bilinmeyen kurulum parametresi.' ;;
    esac
done
[[ "$LANGUAGE" == en || "$LANGUAGE" == tr ]] || fail 'Language must be tr or en.' 'Dil tr veya en olmalidir.'
if [[ "$MODE" == auto ]]; then
    MODE=non-interactive
    if [[ -z "$SERVER" || -z "$TOKEN" ]]; then
        if ( : </dev/tty ) 2>/dev/null; then MODE=interactive; fi
    fi
fi
if [[ "$MODE" == interactive ]]; then
    exec 3<>/dev/tty || fail 'Interactive mode requires a terminal.' 'Etkilesimli kip icin terminal gerekir.'
    printf '\n%s\n' "$(text 'Backup Platform agent installation' 'Backup Platform ajan kurulumu')" >&3
    printf '%s\n' "$(text '1. Connect using a trusted HTTPS server address and a one-time token from Add agent.' '1. Guvenilir HTTPS sunucu adresi ve Ajan ekle ekranindaki tek kullanimlik kayit anahtari ile baglanin.')" >&3
    if [[ -z "$SERVER" ]]; then printf '%s: ' "$(text 'Server address' 'Sunucu adresi')" >&3; IFS= read -r SERVER <&3; fi
    if [[ -z "$TOKEN" ]]; then
        printf '%s: ' "$(text 'Enrollment token (hidden)' 'Kayit anahtari (gizli)')" >&3
        IFS= read -rs TOKEN <&3; printf '\n' >&3
    fi
    printf '%s\n' "$(text '2. Choose the display name; Enter accepts the machine name.' '2. Gorunen adi secin; Enter tusu makine adini kabul eder.')" >&3
    if [[ -z "$AGENT_NAME" ]]; then
        printf '%s [%s]: ' "$(text 'Agent name' 'Ajan adi')" "$(uname -n)" >&3
        IFS= read -r AGENT_NAME <&3
    fi
fi
AGENT_NAME=${AGENT_NAME:-$(uname -n)}
[[ -n "$SERVER" && -n "$TOKEN" ]] || fail 'Server and enrollment token are required; automatic mode never prompts.' 'Sunucu ve kayit anahtari zorunludur; otomatik kip soru sormaz.'
[[ "$TOKEN" =~ ^[[:xdigit:]]{32}\.[A-Za-z0-9_-]{43}$ && "$TOKEN" != 00000000000000000000000000000000.* ]] || fail 'Invalid enrollment token.' 'Kayit anahtari gecersiz.'
[[ ${#AGENT_NAME} -le 255 && "$AGENT_NAME" =~ [^[:space:]] && ! "$AGENT_NAME" =~ [[:cntrl:]] ]] || fail 'Agent name must contain 1-255 characters without control characters.' 'Ajan adi denetim karakteri icermeyen 1-255 karakter olmalidir.'
for command in python3 curl systemctl runuser useradd getent stat install; do
    command -v "$command" >/dev/null || fail 'Required installation tools are missing (python3, curl, systemd, util-linux, shadow-utils).' 'Gerekli kurulum araclari eksik (python3, curl, systemd, util-linux, shadow-utils).'
done
if ! python3 - "$SERVER" "$AGENT_NAME" <<'PY'
import sys, unicodedata, urllib.parse
try:
    value = sys.argv[1]
    uri = urllib.parse.urlsplit(value)
    if uri.scheme != 'https' or not uri.hostname or '@' in uri.netloc:
        raise ValueError('Invalid HTTPS address')
    if any(c in value for c in '?#\\"') or any(c.isspace() or ord(c) < 32 for c in value):
        raise ValueError('Unsafe address')
    if uri.port is not None and not 1 <= uri.port <= 65535:
        raise ValueError('Invalid port')
    name = sys.argv[2]
    if not name.strip() or len(name.encode('utf-16-le')) // 2 > 255 or any(unicodedata.category(c) == 'Cc' for c in name):
        raise ValueError('Invalid agent name')
except (ValueError, AssertionError):
    sys.exit(1)
PY
then fail 'Invalid HTTPS server address or agent name. Use HTTPS without credentials, query or fragment; name must contain 1-255 characters without control characters.' 'HTTPS sunucu adresi veya ajan adi gecersiz. Kimlik bilgisi, sorgu veya parca kullanmayin; ad denetim karakteri icermeyen 1-255 karakter olmalidir.'; fi
SERVER=${SERVER%/}
[[ $(uname -s) == Linux && $(uname -m) == x86_64 ]] || fail 'Only Linux x64 is supported.' 'Yalnizca Linux x64 desteklenir.'
(( EUID == 0 )) || fail 'Run this installer as root (sudo bash).' 'Kurulumu root yetkisiyle calistirin (sudo bash).'
if [[ ! -d /run/systemd/system ]] || ! systemctl show-environment >/dev/null 2>&1; then
    fail 'A running systemd service manager is required.' 'Calisan bir systemd hizmet yoneticisi gerekir.'
fi
# Fixed root-controlled parents prevent another user from swapping paths during installation.
for path in /opt /var /var/lib /etc /etc/systemd /etc/systemd/system; do
    [[ -d "$path" && ! -L "$path" && $(stat -c %u "$path") == 0 ]] || fail 'Installation parents must be real root-owned directories.' 'Ust kurulum dizinleri root sahipliginde gercek dizinler olmalidir.'
    permissions=$(stat -c %a "$path")
    (( (8#$permissions & 0022) == 0 )) || fail 'Installation parents must not be writable by other users.' 'Diger kullanicilar ust kurulum dizinlerine yazamamalidir.'
done
for path in "$INSTALL_DIR" "$STATE_DIR" "$UNIT" /run/systemd/system/backup-agent.service /usr/lib/systemd/system/backup-agent.service /lib/systemd/system/backup-agent.service; do
    [[ ! -e "$path" && ! -L "$path" ]] || EXISTING=1
done
[[ $(systemctl show backup-agent.service --property=LoadState --value) == not-found ]] || EXISTING=1
if getent passwd backup-agent >/dev/null || getent group backup-agent >/dev/null; then EXISTING=1; fi
# No terminal and no explicit consent: refuse before downloading anything.
if (( EXISTING && ! REPLACE )) && [[ "$MODE" != interactive ]] && ! ( : </dev/tty ) 2>/dev/null; then refuse_existing; fi
if [[ "$MODE" == interactive ]]; then
    printf '\n%s\n  %s\n  %s\n  %s\n  %s\n' "$(text '3. Installation summary (token hidden)' '3. Kurulum ozeti (kayit anahtari gizli)')" "$SERVER" "$AGENT_NAME" "$INSTALL_DIR" 'backup-agent.service / backup-agent' >&3
    printf '%s\n' "$(text 'The service uses a dedicated unprivileged account; grant source access separately.' 'Hizmet ayri, yetkisiz bir hesap kullanir; kaynak erisimini ayrica verin.')" >&3
    printf '%s ' "$(text 'Install? [y/N]' 'Kurulsun mu? [e/H]')" >&3
    IFS= read -r confirm <&3
    if [[ "$LANGUAGE" == tr ]]; then accepted='^[eE]$'; else accepted='^[yY]$'; fi
    if [[ ! "$confirm" =~ $accepted ]]; then text 'Installation cancelled.' 'Kurulum iptal edildi.'; printf '\n'; exit 0; fi
fi
WORK=$(mktemp -d /tmp/backup-agent-install.XXXXXXXX)
chmod 700 "$WORK"
text 'Downloading and validating the agent package...' 'Ajan paketi indiriliyor ve dogrulaniyor...'; printf '\n'
curl --fail --silent --proto '=https' --tlsv1.2 --max-redirs 0 "$SERVER/api/agent-dist/packages/linux-x64" -o "$WORK/agent.tar.gz" || fail 'Package download failed; verify HTTPS trust and package availability.' 'Paket indirilemedi; HTTPS guvenini ve paket kullanilabilirligini denetleyin.'
# Accept only the official release layout, regular files and directories. Never follow archive links.
if ! python3 - "$WORK" 2>"$WORK/archive.log" <<'PY'
import pathlib, re, shutil, sys, tarfile
work = pathlib.Path(sys.argv[1])
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
    if not re.fullmatch(r'backup-agent-linux-x64-\d+\.\d+\.\d+(?:-[0-9A-Za-z.-]+)?', root): raise ValueError('Invalid archive root')
    binary = f'{root}/bin/BackupPlatform.Agent.Host'
    if not any(m.name == binary and m.isfile() for m in members): raise ValueError('Missing executable')
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
PY
then fail 'The package is invalid or unsafe; nothing was installed.' 'Paket gecersiz veya guvensiz; kurulum yapilmadi.'; fi
root=$(<"$WORK/root")
# The new package is valid; only now may the old installation be removed.
if (( EXISTING )); then
    if (( ! REPLACE )); then
        [[ "$MODE" == interactive ]] || exec 3<>/dev/tty
        {
            printf '\n%s\n' "$(text 'An existing agent installation was found:' 'Mevcut bir ajan kurulumu bulundu:')"
            printf '  %s%s\n  %s%s\n  %s%s\n' "$(text 'Service: ' 'Hizmet: ')" backup-agent.service "$(text 'Install path: ' 'Kurulum yolu: ')" "$INSTALL_DIR" "$(text 'Identity store: ' 'Kimlik deposu: ')" "$STORE_DIR"
            enrolled=$(sed -n 's/^Environment="Agent__Backend__BaseUri=\(.*\)"$/\1/p' "$UNIT" 2>/dev/null | head -n 1 | sed 's/%%/%/g' || true)
            [[ -z "$enrolled" ]] || printf '  %s%s\n' "$(text 'Server: ' 'Sunucu: ')" "$enrolled"
            printf '%s\n' "$(text 'Removing deletes the service, binaries, the backup-agent account and the agent identity (re-enrollment with the new token). Backups already stored in backup storage locations are not deleted.' 'Kaldirma hizmeti, ikili dosyalari, backup-agent hesabini ve ajan kimligini siler (yeni anahtarla yeniden kayit yapilir). Yedekleme depolama konumlarindaki mevcut yedekler silinmez.')"
            printf '%s ' "$(text 'Remove it and reinstall? (y/n)' 'Mevcut ajan kurulumu bulundu. Kaldirilip yeniden kurulsun mu? (e/h)')"
        } >&3
        IFS= read -r confirm <&3
        if [[ "$LANGUAGE" == tr ]]; then accepted='^[eE]$'; else accepted='^[yY]$'; fi
        [[ "$confirm" =~ $accepted ]] || refuse_existing
    fi
    text 'Removing the existing agent installation...' 'Mevcut ajan kurulumu kaldiriliyor...'; printf '\n'
    remove_existing
fi
# Fail closed after this point: preserve any newly saved identity and partial installation for diagnosis.
mkdir -m 755 "$INSTALL_DIR"
cp -R -- "$WORK/package/$root/." "$INSTALL_DIR/"
chown -R root:root "$INSTALL_DIR"
chmod 755 "$INSTALL_DIR/bin/BackupPlatform.Agent.Host"
useradd --system --user-group --no-create-home --home-dir "$STATE_DIR" --shell /usr/sbin/nologin backup-agent
install -d -m 700 -o backup-agent -g backup-agent "$STATE_DIR" "$STORE_DIR"
export BACKUP_AGENT_ENROLLMENT_TOKEN="$TOKEN"
unset TOKEN
if ! runuser --user backup-agent --preserve-environment -- "$INSTALL_DIR/bin/BackupPlatform.Agent.Host" enroll --server "$SERVER" --store-directory "$STORE_DIR" --name "$AGENT_NAME" --non-interactive >"$WORK/enroll.log" 2>&1; then
    fail 'Enrollment failed. Any identity was preserved; the token may have been consumed. Check the server before retrying.' 'Kayit basarisiz. Kaydedilen kimlik korundu; anahtar kullanilmis olabilir. Yeniden denemeden once sunucuyu denetleyin.'
fi
unset BACKUP_AGENT_ENROLLMENT_TOKEN
escaped_server=${SERVER//%/%%}
# A noclobber write refuses a unit created concurrently; the URL is quoted and percent-escaped for systemd.
if ! ( set -o noclobber; cat >"$UNIT" <<EOF
[Unit]
Description=Backup Platform Agent
Wants=network-online.target
After=network-online.target

[Service]
Type=simple
User=backup-agent
Group=backup-agent
WorkingDirectory=$INSTALL_DIR/bin
ExecStart=$INSTALL_DIR/bin/BackupPlatform.Agent.Host
Environment="Agent__Backend__BaseUri=$escaped_server"
Environment="Agent__CredentialStoreDirectory=$STORE_DIR"
Restart=on-failure
RestartSec=10
UMask=0077
NoNewPrivileges=true

[Install]
WantedBy=multi-user.target
EOF
); then fail 'Service file could not be created; identity was preserved.' 'Hizmet dosyasi olusturulamadi; kimlik korundu.'; fi
chmod 644 "$UNIT"
if ! { systemctl daemon-reload && systemctl enable --now backup-agent.service && systemctl is-active --quiet backup-agent.service && systemctl is-enabled --quiet backup-agent.service; } >"$WORK/service.log" 2>&1; then
    fail 'Service activation failed; identity and installation were preserved.' 'Hizmet etkinlestirilemedi; kimlik ve kurulum korundu.'
fi
text 'Agent installed; the persistent service is active. Confirm Online in Agents after heartbeat.' 'Ajan kuruldu; kalici hizmet etkin. Yasam sinyalinden sonra Ajanlar sayfasinda cevrimici durumunu dogrulayin.'; printf '\n'
