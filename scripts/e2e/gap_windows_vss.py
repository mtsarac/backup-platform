"""Live Windows VSS backup/restore and shadow baseline check."""
import json, time
import lib, flows
from gap_windows import winx


def shadows():
    rc, out = winx("(Get-CimInstance Win32_ShadowCopy | Select-Object -ExpandProperty ID | Sort-Object) -join ','")
    assert rc == 0, out
    return sorted(x for x in out.strip().split(',') if x)


def manifest(path):
    rc, out = winx(rf'''$ErrorActionPreference='Stop'; $p='{path}'; Get-ChildItem -LiteralPath $p -Recurse -File | Sort-Object FullName | ForEach-Object {{ "$($_.FullName.Substring($p.Length+1))|$($_.Length)|$((Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash)" }}''')
    assert rc == 0, out
    return sorted(out.strip().splitlines())


@lib.scenario('gap/JOB-06/windows-vss-roundtrip')
def run():
    t = lib.Tenant('a'); agent = t.agents()['win11-e2e']; base = shadows()
    source = r'C:\e2e\snap-src'; rel = 'gap-windows-vss'; root = source + '\\' + rel
    rc, out = winx(rf'''$ErrorActionPreference='Stop'; if (Test-Path '{root}') {{ throw 'already exists' }}; New-Item -ItemType Directory '{root}' | Out-Null; [IO.File]::WriteAllText('{root}\sample.txt','gap Windows VSS deterministic data'); $b=New-Object byte[] 1048576; (New-Object Random 72).NextBytes($b); [IO.File]::WriteAllBytes('{root}\binary.bin',$b)''')
    assert rc == 0, out
    rid = aid = None
    try:
        expected = manifest(root); assert len(expected) == 2, expected
        cfg = flows.file_job_config('e2esnap', [flows.local_dest()], paths=[rel])
        job, rid, art, detail = flows.run_backup(t, agent, 'gap-windows-vss', cfg, timeout=600); aid = art['id']
        replica = t.call('GET', f'/backup-artifacts/{aid}')
        req = {'agentId': agent['agentId'], 'target': {'host':'localhost','port':None,'integratedSecurity':True,'databaseCredentialReference':None,'encrypt':False,'trustServerCertificate':False}, 'databaseName':'e2esnap','mode':'NewDatabase','options':{'files':{'relativeDirectory':'gap-windows-vss-restore'}}}
        r = t.call('POST', f'/backup-artifacts/{aid}/restore', req); restore_id = r.get('id', r.get('restoreId'))
        d = t.wait(f'/restores/{restore_id}', timeout=600)
        assert d['status'] == 'Succeeded', json.dumps(d)[:1200]
        got = manifest(source + '\\gap-windows-vss-restore\\' + rel)
        assert expected == got, f'manifest mismatch: {expected} vs {got}'
        after = shadows(); assert after == base, f'shadow baseline changed: before {base}, after {after}'
        return {'runId':rid,'artifactId':aid,'restoreId':restore_id,'manifest':expected,'replicas':replica.get('replicas'),'shadowsBefore':base,'shadowsAfter':after,'restorePath':source + r'\gap-windows-vss-restore'}
    finally:
        winx(rf"Remove-Item -LiteralPath '{root}','{source}\gap-windows-vss-restore' -Recurse -Force -ErrorAction SilentlyContinue")
        print('gap-windows-vss-final-shadows', json.dumps(shadows()), 'run', rid, 'artifact', aid, flush=True)


if __name__ == '__main__': run()
