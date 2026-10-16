#Requires -RunAsAdministrator
# A disposable Windows Agent run as a hidden console process (not a service) for the R1 acceptance. The VM keeps its own BackupAgent
# service and identity untouched: this script never registers, stops or reads any service, and only ever kills the one executable
# inside its own BackupAgentSmoke-<12 hex> root. Secrets stay in files protected by the root's administrator-only ACL and are removed
# with the root by 'cleanup'.
[CmdletBinding()]
param(
    [Parameter(Mandatory)] [ValidateSet('prepare', 'stage', 'enroll', 'start', 'stop', 'cleanup')] [string] $Action,
    [Parameter(Mandatory)] [string] $RootDirectory,
    [string] $BackendUri,
    [string] $TokenFile,
    [string] $AgentName = 'R1 console agent',
    [string] $SqlServiceName = 'MSSQL$MSSQLSERVER25'
)
$ErrorActionPreference = 'Stop'
# Errors travel on stdout: stderr arrives as a CLIXML progress stream that hides them.
trap { Write-Output ('R1-AGENT-ERROR: ' + $_.Exception.Message); exit 1 }
$root = [IO.Path]::GetFullPath($RootDirectory)
$programData = [IO.Path]::GetFullPath([Environment]::GetFolderPath('CommonApplicationData'))
if ((Split-Path -Parent $root) -ne $programData -or (Split-Path -Leaf $root) -notmatch '^BackupAgentSmoke-[a-f0-9]{12}$') {
    throw 'Use a unique direct ProgramData child named BackupAgentSmoke-<12 lowercase hex digits>.'
}
$marker = Join-Path $root 'smoke-owned.json'
$binary = Join-Path $root 'agent files'
$exe = Join-Path $binary 'BackupPlatform.Agent.Host.exe'
$identity = Join-Path $root 'identity'
$stage = Join-Path $root 'staging'
$storage = Join-Path $root 'storage'
function Assert-OwnedRoot {
    if (-not (Test-Path -LiteralPath $marker -PathType Leaf) -or ((Get-Item -LiteralPath $root -Force).Attributes -band [IO.FileAttributes]::ReparsePoint)) {
        throw 'Refusing to touch an unowned or redirected smoke directory.'
    }
}
function Stop-OwnedAgent {
    # Only this root's executable: the VM's own service process has another path and is never matched.
    Get-Process -Name 'BackupPlatform.Agent.Host' -ErrorAction SilentlyContinue | Where-Object { $_.Path -and $_.Path.Equals($exe, [StringComparison]::OrdinalIgnoreCase) } | Stop-Process -Force
}
switch ($Action) {
    'prepare' {
        if (Test-Path -LiteralPath $root) { throw 'Smoke directory already exists; choose a fresh run identifier.' }
        New-Item -ItemType Directory -Path $root | Out-Null
        $acl = New-Object Security.AccessControl.DirectorySecurity
        $acl.SetAccessRuleProtection($true, $false)
        $acl.SetOwner((New-Object Security.Principal.SecurityIdentifier('S-1-5-32-544')))
        foreach ($sid in @('S-1-5-18', 'S-1-5-32-544')) {
            $acl.AddAccessRule((New-Object Security.AccessControl.FileSystemAccessRule((New-Object Security.Principal.SecurityIdentifier($sid)), 'FullControl', 'ContainerInherit,ObjectInherit', 'None', 'Allow')))
        }
        Set-Acl -LiteralPath $root -AclObject $acl
        [IO.File]::WriteAllText($marker, '{}')
        foreach ($path in @($stage, $storage, $identity)) { New-Item -ItemType Directory -Path $path | Out-Null }
        & icacls.exe $stage /grant "NT SERVICE\${SqlServiceName}:(OI)(CI)M" /Q | Out-Null
        if ($LASTEXITCODE -ne 0) { throw 'SQL Server staging access grant failed.' }
        Write-Output 'Owned administrator-only console-agent root prepared.'
    }
    'stage' {
        Assert-OwnedRoot
        Expand-Archive -LiteralPath (Join-Path $root 'agent.zip') -DestinationPath $binary
        $certificate = Import-Certificate -FilePath (Join-Path $root 'certificate.pem') -CertStoreLocation Cert:\LocalMachine\Root
        @{ certificateThumbprint = $certificate.Thumbprint } | ConvertTo-Json | Set-Content -LiteralPath $marker
        Write-Output 'Agent staged; disposable smoke certificate trusted (validation remains enabled).'
    }
    'enroll' {
        Assert-OwnedRoot
        if (-not $BackendUri -or -not $TokenFile) { throw 'enroll requires BackendUri and a protected runtime TokenFile.' }
        $token = ((Get-Content -LiteralPath $TokenFile -Raw | ConvertFrom-Json).token)
        try {
            & $exe enroll --server $BackendUri --token $token --name $AgentName --store-directory $identity --non-interactive --console
            if ($LASTEXITCODE -ne 0) { throw 'Agent enrollment CLI failed; see the secret-free diagnostic above.' }
        } finally {
            $token = $null
            Remove-Item -LiteralPath $TokenFile -Force
        }
        Write-Output 'Console agent enrolled.'
    }
    'start' {
        Assert-OwnedRoot
        if (-not $BackendUri) { throw 'start requires BackendUri.' }
        Stop-OwnedAgent
        $config = @{ Agent = @{
            Backend = @{ BaseUri = $BackendUri }
            CredentialStoreDirectory = $identity
            Connectivity = @{ HeartbeatInterval = '00:00:05'; PollInterval = '00:00:05'; MinimumPollSpacing = '00:00:01' }
            Backup = @{ Staging = @{ Mapping = 'SameHost'; AgentDirectory = $stage }; LocalStorage = @{ RootDirectory = $storage } }
        } }
        $config | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath (Join-Path $binary 'appsettings.Production.json') -Encoding UTF8
        $launcher = @"
`$ErrorActionPreference = 'Stop'
Start-Process -FilePath '$exe' -WorkingDirectory '$binary' -WindowStyle Hidden -RedirectStandardOutput '$(Join-Path $root 'agent.log')' -RedirectStandardError '$(Join-Path $root 'agent-err.log')'
"@
        Set-Content -LiteralPath (Join-Path $root 'launch.ps1') -Value $launcher -Encoding UTF8
        # Win32_Process.Create detaches the agent from this SSH session's job object, so it survives the session ending.
        $result = Invoke-CimMethod -ClassName Win32_Process -MethodName Create -Arguments @{ CommandLine = "powershell.exe -NoProfile -ExecutionPolicy Bypass -File `"$(Join-Path $root 'launch.ps1')`"" }
        if ($result.ReturnValue -ne 0) { throw 'Could not launch the console agent.' }
        $deadline = (Get-Date).AddSeconds(30)
        while ((Get-Date) -lt $deadline) {
            if (Get-Process -Name 'BackupPlatform.Agent.Host' -ErrorAction SilentlyContinue | Where-Object { $_.Path -and $_.Path.Equals($exe, [StringComparison]::OrdinalIgnoreCase) }) { Write-Output 'Console agent running.'; return }
            Start-Sleep -Milliseconds 500
        }
        throw 'The console agent did not start.'
    }
    'stop' {
        Assert-OwnedRoot
        Stop-OwnedAgent
        Write-Output 'Console agent stopped.'
    }
    'cleanup' {
        Assert-OwnedRoot
        Stop-OwnedAgent
        Start-Sleep -Seconds 2
        $state = Get-Content -Raw -LiteralPath $marker | ConvertFrom-Json
        if ($state.certificateThumbprint) {
            $certificatePath = 'Cert:\LocalMachine\Root\' + $state.certificateThumbprint
            if (Test-Path -LiteralPath $certificatePath) { Remove-Item -LiteralPath $certificatePath -Force }
        }
        Remove-Item -LiteralPath $root -Recurse -Force
        Write-Output 'Owned console-agent files and disposable trust anchor removed.'
    }
}
