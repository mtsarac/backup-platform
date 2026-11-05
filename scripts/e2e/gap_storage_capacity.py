"""Live isolated SFTP capacity threshold probes for STO-11, STO-15 and POL-10."""
import math, sys, time
import lib, gap_sp_common as g, gap_sp_fedora as gf
from gap_storage_policy import sftp_conn, drop


def probe(t, cid):
    p = t.call('POST', f'/storage-connections/{cid}/probe', {})
    for _ in range(60):
        if p['status'] != 'Pending': return p
        time.sleep(2)
        p = t.call('GET', f'/storage-connections/{cid}/capacity')
    raise TimeoutError('capacity probe remained Pending')


@lib.scenario('gap/STO-15/capacity-boundaries-and-history')
def thresholds():
    t = g.GT('sto'); c = sftp_conn(t, 'capacity', base=gf.CAP_BASE)
    a = t.agents()['gap-sp-sto']
    job = g.new_job(t, a, 'capacity-health', g.gcfg([g.gdest('sftp', conn=c['id'], primary=True)], paths=['docs']))
    results = []
    try:
        bs, total, available = gf.stat_fs()
        # ext4 may consume metadata blocks for the filler itself; stay 16 blocks either side.
        for threshold in (.80, .90):
            for offset in (16, -16):
                free_blocks = math.floor(total * (1-threshold)) + offset
                measured = gf.fill_to_avail(free_blocks)
                p = probe(t, c['id'])
                below = offset > 0
                expected = ('Available' if threshold == .80 else 'Warning') if below else ('Warning' if threshold == .80 else 'Critical')
                results.append({'threshold': threshold, 'side': 'below' if below else 'at-or-above', 'fs': measured, 'status': p['status'], 'total': p.get('totalBytes'), 'free': p.get('freeBytes')})
                assert p['status'] == expected, f'expected {expected}: {results[-1]}'
                assert p.get('totalBytes') == measured[0]*measured[1] and p.get('freeBytes') == measured[0]*measured[2], f'probe != statvfs: {results[-1]}'
                health = t.call('GET', f'/backup-jobs/{job["id"]}/health')
                factor = health['factors']['storageCapacity']
                assert factor['status'] == expected and factor['score'] == {'Available': 10, 'Warning': 5, 'Critical': 0}[expected], health
                assert health['score'] == sum(x['score'] for x in health['factors'].values()), health
                assert health['state'] == ('Healthy' if health['score'] >= 80 else 'Degraded' if health['score'] >= 50 else 'Critical'), health
                results[-1]['health'] = {'score': health['score'], 'state': health['state'], 'capacity': factor}
        h = t.call('GET', f'/storage-connections/{c["id"]}/capacity')
        assert len(h.get('history', [])) >= 4, f'history entries: {len(h.get("history", []))}'
        return {'connectionId': c['id'], 'readings': results, 'historyCount': len(h['history'])}
    finally:
        t.raw('POST', f'/backup-jobs/{job["id"]}/disable', {})
        gf.fed(f'sudo rm -f {gf.CAP}/filler; sync'); drop(t, c)


@lib.scenario('gap/STO-11/isolated-sftp-local-s3-smb-probe')
def providers():
    t = g.GT('sto'); c = sftp_conn(t, 'capacity-base', base=gf.CAP_BASE)
    try:
        p = probe(t, c['id']); bs, total, avail = gf.stat_fs()
        assert p['status'] == 'Available' and (p['totalBytes'],p['freeBytes']) == (bs*total,bs*avail), p
        local = next(x for x in t.call('GET', '/storage-connections')['items'] if x['providerType'] == 'local' and x['isDefault'])
        lp = probe(t, local['id']); assert lp['status'] in ('Available','Warning','Critical') and lp['totalBytes'] > 0, lp
        return {'sftp': {'total': p['totalBytes'], 'free': p['freeBytes'], 'status': p['status']}, 'local': {'total': lp['totalBytes'], 'free': lp['freeBytes'], 'status': lp['status']}}
    finally: drop(t, c)


@lib.scenario('gap/POL-10/capacity-audit-alerts')
def audit_alerts():
    import api, json
    t = g.GT('sto')
    rows = [json.loads(line) for line in lib.RESULTS.read_text().splitlines()]
    last = next(r for r in reversed(rows) if r['scenario'] == 'gap/STO-15/capacity-boundaries-and-history' and r['result'] == 'PASS')
    cid = last['connectionId']
    response = api.login().get(f'{api.BASE}/api/platform/audit', params={'tenantId': t.id, 'pageSize': 100})
    response.raise_for_status()
    actions = [r['action'] for r in response.json()['items'] if r.get('targetId') == cid]
    assert 'storage_capacity_warning' in actions and 'storage_capacity_critical' in actions, actions
    return {'connectionId': cid, 'actions': actions}


if __name__ == '__main__':
    for fn in (providers, thresholds, audit_alerts):
        if len(sys.argv) == 1 or any(s in fn.scenario for s in sys.argv[1:]): fn()
