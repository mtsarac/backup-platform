[CmdletBinding()]
param(
    [ValidateSet('status', 'start', 'stop', 'restart', 'logs', 'check')]
    [string] $Action = 'status',
    [switch] $RequireAgent,
    [switch] $CheckBackend,
    [string] $BackendUri,
    [ValidateRange(1, 10000)] [int] $MaximumEvents = 100
)

$ErrorActionPreference = 'Stop'
$serviceName = 'BackupAgent'
$service = Get-Service -Name $serviceName -ErrorAction SilentlyContinue
if (-not $service) {
    Write-Output 'Agent service: not installed.'
    Write-Output 'Enrollment readiness: unavailable until the service is installed and enrolled under its service account.'
    if ($RequireAgent -or $CheckBackend -or $Action -in @('start', 'stop', 'restart', 'logs')) {
        throw 'The BackupAgent service is required for this operation.'
    }
    return
}

try {
    switch ($Action) {
        'start' {
            Start-Service -Name $serviceName
            $service.WaitForStatus('Running', [TimeSpan]::FromSeconds(60))
        }
        'stop' {
            Stop-Service -Name $serviceName
            $service.WaitForStatus('Stopped', [TimeSpan]::FromSeconds(60))
        }
        'restart' {
            Restart-Service -Name $serviceName
            $service.WaitForStatus('Running', [TimeSpan]::FromSeconds(60))
        }
        'logs' {
            # No entries is distinct from an inaccessible/missing event source.
            if (-not [Diagnostics.EventLog]::SourceExists($serviceName)) {
                throw 'The BackupAgent Application event source is missing.'
            }
            $eventLog = New-Object Diagnostics.EventLog('Application')
            try {
                $count = 0
                for ($index = $eventLog.Entries.Count - 1; $index -ge 0 -and $count -lt $MaximumEvents; $index--) {
                    $entry = $eventLog.Entries[$index]
                    if ($entry.Source -eq $serviceName) {
                        $entry | Select-Object TimeGenerated, EntryType, InstanceId, Message | Format-List
                        $count++
                    }
                }
                if ($count -eq 0) { Write-Output 'No BackupAgent Application events found.' }
            } finally { $eventLog.Dispose() }
            return
        }
    }
    $service.Refresh()
    Write-Output ('Agent service status: ' + $service.Status)
    if ($Action -ne 'check') { return }

    $configuration = Get-ItemProperty -LiteralPath "HKLM:\SYSTEM\CurrentControlSet\Services\$serviceName"
    $serviceDetails = Get-CimInstance Win32_Service -Filter "Name='$serviceName'"
    Write-Output ('Agent service account: ' + $serviceDetails.StartName)
    if ($serviceDetails.StartName -ne "NT SERVICE\$serviceName") {
        throw 'The service does not use the expected virtual account; DPAPI identity scope may not match.'
    }
    $environmentValues = @{}
    foreach ($entry in $configuration.Environment) {
        $pair = $entry -split '=', 2
        if ($pair.Length -eq 2) { $environmentValues[$pair[0]] = $pair[1] }
    }
    $storeDirectory = $environmentValues['Agent__CredentialStoreDirectory']
    if (-not $storeDirectory) {
        $storeDirectory = Join-Path ([Environment]::GetFolderPath('CommonApplicationData')) 'BackupAgent'
    }
    $identityPath = Join-Path $storeDirectory 'agent-identity.bin'
    if (Test-Path -LiteralPath $identityPath -PathType Leaf) {
        $identity = Get-Item -LiteralPath $identityPath -Force
        if ($identity.Length -eq 0) {
            Write-Output 'Identity: empty file; enrollment is not ready.'
        } else {
            Write-Output 'Identity: encrypted file present; decryptability and backend credential validity are NOT verified.'
        }
    } else {
        Write-Output 'Identity: absent; service-account enrollment is required.'
    }
    Write-Output 'Enrollment readiness: cannot be confirmed by an administrator/SSH account; verification requires the running service account and backend authentication.'

    if ($CheckBackend) {
        if (-not $BackendUri) { $BackendUri = $environmentValues['Agent__Backend__BaseUri'] }
        if (-not $BackendUri) { throw 'Agent__Backend__BaseUri is absent from the service configuration; provide --backend-uri explicitly.' }
        $uri = $null
        if (-not [Uri]::TryCreate($BackendUri, [UriKind]::Absolute, [ref] $uri) -or $uri.Scheme -ne 'https' -or $uri.UserInfo) {
            throw 'Backend BaseUri must be an absolute HTTPS address without embedded credentials.'
        }
        $response = $null
        try {
            $request = [Net.HttpWebRequest]::Create($uri)
            $request.Method = 'HEAD'
            $request.Timeout = 15000
            $request.AllowAutoRedirect = $false
            $response = $request.GetResponse()
            Write-Output ('Backend HTTPS reachable; HTTP status: ' + [int] $response.StatusCode)
        } catch [Net.WebException] {
            if ($_.Exception.Response) {
                $response = $_.Exception.Response
                Write-Output ('Backend HTTPS reachable; HTTP status: ' + [int] $response.StatusCode)
            } else {
                # Avoid echoing BaseUri, credentials, or query strings in exception messages.
                throw ('Backend HTTPS reachability failed: ' + $_.Exception.Status)
            }
        } finally { if ($response) { $response.Close() } }
        Write-Output 'Reachability does not establish agent enrollment or authorization.'
    }
} finally { $service.Dispose() }
