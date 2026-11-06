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

Windows VSS snapshots (optional, off by default)
  A file source with SnapshotKind "Vss" reads a point-in-time shadow copy through the native VSS requester API. That needs two things:
    - Features__windowsVss=true in the service environment, and
    - NT SERVICE\BackupAgent in the local Backup Operators group. The agent never runs as an administrator.
  Install with the group membership:  .\Install-BackupAgentService.ps1 -BinaryDirectory .\bin -EnableVss
  The installer prints a warning first: Backup Operators carries SeBackupPrivilege and SeRestorePrivilege (any file can be read or
  written once they are enabled; the agent leaves them disabled). Without -EnableVss the group is never touched, and the first
  VSS backup fails with the cause "PermissionDenied" instead of a generic staging error.
  Uninstall-BackupAgentService.ps1 removes the membership again only if the installer added it. A membership you added by hand stays.
  The shadow copy is created for the backup, owned by the agent process and released when the backup ends; if the agent is killed
  Windows removes it. No other shadow copy on the machine is ever touched.
  Verified on Windows 11. Windows Server has not been verified.

Backup-specific setup is separate from onboarding: staging/storage and SQL Server credentials are documented in
config\agent.settings.example.json. Never put Agent permanent credentials in that file.

Limitations of this release
  - A backend that sets AgentDistribution__PackagesDirectory serves this archive plus a guided/automatic one-line installer
    (see the repository README). This archive can still be extracted and enrolled manually as described above.
  - The binaries and installer scripts are not code-signed.

Verify the download with the SHA256SUMS.txt file attached to the release.
