#Requires -RunAsAdministrator
[CmdletBinding()]
param(
    [Parameter(Mandatory)] [string] $RootDirectory,
    [Parameter(Mandatory)] [string] $DatabaseSecretFile,
    [string] $SqlServiceName = 'MSSQL$MSSQLSERVER25',
    [string] $PostgresSecretFile
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
$secretPath = [IO.Path]::GetFullPath($DatabaseSecretFile)
if ((Split-Path -Parent $secretPath) -ne $root -or ((Get-Item -LiteralPath $secretPath -Force).Attributes -band [IO.FileAttributes]::ReparsePoint)) {
    throw 'The disposable credential file must be directly inside the protected smoke root.'
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
        }
    }
}
$config | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath (Join-Path $binary 'appsettings.Production.json') -Encoding UTF8
$key = 'HKLM:\SYSTEM\CurrentControlSet\Services\BackupAgent'
# The service manager owns environment injection. Restrict the temporary service
# registry key before adding SQL credentials; uninstall removes this owned key.
$acl = Get-Acl $key
$acl.SetAccessRuleProtection($true, $false)
foreach ($sidText in @('S-1-5-18', 'S-1-5-32-544')) {
    $sid = New-Object Security.Principal.SecurityIdentifier $sidText
    $acl.AddAccessRule((New-Object Security.AccessControl.RegistryAccessRule($sid, 'FullControl', 'ContainerInherit', 'None', 'Allow')))
}
Set-Acl $key $acl
try {
    $secret = Get-Content -LiteralPath $secretPath -Raw | ConvertFrom-Json
    if (-not $secret.userName -or -not $secret.password) { throw 'SQL smoke credential JSON is invalid.' }
    $environment = @((Get-ItemProperty -Path $key -Name Environment).Environment | Where-Object { -not $_.StartsWith('BACKUP_AGENT_SECRET_sql_smoke=') })
    $environment += 'BACKUP_AGENT_SECRET_sql_smoke=' + ($secret | ConvertTo-Json -Compress)
    if ($PostgresSecretFile) {
        $postgresPath = [IO.Path]::GetFullPath($PostgresSecretFile)
        if ((Split-Path -Parent $postgresPath) -ne $root) { throw 'The disposable credential file must be directly inside the protected smoke root.' }
        $postgres = Get-Content -LiteralPath $postgresPath -Raw | ConvertFrom-Json
        if (-not $postgres.userName -or -not $postgres.password) { throw 'PostgreSQL credential JSON is invalid.' }
        $environment = @($environment | Where-Object { -not $_.StartsWith('BACKUP_AGENT_SECRET_pg_win=') })
        $environment += 'BACKUP_AGENT_SECRET_pg_win=' + ($postgres | ConvertTo-Json -Compress)
    }
    Set-ItemProperty -Path $key -Name Environment -Type MultiString -Value $environment
} finally {
    $secret = $null
    $environment = $null
    $postgres = $null
    if ($postgresPath) { Remove-Item -LiteralPath $postgresPath -Force -ErrorAction SilentlyContinue }
    Remove-Item -LiteralPath $secretPath -Force
}
Start-Service BackupAgent
Write-Output 'Owned Windows Agent configured for real MSSQL backup and outbound local download; temporary SQL credentials are protected and not logged.'
