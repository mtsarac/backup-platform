"""Replace the agent binaries on both VMs with the packages the (rebuilt) API serves; identity and configuration are kept."""
import lib
U = "https://192.168.122.1:5252/api/agent-dist/packages"
print(lib.fedora(f"""set -e
sudo systemctl stop backup-agent
cd /tmp && rm -rf agent-new && mkdir agent-new && curl -s {U}/linux-x64 | tar xz -C agent-new
sudo cp -a agent-new/*/bin/. /opt/backup-agent/bin/ && sudo chown -R backup-agent: /opt/backup-agent && sudo restorecon -RF /opt/backup-agent
sudo systemctl start backup-agent; sleep 3; systemctl is-active backup-agent"""))
print(lib.win(f"""$ErrorActionPreference='Stop'
Stop-Service BackupAgent
$z='C:\\Users\\test-user\\agent-new.zip'; $d='C:\\Users\\test-user\\agent-new'
Invoke-WebRequest {U}/win-x64 -OutFile $z -UseBasicParsing; Remove-Item -Recurse -Force $d -ErrorAction SilentlyContinue; Expand-Archive $z $d
$src=(Get-ChildItem $d -Directory | Select -First 1).FullName + '\\bin'
Get-ChildItem $src -Recurse -File | ? Name -notlike 'appsettings*.json' | % {{ $t=Join-Path 'C:\\Program Files\\BackupAgent\\bin' $_.FullName.Substring($src.Length+1); New-Item -ItemType Directory -Force (Split-Path $t) | Out-Null; Copy-Item $_.FullName $t -Force }}
Start-Service BackupAgent; Start-Sleep 3; (Get-Service BackupAgent).Status"""))
