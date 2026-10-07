<#
.SYNOPSIS
Stops and removes BackupAgent. Identity and its saved backend address are preserved by default for reinstall.
.PARAMETER RemoveIdentity
Explicitly purges the agent identity store. Alias: PurgeIdentity. Requires enrolling again.
.PARAMETER StoreDirectory
Identity store to purge. Defaults to the registered service's store, or %ProgramData%\BackupAgent.
#>
#Requires -RunAsAdministrator
[CmdletBinding()]
param(
    [Alias('PurgeIdentity')] [switch] $RemoveIdentity,
    [string] $StoreDirectory
)

$ErrorActionPreference = 'Stop'
$ServiceName = 'BackupAgent'
if (-not $StoreDirectory) {
    $environment = (Get-ItemProperty -Path "HKLM:\SYSTEM\CurrentControlSet\Services\$ServiceName" -Name Environment -ErrorAction SilentlyContinue).Environment
    $setting = @($environment | Where-Object { $_ -like 'Agent__CredentialStoreDirectory=*' }) | Select-Object -First 1
    $StoreDirectory = if ($setting) { $setting.Substring('Agent__CredentialStoreDirectory='.Length) } else { Join-Path ([Environment]::GetFolderPath('CommonApplicationData')) 'BackupAgent' }
}

function Assert-PurgeStore([string] $Path) {
    if (-not [IO.Path]::IsPathRooted($Path) -or $Path.StartsWith('\\')) { throw 'Identity purge requires an absolute local directory.' }
    $full = [IO.Path]::GetFullPath($Path).TrimEnd('\')
    $forbidden = @([IO.Path]::GetPathRoot($full).TrimEnd('\'), $env:WINDIR, $env:ProgramFiles, ${env:ProgramFiles(x86)}, [Environment]::GetFolderPath('CommonApplicationData'), $env:USERPROFILE)
    if ($forbidden -contains $full) { throw 'Refusing to purge a system, root or profile directory.' }
    for ($current = $full; $current; $current = [IO.Path]::GetDirectoryName($current)) {
        if ((Test-Path -LiteralPath $current) -and ((Get-Item -LiteralPath $current -Force).Attributes -band [IO.FileAttributes]::ReparsePoint)) { throw 'Refusing identity purge through a reparse point.' }
    }
    if (Test-Path -LiteralPath $full) {
        if (-not (Test-Path -LiteralPath $full -PathType Container)) { throw 'Identity store must be a directory.' }
        foreach ($item in Get-ChildItem -LiteralPath $full -Force) {
            if ($item.Attributes -band [IO.FileAttributes]::ReparsePoint) { throw 'Refusing identity purge containing a reparse point.' }
            if ($item.PSIsContainer -or ($item.Name -ne 'agent-identity.bin' -and $item.Name -ne '.enrollment.lock' -and $item.Name -notmatch '^\.agent-identity\.bin\.[a-fA-F0-9]{32}\.tmp$')) {
                throw 'Refusing identity purge: this directory contains data not owned by the agent credential store.'
            }
        }
    }
    return $full
}

# Validate all purge targets before stopping/removing the service.
if ($RemoveIdentity) { $StoreDirectory = Assert-PurgeStore $StoreDirectory }
$service = Get-Service -Name $ServiceName -ErrorAction SilentlyContinue
if ($service) {
    if ($service.Status -ne 'Stopped') {
        Stop-Service -Name $ServiceName -Force
        $service.WaitForStatus('Stopped', [TimeSpan]::FromSeconds(60))
    }
    $service.Dispose()
    & sc.exe delete $ServiceName | Out-Null
    if ($LASTEXITCODE -ne 0) { throw 'Windows Service removal failed.' }
}
if ([Diagnostics.EventLog]::SourceExists($ServiceName)) { Remove-EventLog -Source $ServiceName }
if ($RemoveIdentity) {
    $StoreDirectory = Assert-PurgeStore $StoreDirectory
    if (Test-Path -LiteralPath $StoreDirectory) { Remove-Item -LiteralPath $StoreDirectory -Recurse -Force }
    Write-Host 'BackupAgent removed. Identity explicitly purged; a new enrollment token is required.'
}
else {
    Write-Host 'BackupAgent removed. Identity and saved backend address preserved for reinstall. Use -RemoveIdentity (alias -PurgeIdentity) only to explicitly erase enrollment.'
}
