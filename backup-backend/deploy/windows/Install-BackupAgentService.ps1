<#
.SYNOPSIS
Registers the Backup Platform agent host as a Windows Service.

.DESCRIPTION
The service runs as the virtual account "NT SERVICE\BackupAgent": it needs no password, has no rights beyond what it is granted
explicitly, and is the account whose DPAPI (CurrentUser) keys protect the agent identity. The identity store (default
%ProgramData%\BackupAgent) is created and protected by the agent itself the first time it saves an identity.

The agent identity is bound to this account. Whatever stores it (enrollment) must therefore run as the same account; changing the
service account later orphans the stored identity and requires enrolling again.

The script registers the Application event log source the service logs to (a virtual account may not create one), creates the
service with automatic start and restart-on-failure, and passes the configuration through the service's environment. It does not
start the service.

.PARAMETER BinaryDirectory
Directory holding BackupPlatform.Agent.Host.exe (the published agent host).

.PARAMETER BackendUri
Base address of the backend, https only. Becomes Agent__Backend__BaseUri.

.PARAMETER StoreDirectory
Optional identity store directory. Becomes Agent__CredentialStoreDirectory; the default is %ProgramData%\BackupAgent.

.EXAMPLE
dotnet publish src/hosts/agent/BackupPlatform.Agent.Host.csproj -c Release -r win-x64 --self-contained true -p:AgentPlatform=Windows -o C:\BackupAgent
.\deploy\windows\Install-BackupAgentService.ps1 -BinaryDirectory C:\BackupAgent -BackendUri https://backup.example/

.NOTES
Run this script elevated. Keep the published binaries writable only by administrators, and grant the service account read/execute.
Enrollment must save the identity as NT SERVICE\BackupAgent; a blob created under an interactive user's DPAPI scope cannot be reused.
Start-Service BackupAgent starts the existing Generic Host with the Windows Service lifetime. Stop-Service BackupAgent requests graceful
shutdown; after restart the same DPAPI-protected identity is loaded. Console starts retain Ctrl+C behavior; Linux retains SIGTERM behavior.

Windows validation: dotnet test tests/windows-tests -c Release (net10.0-windows). Run elevated for the cross-account tests; their disposable
local account is granted SeBatchLogonRight only for the test and removed afterwards. Symbolic links additionally need the corresponding
privilege or Developer Mode. On Linux these 36 tests are explicitly skipped, not passed: a Windows worker is required for actual coverage.
Existing identity directories are ACL-checked on every operation; unsafe ownership is refused. A loose ACL is narrowed only for an empty
directory or one containing only store files. Reparse points are refused at the directory, identity file and every ancestor.

#>
#Requires -RunAsAdministrator
[CmdletBinding()]
param(
    [Parameter(Mandatory)] [string] $BinaryDirectory,
    [Parameter(Mandatory)] [string] $BackendUri,
    [string] $StoreDirectory
)

$ErrorActionPreference = 'Stop'
$ServiceName = 'BackupAgent'

$executable = Join-Path (Resolve-Path -LiteralPath $BinaryDirectory).Path 'BackupPlatform.Agent.Host.exe'
if (-not (Test-Path -LiteralPath $executable)) { throw "BackupPlatform.Agent.Host.exe was not found in $BinaryDirectory." }
if ($BackendUri -notmatch '^https://') { throw 'The backend address must use https.' }
if (Get-Service -Name $ServiceName -ErrorAction SilentlyContinue) { throw "The service $ServiceName already exists; run Uninstall-BackupAgentService.ps1 first." }

if (-not [System.Diagnostics.EventLog]::SourceExists($ServiceName)) {
    New-EventLog -LogName Application -Source $ServiceName
}

$account = "NT SERVICE\$ServiceName"
& sc.exe create $ServiceName binPath= "`"$executable`"" start= delayed-auto obj= $account DisplayName= 'Backup Platform Agent' | Out-Null
if ($LASTEXITCODE -ne 0) { throw "sc.exe create failed with exit code $LASTEXITCODE." }
& sc.exe description $ServiceName 'Connects this machine to the Backup Platform and runs backup work for it.' | Out-Null
# Restart after a crash: 5 s, 30 s, then every 60 s; the failure counter resets after a day without failures.
& sc.exe failure $ServiceName reset= 86400 actions= restart/5000/restart/30000/restart/60000 | Out-Null
& sc.exe failureflag $ServiceName 1 | Out-Null

$environment = @("Agent__Backend__BaseUri=$BackendUri")
if ($StoreDirectory) { $environment += "Agent__CredentialStoreDirectory=$StoreDirectory" }
Set-ItemProperty -Path "HKLM:\SYSTEM\CurrentControlSet\Services\$ServiceName" -Name Environment -Type MultiString -Value $environment

Write-Host "Service $ServiceName registered to run as $account. Start it with: Start-Service $ServiceName"
