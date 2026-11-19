<#
.SYNOPSIS
Installs the agent using its real Windows Service enrollment (DPAPI CurrentUser).
.DESCRIPTION
Run in elevated Windows PowerShell. Use -Interactive for guided installation, or
-NonInteractive -Server HTTPS_URL -Token TOKEN for unattended installation.
An existing installation (BackupAgent service, %ProgramFiles%\BackupAgent, %ProgramData%\BackupAgent) is only replaced
after the new package was downloaded and validated, and only with consent: with a terminal the script asks (default
no); without one it refuses unless -ReplaceExisting (yes without asking) is given. Replacing removes the service,
binaries and the agent identity (a new token is enrolled); backups already stored in backup storage locations are not deleted.
#>
[CmdletBinding()]
param(
    [string] $Server,
    [string] $Token,
    [string] $AgentName,
    [switch] $Interactive,
    [switch] $NonInteractive,
    [switch] $ReplaceExisting,
    [ValidateSet('tr', 'en')] [string] $Language = 'en'
)
$ErrorActionPreference = 'Stop'
function Text([string] $English, [string] $Turkish) {
    if ($Language -eq 'tr') { return $Turkish }
    return $English
}
function Fail([string] $English, [string] $Turkish) { throw (Text $English $Turkish) }
function Assert-SafePath([string] $Path) {
    $full = [IO.Path]::GetFullPath($Path)
    if (-not [IO.Path]::IsPathRooted($Path) -or $full.StartsWith('\\')) { Fail 'Use an absolute local installation path.' 'Mutlak ve yerel bir kurulum yolu kullanin.' }
    for ($current = $full; $current; $current = [IO.Path]::GetDirectoryName($current)) {
        if ((Test-Path -LiteralPath $current) -and ((Get-Item -LiteralPath $current -Force).Attributes -band [IO.FileAttributes]::ReparsePoint)) {
            Fail 'Installation paths must not contain reparse points.' 'Kurulum yollari yeniden ayristirma noktasi icermemelidir.'
        }
    }
    return $full
}
function New-ProtectedDirectory([string] $Path) {
    $security = New-Object Security.AccessControl.DirectorySecurity
    $administrators = New-Object Security.Principal.SecurityIdentifier 'S-1-5-32-544'
    $security.SetOwner($administrators)
    $security.SetAccessRuleProtection($true, $false)
    foreach ($sid in @($administrators, (New-Object Security.Principal.SecurityIdentifier 'S-1-5-18'))) {
        $security.AddAccessRule((New-Object Security.AccessControl.FileSystemAccessRule($sid, 'FullControl', 'ContainerInherit,ObjectInherit', 'None', 'Allow')))
    }
    $null = [IO.Directory]::CreateDirectory($Path)
    Set-Acl -Path $Path -AclObject $security
}
function Refuse-Existing { Fail 'An existing agent installation was found; nothing was replaced. Uninstall the existing agent first, then run this installer again. Alternatively run it in a terminal to be asked, or add -ReplaceExisting to remove the existing agent and reinstall without asking.' 'Mevcut bir ajan kurulumu bulundu; hicbir sey degistirilmedi. Once mevcut ajani kaldirin, ardindan bu kurulumu yeniden calistirin. Ya da sorulmasi icin bir terminalde calistirin, veya sormadan kaldirip yeniden kurmak icin -ReplaceExisting ekleyin.' }
# Same protections as Uninstall-BackupAgentService.ps1 -RemoveIdentity: only the agent credential store files may be present.
function Assert-PurgeStore([string] $Path) {
    $full = (Assert-SafePath $Path).TrimEnd('\')
    $forbidden = @([IO.Path]::GetPathRoot($full).TrimEnd('\'), $env:WINDIR, $env:ProgramFiles, ${env:ProgramFiles(x86)}, [Environment]::GetFolderPath('CommonApplicationData'), $env:USERPROFILE)
    if ($forbidden -contains $full) { Fail 'Refusing to remove a system, root or profile directory.' 'Sistem, kok veya profil dizini kaldirilmaz.' }
    if (Test-Path -LiteralPath $full) {
        if (-not (Test-Path -LiteralPath $full -PathType Container)) { Fail 'The identity store must be a directory.' 'Kimlik deposu bir dizin olmalidir.' }
        foreach ($item in Get-ChildItem -LiteralPath $full -Force) {
            if ($item.Attributes -band [IO.FileAttributes]::ReparsePoint) { Fail 'Refusing removal through a reparse point.' 'Yeniden ayristirma noktasi uzerinden silme reddedildi.' }
            if ($item.PSIsContainer -or ($item.Name -ne 'agent-identity.bin' -and $item.Name -ne '.enrollment.lock' -and $item.Name -notmatch '^\.agent-identity\.bin\.[a-fA-F0-9]{32}\.tmp$')) {
                Fail 'Refusing removal: a directory contains data not owned by the agent credential store; nothing was removed.' 'Silme reddedildi: bir dizin ajan kimlik deposuna ait olmayan veri iceriyor; hicbir sey silinmedi.'
            }
        }
    }
    return $full
}
# The update staging directory the service installer creates next to the store ("<store>.updates"): only the agent's own staged releases, so refuse reparse points anywhere inside it.
function Assert-PurgeUpdateStage([string] $Path) {
    $full = (Assert-SafePath $Path).TrimEnd('\')
    if (Test-Path -LiteralPath $full) {
        if (-not (Test-Path -LiteralPath $full -PathType Container)) { Fail 'The update staging path must be a directory; nothing was removed.' 'Guncelleme hazirlik yolu bir dizin olmalidir; hicbir sey silinmedi.' }
        if (@(Get-ChildItem -LiteralPath $full -Recurse -Force | Where-Object { $_.Attributes -band [IO.FileAttributes]::ReparsePoint }).Count) {
            Fail 'Refusing removal through a reparse point.' 'Yeniden ayristirma noktasi uzerinden silme reddedildi.'
        }
    }
    return $full
}
function Get-ServiceSetting([string] $Name) {
    $environment = (Get-ItemProperty -Path 'HKLM:\SYSTEM\CurrentControlSet\Services\BackupAgent' -Name Environment -ErrorAction SilentlyContinue).Environment
    $setting = @($environment | Where-Object { $_ -like "$Name=*" }) | Select-Object -First 1
    if ($setting) { return $setting.Substring($Name.Length + 1) }
}
function Remove-ExistingAgent([string] $InstallPath, [string] $StorePath) {
    # Validate every target before touching the service so a failed check removes nothing.
    $stores = @($StorePath)
    $registered = Get-ServiceSetting 'Agent__CredentialStoreDirectory'
    if ($registered -and -not $registered.Equals($StorePath, [StringComparison]::OrdinalIgnoreCase)) { $stores += $registered }
    $stores = @($stores | ForEach-Object { Assert-PurgeStore $_ })
    $stages = @()
    foreach ($store in $stores) { $stages += Assert-PurgeUpdateStage "$store.updates" }
    if (Test-Path -LiteralPath $InstallPath) {
        if (-not (Test-Path -LiteralPath $InstallPath -PathType Container)) { Fail 'The installation path is not a directory; nothing was removed.' 'Kurulum yolu bir dizin degil; hicbir sey silinmedi.' }
        if (@(Get-ChildItem -LiteralPath $InstallPath -Recurse -Force | Where-Object { $_.Attributes -band [IO.FileAttributes]::ReparsePoint }).Count) {
            Fail 'The installation contains reparse points; nothing was removed.' 'Kurulum yeniden ayristirma noktasi iceriyor; hicbir sey silinmedi.'
        }
    }
    if (Get-ScheduledTask -TaskName 'BackupAgentUpdate' -ErrorAction SilentlyContinue) {
        Stop-ScheduledTask -TaskName 'BackupAgentUpdate' -ErrorAction SilentlyContinue
        Unregister-ScheduledTask -TaskName 'BackupAgentUpdate' -Confirm:$false
    }
    $service = Get-Service -Name BackupAgent -ErrorAction SilentlyContinue
    if ($service) {
        $marker = (Get-ItemProperty -Path 'HKLM:\SYSTEM\CurrentControlSet\Services\BackupAgent' -Name BackupOperatorsAddedByInstaller -ErrorAction SilentlyContinue).BackupOperatorsAddedByInstaller
        if ($marker -eq 1) {
            $backupOperators = (New-Object Security.Principal.SecurityIdentifier 'S-1-5-32-551').Translate([Security.Principal.NTAccount]).Value.Split('\')[1]
            & net.exe localgroup $backupOperators 'NT SERVICE\BackupAgent' /delete | Out-Null
        }
        if ($service.Status -ne 'Stopped') { Stop-Service -Name BackupAgent -Force; $service.WaitForStatus('Stopped', [TimeSpan]::FromSeconds(60)) }
        $service.Dispose()
        & sc.exe delete BackupAgent | Out-Null
        if ($LASTEXITCODE -ne 0) { Fail 'Windows Service removal failed.' 'Windows hizmeti kaldirilamadi.' }
    }
    if ([Diagnostics.EventLog]::SourceExists('BackupAgent')) { Remove-EventLog -Source 'BackupAgent' }
    foreach ($store in $stores) { if (Test-Path -LiteralPath $store) { Remove-Item -LiteralPath $store -Recurse -Force } }
    foreach ($stage in $stages) { if (Test-Path -LiteralPath $stage) { Remove-Item -LiteralPath $stage -Recurse -Force } }
    if (Test-Path -LiteralPath $InstallPath) { Remove-Item -LiteralPath $InstallPath -Recurse -Force }
}
if ($Interactive -and $NonInteractive) { Fail 'Choose only one interaction mode.' 'Yalnizca bir etkilesim kipi secin.' }
# SSH/ConPTY has a real console even when Windows reports no interactive desktop session.
$canPrompt = -not [Console]::IsInputRedirected
$guided = $Interactive -or (-not $NonInteractive -and $canPrompt -and ([string]::IsNullOrWhiteSpace($Server) -or [string]::IsNullOrWhiteSpace($Token)))
if ($guided -and -not $canPrompt) { Fail 'Interactive mode requires a terminal.' 'Etkilesimli kip icin terminal gerekir.' }
if ($guided) {
    Write-Host (Text 'Backup Platform agent installation' 'Backup Platform ajan kurulumu')
    Write-Host (Text '1. Use a trusted HTTPS server address and a one-time token from Add agent.' '1. Guvenilir HTTPS sunucu adresini ve Ajan ekle ekranindaki tek kullanimlik kayit anahtarini kullanin.')
    if ([string]::IsNullOrWhiteSpace($Server)) { $Server = Read-Host (Text 'Server address' 'Sunucu adresi') }
    if ([string]::IsNullOrWhiteSpace($Token)) {
        $secure = Read-Host (Text 'Enrollment token (hidden)' 'Kayit anahtari (gizli)') -AsSecureString
        $pointer = [Runtime.InteropServices.Marshal]::SecureStringToBSTR($secure)
        try { $Token = [Runtime.InteropServices.Marshal]::PtrToStringBSTR($pointer) }
        finally { [Runtime.InteropServices.Marshal]::ZeroFreeBSTR($pointer); $secure.Dispose() }
    }
    Write-Host (Text '2. Choose the display name; Enter accepts the machine name.' '2. Gorunen adi secin; Enter tusu makine adini kabul eder.')
    if ([string]::IsNullOrWhiteSpace($AgentName)) { $AgentName = Read-Host ((Text 'Agent name' 'Ajan adi') + " [$env:COMPUTERNAME]") }
}
if ([string]::IsNullOrWhiteSpace($AgentName)) { $AgentName = $env:COMPUTERNAME }
$uri = $null
if (-not [Uri]::TryCreate($Server, [UriKind]::Absolute, [ref] $uri) -or $uri.Scheme -ne 'https' -or -not $uri.Host -or $uri.UserInfo -or $uri.Query -or $uri.Fragment -or $Server -match '[\s\\\x00-\x1F\x7F]') {
    Fail 'Use an absolute HTTPS server address without credentials, query or fragment.' 'Kimlik bilgisi, sorgu veya parca icermeyen mutlak HTTPS sunucu adresi kullanin.'
}
if ($Token -cnotmatch '^[0-9a-fA-F]{32}\.[A-Za-z0-9_-]{43}$' -or $Token.StartsWith('00000000000000000000000000000000.')) {
    Fail 'A complete valid enrollment token is required; automatic mode never prompts.' 'Eksiksiz ve gecerli kayit anahtari zorunludur; otomatik kip soru sormaz.'
}
if ([string]::IsNullOrWhiteSpace($AgentName) -or $AgentName.Length -gt 255 -or $AgentName -match '\p{Cc}') {
    Fail 'Agent name must contain 1-255 characters without control characters.' 'Ajan adi denetim karakteri icermeyen 1-255 karakter olmalidir.'
}
if ($env:OS -ne 'Windows_NT' -or -not [Environment]::Is64BitOperatingSystem -or $env:PROCESSOR_ARCHITECTURE -eq 'ARM64' -or $env:PROCESSOR_ARCHITEW6432 -eq 'ARM64') {
    Fail 'Only Windows x64 is supported.' 'Yalnizca Windows x64 desteklenir.'
}
$current = [Security.Principal.WindowsIdentity]::GetCurrent()
$principal = New-Object Security.Principal.WindowsPrincipal $current
if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    Fail 'Run PowerShell as Administrator.' 'PowerShell uygulamasini yonetici olarak calistirin.'
}
$Server = $uri.AbsoluteUri.TrimEnd('/')
$installPath = Assert-SafePath (Join-Path ([Environment]::GetFolderPath('ProgramFiles')) 'BackupAgent')
$storePath = Assert-SafePath (Join-Path ([Environment]::GetFolderPath('CommonApplicationData')) 'BackupAgent')
$existing = [bool]((Get-Service -Name BackupAgent -ErrorAction SilentlyContinue) -or (Test-Path -LiteralPath $installPath) -or (Test-Path -LiteralPath $storePath))
# No terminal and no explicit consent: refuse before downloading anything.
if ($existing -and -not $ReplaceExisting -and -not $canPrompt) { Refuse-Existing }
if ($guided) {
    Write-Host (Text '3. Installation summary (token hidden)' '3. Kurulum ozeti (kayit anahtari gizli)')
    Write-Host "  $Server`n  $AgentName`n  $installPath`n  BackupAgent / NT SERVICE\BackupAgent"
    Write-Host (Text 'Enrollment runs as the service account; the permanent identity is protected for that account.' 'Kayit hizmet hesabiyla yapilir; kalici kimlik bu hesap icin korunur.')
    $answer = Read-Host (Text 'Install? [y/N]' 'Kurulsun mu? [e/H]')
    $accepted = if ($Language -eq 'tr') { '^[eE]$' } else { '^[yY]$' }
    if ($answer -notmatch $accepted) { Write-Host (Text 'Installation cancelled.' 'Kurulum iptal edildi.'); return }
}
$work = Join-Path ([Environment]::GetFolderPath('CommonApplicationData')) ('BackupAgent-install-' + [Guid]::NewGuid().ToString('N'))
$null = Assert-SafePath $work
New-ProtectedDirectory $work
try {
    Write-Host (Text 'Downloading and validating the agent package...' 'Ajan paketi indiriliyor ve dogrulaniyor...')
    $archivePath = Join-Path $work 'agent.zip'
    [Net.ServicePointManager]::SecurityProtocol = [Net.ServicePointManager]::SecurityProtocol -bor [Net.SecurityProtocolType]::Tls12
    try { Invoke-WebRequest -Uri "$Server/api/agent-dist/packages/win-x64" -OutFile $archivePath -UseBasicParsing -MaximumRedirection 0 }
    catch { Fail 'Package download failed; verify HTTPS trust and package availability.' 'Paket indirilemedi; HTTPS guvenini ve paket kullanilabilirligini denetleyin.' }
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    $archive = [IO.Compression.ZipFile]::OpenRead($archivePath)
    $entries = @()
    $roots = New-Object 'Collections.Generic.HashSet[string]' ([StringComparer]::Ordinal)
    $paths = New-Object 'Collections.Generic.HashSet[string]' ([StringComparer]::OrdinalIgnoreCase)
    try {
        foreach ($entry in $archive.Entries) {
            $name = $entry.FullName.TrimEnd('/')
            $parts = $name.Split('/')
            $kind = ($entry.ExternalAttributes -shr 16) -band 0xF000
            if (-not $name -or $entry.FullName.Contains('\') -or $entry.FullName.StartsWith('/') -or $kind -notin @(0, 0x4000, 0x8000) -or -not $paths.Add($name)) { throw 'Unsafe archive entry.' }
            foreach ($part in $parts) {
                if ($part -in @('', '.', '..') -or $part -match '[<>:"|?*\x00-\x1F]' -or $part -match '[. ]$' -or $part -match '^(?i:CON|PRN|AUX|NUL|COM[1-9]|LPT[1-9])(?:\.|$)') { throw 'Unsafe archive path.' }
            }
            $null = $roots.Add($parts[0])
            $entries += $entry
        }
        if ($roots.Count -ne 1) { throw 'Expected one archive root.' }
        $root = @($roots)[0]
        if ($root -cnotmatch '^backup-agent-win-x64-\d+\.\d+\.\d+(?:-[0-9A-Za-z.-]+)?$' -or -not $paths.Contains("$root/bin/BackupPlatform.Agent.Host.exe") -or -not $paths.Contains("$root/Install-BackupAgentService.ps1")) { throw 'Invalid release layout.' }
        # Strip exactly the validated release root; never search or flatten nested directories.
        $package = Join-Path $work 'package'
        New-ProtectedDirectory $package
        foreach ($entry in $entries) {
            if ($entry.FullName.TrimEnd('/') -eq $root) { continue }
            $relative = $entry.FullName.Substring($root.Length + 1)
            $target = Join-Path $package $relative
            if ($entry.FullName.EndsWith('/')) { $null = [IO.Directory]::CreateDirectory($target); continue }
            $null = [IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($target))
            $source = $entry.Open()
            try {
                $destination = New-Object IO.FileStream($target, [IO.FileMode]::CreateNew, [IO.FileAccess]::Write, [IO.FileShare]::None)
                try { $source.CopyTo($destination) } finally { $destination.Dispose() }
            } finally { $source.Dispose() }
        }
    } catch { Fail 'The package is invalid or unsafe; nothing was installed.' 'Paket gecersiz veya guvensiz; kurulum yapilmadi.' }
    finally { $archive.Dispose() }
    # The new package is valid; only now may the old installation be removed.
    if ($existing) {
        if (-not $ReplaceExisting) {
            Write-Host (Text 'An existing agent installation was found:' 'Mevcut bir ajan kurulumu bulundu:')
            Write-Host ("  " + (Text 'Service: ' 'Hizmet: ') + $(if (Get-Service -Name BackupAgent -ErrorAction SilentlyContinue) { 'BackupAgent' } else { '-' }))
            Write-Host ("  " + (Text 'Install path: ' 'Kurulum yolu: ') + $installPath)
            Write-Host ("  " + (Text 'Identity store: ' 'Kimlik deposu: ') + $storePath)
            $enrolled = Get-ServiceSetting 'Agent__Backend__BaseUri'
            if ($enrolled) { Write-Host ("  " + (Text 'Server: ' 'Sunucu: ') + $enrolled) }
            Write-Host (Text 'Removing deletes the service, binaries and agent identity (re-enrollment with the new token). Backups already stored in backup storage locations are not deleted.' 'Kaldirma hizmeti, ikili dosyalari ve ajan kimligini siler (yeni anahtarla yeniden kayit yapilir). Yedekleme depolama konumlarindaki mevcut yedekler silinmez.')
            $answer = Read-Host (Text 'Remove it and reinstall? (y/n)' 'Mevcut ajan kurulumu bulundu. Kaldirilip yeniden kurulsun mu? (e/h)')
            $accepted = if ($Language -eq 'tr') { '^[eE]$' } else { '^[yY]$' }
            if ($answer -notmatch $accepted) { Refuse-Existing }
        }
        Write-Host (Text 'Removing the existing agent installation...' 'Mevcut ajan kurulumu kaldiriliyor...')
        Remove-ExistingAgent $installPath $storePath
    }
    # Directory.Move refuses an existing destination; keep the protected ACL on the binary tree.
    [IO.Directory]::Move($package, $installPath)
    try {
        # Execute the trusted package helper in memory without changing the machine's execution policy.
        $serviceInstaller = [scriptblock]::Create([IO.File]::ReadAllText((Join-Path $installPath 'Install-BackupAgentService.ps1')))
        & $serviceInstaller -BinaryDirectory (Join-Path $installPath 'bin') -BackendUri $Server -EnrollmentToken $Token -AgentName $AgentName -StoreDirectory $storePath *> $null
        $service = Get-Service -Name BackupAgent
        try { $service.WaitForStatus('Running', [TimeSpan]::FromSeconds(30)); $service.Refresh(); if ($service.Status -ne 'Running') { throw 'Service is not running.' } }
        finally { $service.Dispose() }
        $configuration = Get-CimInstance Win32_Service -Filter "Name='BackupAgent'"
        if ($configuration.StartName -ne 'NT SERVICE\BackupAgent' -or $configuration.StartMode -ne 'Auto') { throw 'Unexpected service configuration.' }
    } catch {
        Fail 'Service enrollment or activation failed. Any identity and installation were preserved; the token may have been consumed. Check Windows service events and the server before retrying.' 'Hizmet kaydi veya etkinlestirme basarisiz. Kimlik ve kurulum korundu; anahtar kullanilmis olabilir. Yeniden denemeden once Windows hizmet olaylarini ve sunucuyu denetleyin.'
    }
    Write-Host (Text 'Agent installed; the automatic service is running. Confirm Online in Agents after heartbeat.' 'Ajan kuruldu; otomatik hizmet calisiyor. Yasam sinyalinden sonra Ajanlar sayfasinda cevrimici durumunu dogrulayin.')
} finally {
    $Token = $null
    if (Test-Path -LiteralPath $work) { $null = Assert-SafePath $work; Remove-Item -LiteralPath $work -Recurse -Force }
}
