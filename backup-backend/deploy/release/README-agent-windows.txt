Backup Platform Agent @VERSION@ - Windows x64
=============================================

Self-contained build: no .NET runtime has to be installed.

Contents
  bin\                              published Agent.Host (BackupPlatform.Agent.Host.exe) and its runtime
  Install-BackupAgentService.ps1    registers the Windows Service "BackupAgent" (account NT SERVICE\BackupAgent)
  Uninstall-BackupAgentService.ps1  removes the service
  config\agent.settings.example.json  configuration keys with placeholders (no secrets)

Install (elevated PowerShell)
  1. Extract this archive to a permanent folder only administrators can write, for example
     C:\Program Files\BackupAgent. The service runs the files in bin\ from where they are.
  2. .\Install-BackupAgentService.ps1 -BinaryDirectory .\bin -BackendUri https://backup.example.com/
  3. Configure staging and storage (config\agent.settings.example.json) and the SQL Server credentials
     (BACKUP_AGENT_SECRET_<reference> service environment variables, JSON {"userName":"...","password":"..."}).
  4. Start-Service BackupAgent

Limitations of this release
  - Agent.Host has no enrollment command. It exits with code 1 until an agent identity is stored for the
    service account (the identity is protected with that account's DPAPI). See the repository README, section
    "Current MVP scope".
  - The binaries are not code-signed.

Verify the download with the SHA256SUMS.txt file attached to the release.
