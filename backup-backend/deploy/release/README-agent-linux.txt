Backup Platform Agent @VERSION@ - Linux x64
===========================================

Self-contained build: no .NET runtime has to be installed.

Contents
  bin/                              published Agent.Host (BackupPlatform.Agent.Host) and its runtime
  config/agent.settings.example.json  configuration keys with placeholders (no secrets)

Enroll and run (same Unix account)
  1. In the frontend, open Agents -> Add Agent as tenant Owner/Admin, select Linux and create a one-time token.
  2. From this archive's root:
       ./bin/BackupPlatform.Agent.Host enroll --server 'https://backup.example.com' --token '<one-time-token>' --store-directory "$HOME/.local/share/backup-agent" --non-interactive
       ./bin/BackupPlatform.Agent.Host --store-directory "$HOME/.local/share/backup-agent"
     Optional enrollment flag: --name 'Database host'. Interactive enroll also supports hidden token input.
  3. The wizard shows Online after authenticated heartbeat and capability sync. The host handles SIGTERM gracefully.

The permanent identity and server are stored owner-only (directory 0700, files 0600), not in appsettings or output.
Existing enrollment is refused without overwriting identity. Restart using the same account and store retains AgentId.
The default service-style store is /var/lib/backup-agent; the commands above use an unprivileged per-user store.
Use a trusted HTTPS certificate; validation is never disabled.

Backup-specific configuration uses Agent__... environment variables or bin/appsettings.json. SQL Server credentials
are supplied as BACKUP_AGENT_SECRET_<reference> environment variables (JSON {"userName":"...","password":"..."}).

Limitations of this release
  - This archive alone has no installer or systemd unit; the foreground run command above needs a service manager you maintain.
    A backend that sets AgentDistribution__PackagesDirectory serves a systemd one-line installer (guided or automatic) that installs
    this archive under /opt/backup-agent as the unprivileged backup-agent account. There is no uninstall script yet.
  - The binaries and installer scripts are not code-signed.

Verify the download with the SHA256SUMS.txt file attached to the release.
