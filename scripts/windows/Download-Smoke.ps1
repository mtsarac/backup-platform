#Requires -RunAsAdministrator
[CmdletBinding()]
param(
    [Parameter(Mandatory)] [string] $RootDirectory,
    [string] $SqlServiceName = 'MSSQL$MSSQLSERVER25'
)
$ErrorActionPreference = 'Stop'
$root = [IO.Path]::GetFullPath($RootDirectory)
$programData = [IO.Path]::GetFullPath([Environment]::GetFolderPath('CommonApplicationData'))
if ((Split-Path -Parent $root) -ne $programData -or (Split-Path -Leaf $root) -notmatch '^BackupAgentSmoke-[a-f0-9]{12}$') {
    throw 'Use the owned Onboarding-Smoke root.'
}
if (-not (Test-Path -LiteralPath (Join-Path $root 'smoke-owned.json')) -or ((Get-Item -LiteralPath $root -Force).Attributes -band [IO.FileAttributes]::ReparsePoint)) {
    throw 'Refusing an unowned or redirected root.'
}
$service = Get-CimInstance Win32_Service -Filter "Name='BackupAgent'"
$binary = Join-Path $root 'agent files'
$expected = Join-Path $binary 'BackupPlatform.Agent.Host.exe'
if (-not $service -or -not $service.PathName.Trim().Equals('"' + $expected + '"', [StringComparison]::OrdinalIgnoreCase)) {
    throw 'Configure only the service installed by this smoke.'
}
Stop-Service BackupAgent
$stage = Join-Path $root 'staging'
$storage = Join-Path $root 'storage'
foreach ($path in @($stage, $storage)) {
    New-Item -ItemType Directory -Path $path -Force | Out-Null
    & icacls.exe $path /grant 'NT SERVICE\BackupAgent:(OI)(CI)M' /Q | Out-Null
    if ($LASTEXITCODE -ne 0) { throw 'Agent smoke directory access grant failed.' }
}
& icacls.exe $stage /grant "NT SERVICE\${SqlServiceName}:(OI)(CI)M" /Q | Out-Null
if ($LASTEXITCODE -ne 0) { throw 'SQL Server smoke staging access grant failed.' }
$config = @{
    Agent = @{
        Connectivity = @{ HeartbeatInterval = '00:00:05'; PollInterval = '00:00:05'; MinimumPollSpacing = '00:00:01' }
        Backup = @{
            Staging = @{ Mapping = 'SameHost'; AgentDirectory = $stage }
            LocalStorage = @{ RootDirectory = $storage }
            PostgreSql = @{ ClientToolsDirectory = 'C:\Program Files\PostgreSQL\18\bin' }
        }
    }
}
$config | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath (Join-Path $binary 'appsettings.Production.json') -Encoding UTF8
Start-Service BackupAgent
Write-Output 'Owned Windows Agent configured for real MSSQL backup and outbound local download; database logins are entered through the API, never injected into the service.'
