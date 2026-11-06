"""#51 native VSS requester on win11-e2e. Modes: denied | roundtrip | cancel | crash.
denied: service NOT in Backup Operators -> failure code PermissionDenied (never StagingInvalid).
roundtrip: service in Backup Operators; locked file read consistently; restore path+size+SHA-256; no shadow copy left.
cancel / crash: the shadow copy list returns to the baseline (cancelled run, agent process killed)."""
import hashlib, json, sys, time
import lib, flows
from gap_windows import winx
from gap_windows_vss import shadows

SRC = r'C:\e2e\snap-src'; REL = 'vss-native'; ROOT = SRC + '\\' + REL
A_SHA = hashlib.sha256(b'A' * 1048576).hexdigest().upper()


def prep(extra=''):
    cleanup()
    rc, out = winx(rf'''$ErrorActionPreference='Stop'; Remove-Item -LiteralPath '{ROOT}' -Recurse -Force -ErrorAction SilentlyContinue; New-Item -ItemType Directory '{ROOT}' | Out-Null
$b=New-Object byte[] 3145728; (New-Object Random 11).NextBytes($b); [IO.File]::WriteAllBytes('{ROOT}\plain.bin',$b); [IO.File]::WriteAllText('{ROOT}\sample.txt','vss native deterministic data'); {extra}''')
    assert rc == 0, out


def cleanup():
    winx(rf"Stop-ScheduledTask -TaskName vsshold -ErrorAction SilentlyContinue; Unregister-ScheduledTask -TaskName vsshold -Confirm:`$false -ErrorAction SilentlyContinue; Remove-Item -LiteralPath '{ROOT}','{SRC}\vss-native-restore' -Recurse -Force -ErrorAction SilentlyContinue")


def hold_locked():
    """A SYSTEM task writes 1 MiB of 'A', keeps the file open exclusively for 60 s, then appends 'B'."""
    script = r'''$f=[IO.File]::Open("C:\e2e\snap-src\vss-native\locked.bin",'Create','ReadWrite','None')
$a=[byte[]](,[byte]65*1048576); $f.Write($a,0,$a.Length); $f.Flush($true)
Start-Sleep 75
$a=[byte[]](,[byte]66*1048576); $f.Write($a,0,$a.Length); $f.Flush($true); $f.Close()'''
    rc, out = winx(rf'''Set-Content C:\ProgramData\vsshold.ps1 @'
{script}
'@
Unregister-ScheduledTask -TaskName vsshold -Confirm:$false -ErrorAction SilentlyContinue
Register-ScheduledTask -TaskName vsshold -Action (New-ScheduledTaskAction -Execute powershell.exe -Argument '-NoProfile -ExecutionPolicy Bypass -File C:\ProgramData\vsshold.ps1') -Principal (New-ScheduledTaskPrincipal -UserId SYSTEM -LogonType ServiceAccount) | Out-Null
Start-ScheduledTask -TaskName vsshold; for($i=0;$i -lt 40;$i++){{ Start-Sleep 1; if((Test-Path '{ROOT}\locked.bin') -and (Get-Item '{ROOT}\locked.bin').Length -ge 1048576){{break}} }}; (Get-Item '{ROOT}\locked.bin').Length''')
    assert rc == 0 and '1048576' in out, out


def manifest():
    rc, out = winx(rf'''$p='{SRC}\vss-native-restore\{REL}'; Get-ChildItem -LiteralPath $p -Recurse -File | Sort-Object Name | % {{ "$($_.Name)|$($_.Length)|$((Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash)" }}''')
    assert rc == 0, out
    return out.strip().splitlines()


def start_run(t, agent, name):
    cfg = flows.file_job_config('e2esnap', [flows.local_dest()], paths=[REL])
    job = t.call('POST', '/backup-jobs', {'name': f'{name} {int(time.time())}', 'agentId': agent['agentId'], 'overlapPolicy': 'Skip', 'configuration': cfg})
    run = t.call('POST', f"/backup-jobs/{job['id']}/runs", {})
    return run.get('id', run.get('run', {}).get('id'))


def failure_codes(detail):
    return sorted({a.get('reasonCode') for a in detail.get('attempts', []) if a.get('reasonCode')} | ({detail.get('reasonCode')} - {None}))


@lib.scenario('gap/JOB-06/native-vss-denied')
def denied():
    t = lib.Tenant('a'); agent = t.agents()['win11-e2e']; base = shadows(); prep()
    try:
        rid = start_run(t, agent, 'vss-denied'); d = t.wait(f'/backup-runs/{rid}', timeout=300)
        codes = failure_codes(d)
        assert d['status'] == 'Failed' and 'PermissionDenied' in codes and 'StagingInvalid' not in codes, json.dumps(d)[:1200]
        assert shadows() == base
        return {'runId': rid, 'status': d['status'], 'failureCodes': codes, 'shadows': base}
    finally: cleanup()


@lib.scenario('gap/JOB-06/native-vss-roundtrip-locked')
def roundtrip():
    t = lib.Tenant('a'); agent = t.agents()['win11-e2e']; base = shadows(); prep(); hold_locked()
    try:
        expected = sorted([
            f"locked.bin|1048576|{A_SHA}", "plain.bin|3145728|" + winx(rf"(Get-FileHash '{ROOT}\plain.bin' -Algorithm SHA256).Hash")[1].strip(),
            "sample.txt|29|" + winx(rf"(Get-FileHash '{ROOT}\sample.txt' -Algorithm SHA256).Hash")[1].strip()])
        job, rid, art, detail = flows.run_backup(t, agent, 'vss-native', flows.file_job_config('e2esnap', [flows.local_dest()], paths=[REL]), timeout=600); aid = art['id']
        target = {'host': 'localhost', 'port': None, 'integratedSecurity': True, 'databaseCredentialReference': None, 'encrypt': False, 'trustServerCertificate': False}
        req = {'agentId': agent['agentId'], 'target': target, 'databaseName': 'e2esnap', 'mode': 'NewDatabase', 'options': {'files': {'relativeDirectory': 'vss-native-restore'}}}
        r = t.call('POST', f'/backup-artifacts/{aid}/restore', req); rs = r.get('id', r.get('restoreId'))
        d = t.wait(f'/restores/{rs}', timeout=600); assert d['status'] == 'Succeeded', json.dumps(d)[:1000]
        got = manifest(); assert got == expected, f'mismatch\n{expected}\n{got}'
        after = shadows(); assert after == base, (base, after)
        live = winx(rf"Start-Sleep 80; (Get-Item '{ROOT}\locked.bin').Length")[1].strip()
        return {'runId': rid, 'artifactId': aid, 'restoreId': rs, 'manifest': got, 'liveLockedSizeAfterHolder': live, 'shadowsBefore': base, 'shadowsAfter': after}
    finally: cleanup()


def big(mb):
    rc, out = winx(rf'''$b=New-Object byte[] 1048576; $r=New-Object Random 5; $f=[IO.File]::Create('{ROOT}\big.bin'); 1..{mb} | % {{ $r.NextBytes($b); $f.Write($b,0,$b.Length) }}; $f.Close()''', timeout=900)
    assert rc == 0, out


def wait_running(t, rid, base, timeout=240):
    """Waits until the run is executing and a shadow copy beyond the baseline exists."""
    end = time.time() + timeout; seen = None
    while time.time() < end:
        d = t.call('GET', f'/backup-runs/{rid}')
        extra = [s for s in shadows() if s not in base]
        if extra: return extra
        if d['status'] in lib.TERMINAL: raise AssertionError(f'run ended before a snapshot was observed: {json.dumps(d)[:600]}')
        time.sleep(1)
    raise AssertionError('no snapshot observed')


@lib.scenario('gap/JOB-06/native-vss-cancel')
def cancel():
    t = lib.Tenant('a'); agent = t.agents()['win11-e2e']; base = shadows(); prep(); big(900)
    try:
        rid = start_run(t, agent, 'vss-cancel'); extra = wait_running(t, rid, base)
        t.raw('POST', f'/backup-runs/{rid}/cancel', {}); d = t.wait(f'/backup-runs/{rid}', timeout=300)
        end = time.time() + 60
        while shadows() != base and time.time() < end: time.sleep(2)
        assert shadows() == base, (base, shadows())
        return {'runId': rid, 'status': d['status'], 'snapshotDuringRun': extra, 'shadowsAfter': shadows()}
    finally: cleanup()


@lib.scenario('gap/JOB-06/native-vss-crash')
def crash():
    t = lib.Tenant('a'); agent = t.agents()['win11-e2e']; base = shadows(); prep(); big(900)
    try:
        rid = start_run(t, agent, 'vss-crash'); extra = wait_running(t, rid, base)
        rc, out = winx("$p=(Get-CimInstance Win32_Service -Filter \"Name='BackupAgent'\").ProcessId; taskkill /F /PID $p; Start-Sleep 2; (Get-Service BackupAgent).Status")
        end = time.time() + 150
        while shadows() != base and time.time() < end: time.sleep(2)
        after = shadows(); assert after == base, ('snapshot survived the crash', base, after, extra)
        rc, out2 = winx("Start-Sleep 20; (Get-Service BackupAgent).Status")
        try: t.raw('POST', f'/backup-runs/{rid}/cancel', {})
        except Exception: pass
        return {'runId': rid, 'snapshotDuringRun': extra, 'kill': out.strip()[-120:], 'shadowsAfterCrash': after, 'serviceAfter': out2.strip()}
    finally: cleanup()


@lib.scenario('gap/JOB-06/native-vss-service-restart')
def restart():
    t = lib.Tenant('a'); agent = t.agents()['win11-e2e']; base = shadows(); prep(); big(900)
    try:
        rid = start_run(t, agent, 'vss-restart'); extra = wait_running(t, rid, base)
        rc, out = winx('Stop-Service BackupAgent -Force; (Get-Service BackupAgent).Status; Start-Service BackupAgent; Start-Sleep 3; (Get-Service BackupAgent).Status')
        end = time.time() + 120
        while shadows() != base and time.time() < end: time.sleep(2)
        after = shadows(); assert after == base, ('snapshot survived the restart', base, after, extra)
        try: t.raw('POST', f'/backup-runs/{rid}/cancel', {})
        except Exception: pass
        return {'runId': rid, 'snapshotDuringRun': extra, 'service': out.split(), 'shadowsAfterRestart': after}
    finally: cleanup()


if __name__ == '__main__':
    {'denied': denied, 'roundtrip': roundtrip, 'cancel': cancel, 'crash': crash, 'restart': restart}[sys.argv[1]]()
