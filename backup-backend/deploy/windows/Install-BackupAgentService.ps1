<#
.SYNOPSIS
Enrolls and starts BackupAgent as NT SERVICE\BackupAgent, or reinstalls its preserved identity.
.DESCRIPTION
Run elevated from the published agent folder. One-time enrollment is performed by the real Windows Service lifetime,
never by the interactive administrator: permanent identity uses DPAPI CurrentUser of NT SERVICE\BackupAgent.
The one-time request is DPAPI LocalMachine encrypted, staged with only service/SYSTEM/Administrators access,
and removed after completion. Tokens and permanent credentials are never placed in service arguments or registry.
Omit EnrollmentToken to prompt securely for a new identity. An existing identity is preserved and verified instead;
supplying a token with an existing identity is refused before contacting the backend. BackendUri is only needed for new enrollment.
.EXAMPLE
.\deploy\windows\Install-BackupAgentService.ps1 -BinaryDirectory $PWD -BackendUri 'https://backup.example/' -AgentName 'Database host'
#>
#Requires -RunAsAdministrator
[CmdletBinding()]
param(
    [Parameter(Mandatory)] [string] $BinaryDirectory,
    [string] $BackendUri,
    [string] $EnrollmentToken,
    [string] $AgentName,
    [string] $StoreDirectory = (Join-Path ([Environment]::GetFolderPath('CommonApplicationData')) 'BackupAgent'),
    [hashtable] $TrustedUpdateKeys = @{}
)

$ErrorActionPreference = 'Stop'
$ServiceName = 'BackupAgent'
$account = "NT SERVICE\$ServiceName"

function Set-ServiceExecutable([string] $Path) {
    # CIM passes the exact command line to SCM. Windows PowerShell 5 native argument
    # marshalling strips embedded quotes, making sc.exe binPath unsafe for paths with spaces.
    $service = Get-CimInstance Win32_Service -Filter "Name='$ServiceName'"
    $result = Invoke-CimMethod -InputObject $service -MethodName Change -Arguments @{ PathName = $Path }
    if ($result.ReturnValue -ne 0) { throw 'Windows Service executable configuration failed.' }
}

function Assert-SafePath([string] $Path) {
    if (-not [IO.Path]::IsPathRooted($Path) -or $Path.StartsWith('\\')) { throw 'Use an absolute local path.' }
    $full = [IO.Path]::GetFullPath($Path).TrimEnd('\')
    if ($full -eq [IO.Path]::GetPathRoot($full).TrimEnd('\')) { throw 'The filesystem root is not an agent directory.' }
    for ($current = $full; $current; $current = [IO.Path]::GetDirectoryName($current)) {
        if ((Test-Path -LiteralPath $current) -and ((Get-Item -LiteralPath $current -Force).Attributes -band [IO.FileAttributes]::ReparsePoint)) {
            throw 'Agent paths must not contain a reparse point.'
        }
    }
    return $full
}

function New-ProtectedDirectory([string] $Path, [Security.Principal.SecurityIdentifier] $ServiceSid) {
    $security = New-Object Security.AccessControl.DirectorySecurity
    $security.SetOwner((New-Object Security.Principal.SecurityIdentifier 'S-1-5-32-544'))
    $security.SetAccessRuleProtection($true, $false)
    foreach ($sid in @($ServiceSid, (New-Object Security.Principal.SecurityIdentifier 'S-1-5-18'), (New-Object Security.Principal.SecurityIdentifier 'S-1-5-32-544'))) {
        $rule = New-Object Security.AccessControl.FileSystemAccessRule($sid, 'FullControl', 'ContainerInherit,ObjectInherit', 'None', 'Allow')
        $security.AddAccessRule($rule)
    }
    $null = [IO.Directory]::CreateDirectory($Path)
    Set-Acl -Path $Path -AclObject $security
}
function Assert-OutsideBinaries([string] $Path) {
    if ($Path.Equals($binary, [StringComparison]::OrdinalIgnoreCase) -or
        $Path.StartsWith("$binary\", [StringComparison]::OrdinalIgnoreCase) -or
        $binary.StartsWith("$Path\", [StringComparison]::OrdinalIgnoreCase)) {
        throw 'Mutable identity/update staging paths must be separate from the protected binary tree.'
    }
}

$binary = Assert-SafePath ([IO.Path]::GetFullPath($BinaryDirectory))
$store = Assert-SafePath $StoreDirectory
Assert-OutsideBinaries $store
$executable = Join-Path $binary 'BackupPlatform.Agent.Host.exe'
if (-not (Test-Path -LiteralPath $executable -PathType Leaf)) { throw 'BackupPlatform.Agent.Host.exe was not found in BinaryDirectory.' }
$null = Assert-SafePath $executable
# Refuse executable replacement by an ordinary account rather than quietly installing a privileged service over it.
$installerSid = [Security.Principal.WindowsIdentity]::GetCurrent().User.Value
$trustedBinaryOwners = @('S-1-5-18', 'S-1-5-32-544', $installerSid)
$trustedBinaryOwners += (New-Object Security.Principal.NTAccount 'NT SERVICE\TrustedInstaller').Translate([Security.Principal.SecurityIdentifier]).Value
$writeRights = [Security.AccessControl.FileSystemRights]::Write -bor [Security.AccessControl.FileSystemRights]::Delete -bor [Security.AccessControl.FileSystemRights]::DeleteSubdirectoriesAndFiles -bor [Security.AccessControl.FileSystemRights]::ChangePermissions -bor [Security.AccessControl.FileSystemRights]::TakeOwnership
foreach ($item in @((Get-Item -LiteralPath $binary)) + @(Get-ChildItem -LiteralPath $binary -Recurse -Force)) {
    if ($item.Attributes -band [IO.FileAttributes]::ReparsePoint) { throw 'The published binary tree must not contain reparse points.' }
    $acl = Get-Acl -LiteralPath $item.FullName
    if ($trustedBinaryOwners -notcontains $acl.GetOwner([Security.Principal.SecurityIdentifier]).Value) { throw 'The published binary tree has an untrusted owner. Publish under an administrator-controlled directory.' }
    foreach ($rule in $acl.GetAccessRules($true, $true, [Security.Principal.SecurityIdentifier])) {
        if ($rule.AccessControlType -eq 'Allow' -and ($rule.FileSystemRights -band $writeRights) -and $trustedBinaryOwners -notcontains $rule.IdentityReference.Value) {
            throw 'The published binary tree is writable by an untrusted account. Publish under an administrator-controlled directory.'
        }
    }
}
$replacementRights = [Security.AccessControl.FileSystemRights]::Delete -bor [Security.AccessControl.FileSystemRights]::DeleteSubdirectoriesAndFiles -bor [Security.AccessControl.FileSystemRights]::ChangePermissions -bor [Security.AccessControl.FileSystemRights]::TakeOwnership
for ($ancestor = [IO.Path]::GetDirectoryName($binary); $ancestor; $ancestor = [IO.Path]::GetDirectoryName($ancestor)) {
    $acl = Get-Acl -LiteralPath $ancestor
    if ($trustedBinaryOwners -notcontains $acl.GetOwner([Security.Principal.SecurityIdentifier]).Value) { throw 'The protected updater requires administrator-controlled ancestor directories.' }
    foreach ($rule in $acl.GetAccessRules($true, $true, [Security.Principal.SecurityIdentifier])) {
        if ($rule.AccessControlType -eq 'Allow' -and -not ($rule.PropagationFlags -band [Security.AccessControl.PropagationFlags]::InheritOnly) -and
            ($rule.FileSystemRights -band $replacementRights) -and $trustedBinaryOwners -notcontains $rule.IdentityReference.Value) {
            throw 'An untrusted account can replace an ancestor of the privileged updater. Install under Program Files.'
        }
    }
}
if (Get-Service -Name $ServiceName -ErrorAction SilentlyContinue) { throw 'BackupAgent already exists. Uninstall without -RemoveIdentity before reinstalling; its identity will be preserved.' }
$hasIdentity = Test-Path -LiteralPath (Join-Path $store 'agent-identity.bin')
if ($hasIdentity -and $EnrollmentToken) { throw 'This store is already enrolled. Identity was preserved and the token was not used. Omit EnrollmentToken to reinstall.' }
if (-not $hasIdentity) {
    $uri = $null
    if (-not [Uri]::TryCreate($BackendUri, [UriKind]::Absolute, [ref]$uri) -or $uri.Scheme -ne 'https' -or -not $uri.Host -or $uri.UserInfo -or $uri.Query -or $uri.Fragment) {
        throw 'BackendUri must be an absolute HTTPS address without user information, query or fragment.'
    }
    if (-not $EnrollmentToken) {
        $secureToken = Read-Host 'Enrollment token (hidden)' -AsSecureString
        $pointer = [Runtime.InteropServices.Marshal]::SecureStringToBSTR($secureToken)
        try { $EnrollmentToken = [Runtime.InteropServices.Marshal]::PtrToStringBSTR($pointer) }
        finally { [Runtime.InteropServices.Marshal]::ZeroFreeBSTR($pointer); $secureToken.Dispose() }
    }
    if ($EnrollmentToken -notmatch '^[0-9a-fA-F]{32}\.[A-Za-z0-9_-]{43}$' -or $EnrollmentToken.StartsWith('00000000000000000000000000000000.')) {
        throw 'The enrollment token is malformed. Copy the complete token from the wizard.'
    }
}
if ($AgentName -and ($AgentName.Length -gt 255 -or $AgentName -match '[\x00-\x1F\x7F]' -or -not $AgentName.Trim())) { throw 'AgentName must contain 1-255 characters without control characters.' }

if (-not [Diagnostics.EventLog]::SourceExists($ServiceName)) { New-EventLog -LogName Application -Source $ServiceName }
$creation = Invoke-CimMethod -ClassName Win32_Service -MethodName Create -Arguments @{
    Name = $ServiceName
    DisplayName = 'Backup Platform Agent'
    PathName = "`"$executable`""
    ServiceType = [byte] 16
    ErrorControl = [byte] 1
    StartMode = 'Manual'
    StartName = $account
}
if ($creation.ReturnValue -ne 0) { throw 'Windows Service registration failed.' }
$serviceSid = (New-Object Security.Principal.NTAccount $account).Translate([Security.Principal.SecurityIdentifier])
$stage = "$store.enrollment-$([Guid]::NewGuid().ToString('N'))"
$registered = $true
try {
    # Existing store protection is checked by the service itself, not rewritten by the interactive installer.
    if (-not (Test-Path -LiteralPath $store)) { New-ProtectedDirectory $store $serviceSid }
    New-ProtectedDirectory $stage $serviceSid
    # Grant only read/execute on published binaries; do not grant the service write access to its executable.
    & icacls.exe $binary /grant "${account}:(OI)(CI)RX" /T /Q | Out-Null
    if ($LASTEXITCODE -ne 0) { throw 'The service could not be granted read/execute access to the published binaries.' }
    $updateStage = Assert-SafePath "$store.updates"
    Assert-OutsideBinaries $updateStage
    New-ProtectedDirectory $updateStage $serviceSid
    $control = Join-Path $binary 'update-control'
    $releases = Join-Path $binary 'releases'
    $null = Assert-SafePath $control
    $null = Assert-SafePath $releases
    $null = New-Item -ItemType Directory -Path $control -Force
    $null = New-Item -ItemType Directory -Path $releases -Force
    # These inherit only the binary tree's administrator/SYSTEM writes and the service's RX.
    $settingsPath = Join-Path $control 'update-settings.json'
    if ((Test-Path -LiteralPath $settingsPath) -and $TrustedUpdateKeys.Count -eq 0) {
        $saved = Get-Content -LiteralPath $settingsPath -Raw | ConvertFrom-Json
        foreach ($key in $saved.TrustedKeys.PSObject.Properties) { $TrustedUpdateKeys[$key.Name] = $key.Value }
    }
    $settings = @{ Root = $binary; Staging = $updateStage; Store = $store; RuntimeConfiguration = (Join-Path $binary 'appsettings.json'); TrustedKeys = $TrustedUpdateKeys }
    [IO.File]::WriteAllText($settingsPath, ($settings | ConvertTo-Json -Depth 5), (New-Object Text.UTF8Encoding $false))
    $releaseVersion = [Diagnostics.FileVersionInfo]::GetVersionInfo($executable).ProductVersion.Split('+')[0]
    $parsedVersion = $null
    if (-not [Version]::TryParse($releaseVersion, [ref]$parsedVersion) -or $parsedVersion.ToString() -ne $releaseVersion) { throw 'Publish a canonical numeric agent version before installing.' }
    $active = @{ Version = $releaseVersion; Executable = $executable } | ConvertTo-Json
    [IO.File]::WriteAllText((Join-Path $control 'active.json'), $active, (New-Object Text.UTF8Encoding $false))
    $action = New-ScheduledTaskAction -Execute $executable -Argument "--apply-update `"$settingsPath`"" -WorkingDirectory $binary
    $principal = New-ScheduledTaskPrincipal -UserId 'SYSTEM' -LogonType ServiceAccount -RunLevel Highest
    $taskSettings = New-ScheduledTaskSettingsSet -MultipleInstances IgnoreNew -ExecutionTimeLimit (New-TimeSpan -Minutes 10) -RestartCount 3 -RestartInterval (New-TimeSpan -Minutes 1)
    $null = Register-ScheduledTask -TaskName 'BackupAgentUpdate' -Action $action -Principal $principal -Settings $taskSettings -Trigger (New-ScheduledTaskTrigger -AtStartup) -Force
    $scheduler = New-Object -ComObject 'Schedule.Service'
    $scheduler.Connect()
    $task = $scheduler.GetFolder('\').GetTask('BackupAgentUpdate')
    $task.SetSecurityDescriptor("D:P(A;;GA;;;SY)(A;;GA;;;BA)(A;;GRGX;;;$($serviceSid.Value))", 0)
    Add-Type -AssemblyName System.Security
    $request = @{ server = $BackendUri; token = $EnrollmentToken; name = $AgentName } | ConvertTo-Json -Compress
    $plain = [Text.Encoding]::UTF8.GetBytes($request)
    $entropy = [Text.Encoding]::UTF8.GetBytes('backup-platform/agent/enrollment-request/v1')
    try { $encrypted = [Security.Cryptography.ProtectedData]::Protect($plain, $entropy, [Security.Cryptography.DataProtectionScope]::LocalMachine) }
    finally { [Array]::Clear($plain, 0, $plain.Length); $request = $null; $EnrollmentToken = $null }
    $requestPath = Join-Path $stage 'request.bin'
    [IO.File]::WriteAllBytes($requestPath, $encrypted)
    $environment = @("Agent__CredentialStoreDirectory=$store", "Agent__Updates__SettingsFile=$settingsPath")
    if ($BackendUri -and -not $hasIdentity) { $environment += "Agent__Backend__BaseUri=$BackendUri" }
    Set-ItemProperty -Path "HKLM:\SYSTEM\CurrentControlSet\Services\$ServiceName" -Name Environment -Type MultiString -Value $environment
    Set-ServiceExecutable "`"$executable`" --service-enroll `"$requestPath`" `"$store`""
    Start-Service $ServiceName
    $resultPath = Join-Path $stage 'result.json'
    $deadline = [DateTime]::UtcNow.AddSeconds(120)
    while (-not (Test-Path -LiteralPath $resultPath)) {
        if ([DateTime]::UtcNow -ge $deadline) { throw 'Service enrollment timed out. Any saved identity was preserved; check the wizard before retrying.' }
        Start-Sleep -Milliseconds 250
    }
    $service = Get-Service $ServiceName
    $service.WaitForStatus('Stopped', [TimeSpan]::FromSeconds(30))
    $service.Dispose()
    $result = Get-Content -LiteralPath $resultPath -Raw | ConvertFrom-Json
    if (-not $result.success) { throw $result.message }
    Set-ServiceExecutable "`"$executable`""
    & sc.exe config $ServiceName start= delayed-auto | Out-Null
    if ($LASTEXITCODE -ne 0) { throw 'Windows Service runtime configuration failed.' }
    & sc.exe description $ServiceName 'Connects this machine to the Backup Platform and runs backup work for it.' | Out-Null
    & sc.exe failure $ServiceName reset= 86400 actions= restart/5000/restart/30000/restart/60000 | Out-Null
    & sc.exe failureflag $ServiceName 1 | Out-Null
    Start-Service $ServiceName
    Write-Host 'BackupAgent identity verified and service started as NT SERVICE\BackupAgent. The backend will show Online after heartbeat and capability sync.'
}
finally {
    # Remove the secret staging request even on failure; retain identity and the stopped service for diagnosis/reinstall.
    if ($registered -and (Test-Path -LiteralPath $stage)) {
        $running = Get-Service $ServiceName -ErrorAction SilentlyContinue
        if ($running -and $running.Status -ne 'Stopped' -and -not (Test-Path -LiteralPath (Join-Path $stage 'result.json'))) {
            Stop-Service $ServiceName -Force -ErrorAction SilentlyContinue
        }
        try { Set-ServiceExecutable "`"$executable`"" }
        finally {
            $null = Assert-SafePath $stage
            Remove-Item -LiteralPath $stage -Recurse -Force
        }
    }
}
