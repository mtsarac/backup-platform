<#
.SYNOPSIS
Stops and removes the Backup Platform agent Windows Service.

.PARAMETER RemoveIdentity
Also deletes the identity store directory. The agent has to be enrolled again afterwards. Without this switch the identity is kept.

.PARAMETER StoreDirectory
The identity store directory removed by -RemoveIdentity. Default %ProgramData%\BackupAgent.
#>
#Requires -RunAsAdministrator
[CmdletBinding()]
param(
    [switch] $RemoveIdentity,
    [string] $StoreDirectory = (Join-Path ([Environment]::GetFolderPath('CommonApplicationData')) 'BackupAgent')
)

$ErrorActionPreference = 'Stop'
$ServiceName = 'BackupAgent'

$service = Get-Service -Name $ServiceName -ErrorAction SilentlyContinue
if ($service) {
    if ($service.Status -ne 'Stopped') {
        Stop-Service -Name $ServiceName -Force
        $service.WaitForStatus('Stopped', [TimeSpan]::FromSeconds(60))
    }
    $service.Dispose()
    & sc.exe delete $ServiceName | Out-Null
    if ($LASTEXITCODE -ne 0) { throw "sc.exe delete failed with exit code $LASTEXITCODE." }
}

if ([System.Diagnostics.EventLog]::SourceExists($ServiceName)) {
    Remove-EventLog -Source $ServiceName
}

if ($RemoveIdentity -and (Test-Path -LiteralPath $StoreDirectory)) {
    $attributes = (Get-Item -LiteralPath $StoreDirectory -Force).Attributes
    if ($attributes -band [IO.FileAttributes]::ReparsePoint) { throw "Refusing to delete ${StoreDirectory}: it is a link." }
    Remove-Item -LiteralPath $StoreDirectory -Recurse -Force
}

Write-Host "Service $ServiceName removed."
