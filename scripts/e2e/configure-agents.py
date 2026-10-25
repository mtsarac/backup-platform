"""Apply the e2e agent backup configuration (staging, local volumes, file sources, PG tools) to both VM agents and restart them."""
import api, base64, subprocess
def ssh(host, cmd, inp=None):
    r = subprocess.run(["ssh", host, cmd], input=inp, text=True, capture_output=True)
    print(host, "\n".join(l for l in (r.stdout + r.stderr).splitlines() if not any(x in l for x in ("quantum", "vulnerable", "upgraded", "**", "CLIXML", "<Objs")))[-800:])

linux = {
    "Agent__Backup__Staging__Mapping": "SameHost",
    "Agent__Backup__Staging__AgentDirectory": "/var/lib/backup-agent/staging",
    "Agent__Backup__LocalStorage__RootDirectory": "/var/lib/backup-agent/local-store",
    "Agent__Backup__LocalStorage__Volumes__vol2": "/srv/e2e/volume2",
    "Agent__Backup__FileSources__e2e-files__RootDirectory": "/srv/e2e/files",
    "Agent__Backup__Sftp__AllowedPrivateNetworks__0": "192.168.122.0/24",
}
unit = "[Service]\n" + "".join(f'Environment="{k}={v}"\n' for k, v in linux.items())
ssh("virtual-fedora", "sudo bash -s", f"""set -e
mkdir -p /var/lib/backup-agent/staging /var/lib/backup-agent/local-store /srv/e2e/volume2 /srv/e2e/restore
chown -R backup-agent: /var/lib/backup-agent/staging /var/lib/backup-agent/local-store /srv/e2e
mkdir -p /etc/systemd/system/backup-agent.service.d
cat > /etc/systemd/system/backup-agent.service.d/e2e.conf <<'EOT'
{unit}EOT
systemctl daemon-reload && systemctl restart backup-agent && sleep 2 && systemctl is-active backup-agent
""")

win = {
    "Agent__Backup__Staging__Mapping": "SameHost",
    "Agent__Backup__Staging__AgentDirectory": r"C:\e2e\staging",
    "Agent__Backup__LocalStorage__RootDirectory": r"C:\e2e\local-store",
    "Agent__Backup__LocalStorage__Volumes__vol2": r"C:\e2e\volume2",
    "Agent__Backup__FileSources__e2e-files__RootDirectory": r"C:\e2e\files",
    "Agent__Backup__Sftp__AllowedPrivateNetworks__0": "192.168.122.0/24",
    "Agent__Backup__PostgreSql__ClientToolsDirectory": r"C:\Program Files\PostgreSQL\18\bin",
}
items = ",".join("'%s=%s'" % (k, v) for k, v in win.items())
ps = f"""
New-Item -ItemType Directory -Force C:\\e2e\\staging,C:\\e2e\\local-store,C:\\e2e\\volume2,C:\\e2e\\restore | Out-Null
icacls C:\\e2e /grant 'Everyone:(OI)(CI)M' /T /Q | Out-Null
Set-ItemProperty 'HKLM:\\SYSTEM\\CurrentControlSet\\Services\\BackupAgent' -Name Environment -Type MultiString -Value @({items})
Restart-Service BackupAgent; Start-Sleep 3; (Get-Service BackupAgent).Status
"""
ssh("virtual-win11", "powershell -NoProfile -EncodedCommand " + base64.b64encode(ps.encode("utf-16le")).decode())
