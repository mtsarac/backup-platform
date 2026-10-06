Backup Platform Agent @VERSION@ - Linux x64
===========================================

Self-contained build: no .NET runtime has to be installed.

Contents
  bin/                              published Agent.Host (BackupPlatform.Agent.Host) and its runtime
  config/agent.settings.example.json  configuration keys with placeholders (no secrets)

Run
  Agent__Backend__BaseUri=https://backup.example.com/ ./bin/BackupPlatform.Agent.Host

  Configuration comes from environment variables (Agent__...) or bin/appsettings.json; the credential store is
  /var/lib/backup-agent unless Agent__CredentialStoreDirectory says otherwise. SQL Server credentials are supplied
  as BACKUP_AGENT_SECRET_<reference> environment variables (JSON {"userName":"...","password":"..."}).
  The process handles SIGTERM with a graceful shutdown.

Limitations of this release
  - There is NO official installer and NO systemd unit. Run the host under a service manager you maintain.
  - Agent.Host has no enrollment command. It exits with code 1 until an agent identity is stored in the credential
    store. See the repository README, section "Current MVP scope".
  - The binaries are not code-signed.

Verify the download with the SHA256SUMS.txt file attached to the release.
