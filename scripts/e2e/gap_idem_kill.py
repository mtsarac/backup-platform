"""#54: process killed after the credential was stored and before the job row. Retry completes with that credential; without a retry the sweep removes it."""
import subprocess, threading, time, uuid
import api, lib, gap_job as gj
from gap_idem import body, post


def sql(q): return gj.appdb(q)


def hold_agent_lock(agent_id, seconds):
    """A psql session that holds the agent row lock the job insert needs, so the request stalls right after it stored the credential."""
    cmd = f"BEGIN; SELECT \"Id\" FROM agents WHERE \"Id\"='{agent_id}' FOR UPDATE; SELECT pg_sleep({seconds}); ROLLBACK;"
    return subprocess.Popen(["docker", "exec", "bp-" + gj.PG, "sh", "-c", 'psql -U "$POSTGRES_USER" -d "$POSTGRES_DB" -c "$0"', cmd], stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL)


def kill_mid_creation(t, agent, key, hold=45):
    aid = agent['agentId']; prefix = aid.replace('-', '')
    before = int(sql(f"select count(*) from platform_secrets where \"Name\" like 'dbc-{prefix}-%'")[0][0])
    lock = hold_agent_lock(aid, hold); time.sleep(3)
    result = {}
    th = threading.Thread(target=lambda: result.update(r=_try(t, body(agent, name=key), key))); th.start()
    for _ in range(40):
        now = int(sql(f"select count(*) from platform_secrets where \"Name\" like 'dbc-{prefix}-%'")[0][0])
        if now == before + 1: break
        time.sleep(0.5)
    assert now == before + 1, 'credential never stored'
    claims = int(sql("select count(*) from job_credential_claims")[0][0]); jobs = sql(f"select count(*) from backup_jobs where \"IdempotencyKey\"='{key}'")[0][0]
    assert jobs == '0' and claims >= 1, (jobs, claims)
    subprocess.run(["docker", "kill", "bp-api"], check=True, capture_output=True)   # SIGKILL: no finally block, no catch, no cleanup runs
    th.join(timeout=60); lock.wait(timeout=hold + 30)
    subprocess.run(["docker", "start", "bp-api"], check=True, capture_output=True)
    assert api.wait_ready(120)
    return before, prefix


def _try(t, b, key):
    try: return post(t, b, key).status_code
    except Exception as e: return type(e).__name__


@lib.scenario('gap/JOB-01/idempotent-create-process-kill')
def kill():
    t = lib.Tenant('a'); agent = t.agents()['host-e2e']
    # 1. killed, then the same request retried (through the other node): completes with the one credential
    key = 'gap-kill-' + uuid.uuid4().hex[:8]
    before, prefix = kill_mid_creation(t, agent, key)
    assert sql(f"select count(*) from backup_jobs where \"IdempotencyKey\"='{key}'")[0][0] == '0'
    old = api.BASE; api.BASE = "https://192.168.122.1:5255"
    try: t2 = lib.Tenant('a')
    finally: api.BASE = old
    t1 = lib.Tenant('a')
    r = post(t2, body(agent, name=key), key)
    assert r.status_code == 201, r.text[:200]
    jid = r.json()['id']
    ref = sql(f"select \"Configuration\"->>'DatabaseCredentialReference' from backup_jobs where \"Id\"='{jid}'")[0][0]
    after = int(sql(f"select count(*) from platform_secrets where \"Name\" like 'dbc-{prefix}-%'")[0][0])
    assert after == before + 1 and sql(f"select count(*) from platform_secrets where \"Name\"='{ref}'")[0][0] == '1', (before, after, ref)
    # 2. killed and never retried: backdate the claim past the grace period, the sweep removes credential and claim
    key2 = 'gap-kill-' + uuid.uuid4().hex[:8]
    before2, _ = kill_mid_creation(t1, agent, key2)
    orphan = sql("select \"Reference\" from job_credential_claims order by \"ClaimedAt\" desc limit 1")[0][0]
    assert sql(f"select count(*) from platform_secrets where \"Name\"='{orphan}'")[0][0] == '1'
    gj.g.sh("docker", "exec", "bp-" + gj.PG, "sh", "-c", 'psql -U "$POSTGRES_USER" -d "$POSTGRES_DB" -c "$0"', f"update job_credential_claims set \"ClaimedAt\" = now() - interval '2 hours' where \"Reference\"='{orphan}'")
    end = time.time() + 150
    while time.time() < end and sql(f"select count(*) from platform_secrets where \"Name\"='{orphan}'")[0][0] != '0': time.sleep(5)
    gone = sql(f"select count(*) from platform_secrets where \"Name\"='{orphan}'")[0][0]; claim = sql(f"select count(*) from job_credential_claims where \"Reference\"='{orphan}'")[0][0]
    assert (gone, claim) == ('0', '0'), (gone, claim)
    assert sql(f"select count(*) from backup_jobs where \"IdempotencyKey\"='{key2}'")[0][0] == '0'
    # the used credential of part 1 is untouched
    assert sql(f"select count(*) from platform_secrets where \"Name\"='{ref}'")[0][0] == '1'
    for j in (jid,): t1.s.post(t1.base + f'/backup-jobs/{j}/disable', json={})
    return {'retryJob': jid, 'sameCredentialAfterRetry': True, 'orphanSwept': orphan, 'usedCredentialKept': True}


if __name__ == '__main__': kill()
