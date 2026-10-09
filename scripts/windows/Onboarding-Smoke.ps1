#Requires -RunAsAdministrator
[CmdletBinding()]
param(
    [Parameter(Mandatory)] [ValidateSet('prepare', 'stage', 'install', 'reinstall', 'verify', 'uninstall', 'purge', 'cleanup')] [string] $Action,
    [Parameter(Mandatory)] [string] $RootDirectory,
    [string] $BackendUri,
    [string] $TokenFile,
    [string] $AgentName = 'Windows onboarding smoke'
)
$ErrorActionPreference = 'Stop'
$root = [IO.Path]::GetFullPath($RootDirectory)
$programData = [IO.Path]::GetFullPath([Environment]::GetFolderPath('CommonApplicationData'))
if ((Split-Path -Parent $root) -ne $programData -or (Split-Path -Leaf $root) -notmatch '^BackupAgentSmoke-[a-f0-9]{12}$') {
    throw 'Use a unique direct ProgramData child named BackupAgentSmoke-<12 lowercase hex digits>.'
}
$marker = Join-Path $root 'smoke-owned.json'
# Exercise the real installer with spaces in its binary path, not only a simplified fixture path.
$binaryDirectory = Join-Path $root 'agent files'
$storeDirectory = Join-Path $root 'identity'
$installer = Join-Path $binaryDirectory 'deploy\windows\Install-BackupAgentService.ps1'
$uninstaller = Join-Path $binaryDirectory 'deploy\windows\Uninstall-BackupAgentService.ps1'
function Assert-OwnedRoot {
    if (-not (Test-Path -LiteralPath $marker -PathType Leaf) -or ((Get-Item -LiteralPath $root -Force).Attributes -band [IO.FileAttributes]::ReparsePoint)) {
        throw 'Refusing to touch an unowned or redirected smoke directory.'
    }
}
function Assert-OwnedService {
    $service = Get-CimInstance Win32_Service -Filter "Name='BackupAgent'"
    $expectedExecutable = Join-Path $binaryDirectory 'BackupPlatform.Agent.Host.exe'
    $command = if ($service) { $service.PathName.Trim() } else { '' }
    $registeredExecutable = if ($command.StartsWith('"')) { ($command -split '"')[1] } else { ($command -split ' ')[0] }
    if ($service -and -not $registeredExecutable.Equals($expectedExecutable, [StringComparison]::OrdinalIgnoreCase)) {
        throw 'Refusing to touch a BackupAgent service outside this smoke run.'
    }
    return $service
}
switch ($Action) {
    'prepare' {
        if (Test-Path -LiteralPath $root) { throw 'Smoke directory already exists; choose a fresh run identifier.' }
        if (Get-Service BackupAgent -ErrorAction SilentlyContinue) { throw 'Existing BackupAgent service must not be replaced by acceptance.' }
        New-Item -ItemType Directory -Path $root | Out-Null
        $acl = New-Object Security.AccessControl.DirectorySecurity
        $acl.SetAccessRuleProtection($true, $false)
        $administrators = New-Object Security.Principal.SecurityIdentifier('S-1-5-32-544')
        $acl.SetOwner($administrators)
        foreach ($sid in @('S-1-5-18', 'S-1-5-32-544')) {
            $rule = New-Object Security.AccessControl.FileSystemAccessRule((New-Object Security.Principal.SecurityIdentifier($sid)), 'FullControl', 'ContainerInherit,ObjectInherit', 'None', 'Allow')
            $acl.AddAccessRule($rule)
        }
        Set-Acl -LiteralPath $root -AclObject $acl
        [IO.File]::WriteAllText($marker, '{}')
        Write-Output 'Owned administrator-only smoke staging prepared.'
    }
    'stage' {
        Assert-OwnedRoot
        Expand-Archive -LiteralPath (Join-Path $root 'agent.zip') -DestinationPath $binaryDirectory
        $certificate = Import-Certificate -FilePath (Join-Path $root 'certificate.pem') -CertStoreLocation Cert:\LocalMachine\Root
        @{ certificateThumbprint = $certificate.Thumbprint } | ConvertTo-Json | Set-Content -LiteralPath $marker
        Write-Output 'Published agent staged; disposable smoke certificate trusted (validation remains enabled).'
    }
    'install' {
        Assert-OwnedRoot
        Assert-OwnedService | Out-Null
        if (-not $BackendUri -or -not $TokenFile) { throw 'install requires BackendUri and a protected runtime TokenFile.' }
        $token = (Get-Content -Raw -LiteralPath $TokenFile | ConvertFrom-Json).token
        if (-not $token) { throw 'Token file has no token.' }
        try {
            & (Join-Path $binaryDirectory 'BackupPlatform.Agent.Host.exe') enroll --server $BackendUri --token $token --name $AgentName --store-directory $storeDirectory --non-interactive
            if ($LASTEXITCODE -ne 0) { throw 'Agent enrollment CLI failed; see the secret-free diagnostic above.' }
        } finally { $token = $null; Remove-Item -LiteralPath $TokenFile -Force }
    }
    'reinstall' {
        Assert-OwnedRoot
        Assert-OwnedService | Out-Null
        & $installer -BinaryDirectory $binaryDirectory -StoreDirectory $storeDirectory
    }
    'verify' {
        Assert-OwnedRoot
        $service = Assert-OwnedService
        if (-not $service -or $service.State -ne 'Running' -or $service.StartMode -ne 'Auto' -or $service.StartName -ne 'NT SERVICE\BackupAgent') {
            throw 'Service must be Running with automatic startup under NT SERVICE\BackupAgent.'
        }
        if (-not (Get-Item -LiteralPath (Join-Path $storeDirectory 'agent-identity.bin')).Length) { throw 'Encrypted identity is missing or empty.' }
        Write-Output 'Service registration, running state, automatic startup and virtual identity verified.'
        Write-Output 'DPAPI decryptability/auth/heartbeat/capabilities must additionally be proved by the authenticated backend and frontend; file presence alone is not proof.'
    }
    'uninstall' {
        Assert-OwnedRoot
        Assert-OwnedService | Out-Null
        $hadIdentity = Test-Path -LiteralPath (Join-Path $storeDirectory 'agent-identity.bin')
        & $uninstaller -StoreDirectory $storeDirectory
        if (Get-Service BackupAgent -ErrorAction SilentlyContinue) { throw 'Service registration remains after uninstall.' }
        if ($hadIdentity -and -not (Test-Path -LiteralPath (Join-Path $storeDirectory 'agent-identity.bin'))) { throw 'Default uninstall unexpectedly removed identity.' }
        Write-Output 'Uninstall removed registration; any existing identity was preserved.'
    }
    'purge' {
        Assert-OwnedRoot
        Assert-OwnedService | Out-Null
        & $uninstaller -StoreDirectory $storeDirectory -RemoveIdentity
        if (Test-Path -LiteralPath $storeDirectory) { throw 'Explicit purge left the identity directory behind.' }
        Write-Output 'Explicit purge removed local identity.'
    }
    'cleanup' {
        Assert-OwnedRoot
        if (Assert-OwnedService) { throw 'Uninstall the owned service before cleanup.' }
        $state = Get-Content -Raw -LiteralPath $marker | ConvertFrom-Json
        if ($state.certificateThumbprint) {
            $certificatePath = 'Cert:\LocalMachine\Root\' + $state.certificateThumbprint
            if (Test-Path -LiteralPath $certificatePath) { Remove-Item -LiteralPath $certificatePath -Force }
        }
        Remove-Item -LiteralPath $root -Recurse -Force
        Write-Output 'Owned smoke files and disposable trust anchor removed.'
    }
}
