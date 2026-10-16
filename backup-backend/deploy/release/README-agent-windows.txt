Backup Platform Agent @VERSION@ - Windows x64
=============================================

Self-contained build: no .NET runtime has to be installed.

Contents
  bin\                              published Agent.Host (BackupPlatform.Agent.Host.exe) and its runtime
  Install-BackupAgentService.ps1    registers the Windows Service "BackupAgent" (account NT SERVICE\BackupAgent)
  Uninstall-BackupAgentService.ps1  removes the service
  config\agent.settings.example.json  configuration keys with placeholders (no secrets)

Install and enroll (elevated PowerShell)
  1. Extract to an administrator-controlled permanent folder, for example C:\Program Files\BackupAgent.
     The service runs the files in bin\ from where they are; ordinary users must not be able to replace them.
  2. In the frontend, open Agents -> Add Agent as tenant Owner/Admin, select Windows and create a one-time token.
  3. From this archive's root, run:
       .\bin\BackupPlatform.Agent.Host.exe enroll --server 'https://backup.example.com' --token '<one-time-token>' --non-interactive
     Optional: --name 'Database host'. The command enrolls, installs and starts the delayed-auto Windows Service.
     Its identity is provisioned by NT SERVICE\BackupAgent with DPAPI CurrentUser and service-scoped ACLs.
     No manual secret/config/database operations are needed. Without automation options, enroll can prompt interactively.
  4. The wizard shows enrollment progress and Online after authenticated heartbeat and capability sync.
     The HTTPS certificate must already be trusted on this machine; certificate validation is not disabled.

Existing identity and uninstall
  Enrollment never silently replaces an identity. A failed attempt preserves any saved identity.
  .\Uninstall-BackupAgentService.ps1
    Stops/removes the service but retains its local identity and saved server.
  .\Install-BackupAgentService.ps1 -BinaryDirectory .\bin
    Reinstalls without a new token, verifies the saved credential as the service account and retains AgentId.
  .\Uninstall-BackupAgentService.ps1 -RemoveIdentity
    Explicitly deletes local enrollment (alias -PurgeIdentity). Does not delete/revoke the backend Agent record.

Backup-specific setup is separate from onboarding: staging/storage and SQL Server credentials are documented in
config\agent.settings.example.json. Never put Agent permanent credentials in that file.

Limitations of this release
  - A backend that sets AgentDistribution__PackagesDirectory serves this archive plus a guided/automatic one-line installer
    (see the repository README). This archive can still be extracted and enrolled manually as described above.
  - The binaries and installer scripts are not code-signed.

Verify the download with the SHA256SUMS.txt file attached to the release.
