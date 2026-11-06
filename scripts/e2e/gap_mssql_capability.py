"""#53 Linux SQL Server capability + live pre-check. Modes: admit | unreachable | mismatch."""
import json, os, subprocess, sys, time
import lib, flows
import gap_common as g

DROPIN = os.path.expanduser('~/.config/systemd/user/bpe2e-host-agent.service.d')


def ctx():
    c = g.Ctx(); return c, c.t


ONCE = {'maxAttempts': 1, 'initialDelaySeconds': 5, 'maxDelaySeconds': 60, 'backoff': 'Fixed', 'jitter': 0.1}


def start_job(t, agent, name, cfg):
    job = t.call('POST', '/backup-jobs', {'name': f'{name} {int(time.time())}', 'agentId': agent['agentId'], 'overlapPolicy': 'Skip', 'retryPolicy': ONCE, 'configuration': cfg})
    r = t.raw('POST', f"/backup-jobs/{job['id']}/runs", {})
    return job, r


def timed_failure(t, agent, name, cfg, limit=120):
    began = time.monotonic(); job, r = start_job(t, agent, name, cfg)
    assert r.status_code in (200, 201, 202), (r.status_code, r.text[:300])
    rid = r.json().get('id') or r.json().get('run', {}).get('id')
    d = t.wait(f'/backup-runs/{rid}', timeout=limit)
    arts = [a for at in d['attempts'] for a in at['artifacts']]
    return {'runId': rid, 'status': d['status'], 'reasonCode': d['reasonCode'], 'seconds': round(time.monotonic() - began, 1), 'artifacts': len(arts)}


@lib.scenario('gap/JOB-15/linux-mssql-admission-and-success')
def admit():
    c, t = ctx(); fed = t.agents()['fedora-e2e']
    cfg = flows.db_job_config('mssql', c.host_mssql_cfg(), [flows.local_dest()])
    job, r = start_job(t, fed, 'mssql-on-fedora', {**cfg, 'integratedSecurity': True, 'databaseCredentialReference': None})
    body = r.json()
    assert r.status_code == 409 and body.get('code') == 'agent_engine_unsupported', (r.status_code, body)
    runs = t.call('GET', f"/backup-jobs/{job['id']}/runs")
    assert not (runs.get('items') if isinstance(runs, dict) else runs), runs
    host = c.ha
    j, rid, art, d = flows.run_backup(t, host, 'mssql-on-host-mapped', cfg, timeout=600)
    return {'fedoraResponse': {'status': r.status_code, 'code': body.get('code')}, 'fedoraRuns': 0, 'hostRunId': rid, 'hostArtifact': art['id'], 'hostStatus': d['status']}


@lib.scenario('gap/JOB-15/mssql-server-unreachable-fails-fast')
def unreachable():
    c, t = ctx()
    cfg = flows.db_job_config('mssql', {**c.host_mssql_cfg(), 'port': 1}, [flows.local_dest()])
    res = timed_failure(t, c.ha, 'mssql-unreachable', cfg)
    assert res['status'] == 'Failed' and res['artifacts'] == 0 and res['seconds'] < 60, res
    return res


@lib.scenario('gap/JOB-15/mssql-staging-not-shared-fails-fast')
def mismatch():
    c, t = ctx(); res = None
    os.makedirs(DROPIN, exist_ok=True)
    conf = os.path.join(DROPIN, 'zz-mismatch.conf')
    try:
        # the server-side path points at a real directory inside the container that is NOT the bind-mounted staging directory
        open(conf, 'w').write('[Service]\nEnvironment="Agent__Backup__Staging__ServerDirectory=/var/opt/mssql/data"\n')
        subprocess.run(['systemctl', '--user', 'daemon-reload'], check=True); subprocess.run(['systemctl', '--user', 'restart', 'bpe2e-host-agent'], check=True); time.sleep(20)
        cfg = flows.db_job_config('mssql', c.host_mssql_cfg(), [flows.local_dest()])
        res = timed_failure(t, c.ha, 'mssql-unshared', cfg)
        assert res['status'] == 'Failed' and res['artifacts'] == 0 and res['seconds'] < 90, res
        return res
    finally:
        if os.path.exists(conf): os.remove(conf)
        subprocess.run(['systemctl', '--user', 'daemon-reload']); subprocess.run(['systemctl', '--user', 'restart', 'bpe2e-host-agent']); time.sleep(15)


if __name__ == '__main__':
    {'admit': admit, 'unreachable': unreachable, 'mismatch': mismatch}[sys.argv[1]]()
