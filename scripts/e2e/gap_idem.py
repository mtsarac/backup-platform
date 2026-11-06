"""#54 job creation idempotency across both API nodes (:5252 and :5255)."""
import concurrent.futures as cf, json, time, uuid
import api, lib, gap_common as g, gap_job as gj, flows

NODE2 = "https://192.168.122.1:5255"


def tenants():
    t1 = lib.Tenant('a')
    old = api.BASE; api.BASE = NODE2
    try: t2 = lib.Tenant('a')
    finally: api.BASE = old
    return t1, t2


def body(agent, name="gap-idem", password="Gap-Idem-S3cret-pw"):
    cfg = flows.db_job_config('mssql', {'host': '127.0.0.1', 'port': 1433, 'database': 'e2e_src', 'ref': None}, [flows.local_dest()])
    cfg.pop('databaseCredentialReference', None); cfg['integratedSecurity'] = False
    cfg['databaseLogin'] = {'userName': 'sa', 'password': password}
    return {'name': name, 'agentId': agent['agentId'], 'overlapPolicy': 'Skip', 'configuration': cfg}


def post(t, b, key): return t.s.post(t.base + '/backup-jobs', json=b, headers={'Idempotency-Key': key})


def sql(q): return gj.appdb(q)


@lib.scenario('gap/JOB-01/idempotent-create-two-nodes')
def idem():
    t1, t2 = tenants(); agent = t1.agents()['host-e2e']; aid = agent['agentId'].replace('-', '')
    key = 'gap-idem-' + uuid.uuid4().hex[:10]; b = body(agent, name=key)
    secrets0 = int(sql(f"select count(*) from platform_secrets where \"Name\" like 'dbc-{aid}-%'")[0][0])
    with cf.ThreadPoolExecutor(24) as ex:
        rs = list(ex.map(lambda i: post((t1, t2)[i % 2], b, key), range(24)))
    codes = sorted(r.status_code for r in rs); ids = {r.json()['id'] for r in rs}
    assert codes.count(201) == 1 and codes.count(200) == 23 and len(ids) == 1, (codes, ids)
    jid = ids.pop()
    rows = sql(f"select count(*) from backup_jobs where \"IdempotencyKey\"='{key}'")[0][0]
    audits = sql(f"select count(*) from audit_events where \"Action\"='backup_job.created' and \"TargetId\"='{jid}'")[0][0]
    secrets1 = int(sql(f"select count(*) from platform_secrets where \"Name\" like 'dbc-{aid}-%'")[0][0])
    assert (rows, audits, secrets1 - secrets0) == ('1', '1', 1), (rows, audits, secrets1 - secrets0)
    fp = sql(f"select \"RequestFingerprint\" from backup_jobs where \"Id\"='{jid}'")[0][0]
    dump = sql(f"select row_to_json(j)::text from backup_jobs j where \"Id\"='{jid}'")[0][0]
    assert len(fp) == 64 and 'Gap-Idem-S3cret-pw' not in dump
    # different payload, either node -> 409; nothing new
    c1 = post(t1, body(agent, name=key + '-other'), key); c2 = post(t2, body(agent, name=key, password='another-pw'), key)
    assert c1.status_code == 409 and c2.status_code == 409 and c1.json()['error'] == 'idempotency_conflict', (c1.status_code, c2.status_code)
    assert int(sql(f"select count(*) from platform_secrets where \"Name\" like 'dbc-{aid}-%'")[0][0]) - secrets0 == 1
    # disable keeps the key reserved
    assert t1.s.post(t1.base + f'/backup-jobs/{jid}/disable', json={}).status_code == 200
    again = post(t2, b, key); assert again.status_code == 200 and again.json()['id'] == jid
    # tenant B's identical key is a different job
    other = None
    try:
        tb = lib.Tenant('b'); ab = next(iter(tb.agents().values()))
        rb = post(tb, body(ab, name=key), key); other = rb.status_code
        assert rb.status_code == 201 and rb.json()['id'] != jid, rb.text[:200]
        tb.s.post(tb.base + f"/backup-jobs/{rb.json()['id']}/disable", json={})
    except StopIteration: other = 'no tenant B agent'
    return {'jobId': jid, 'codes': {'201': 1, '200': 23}, 'jobRows': rows, 'auditEvents': audits, 'newCredentials': secrets1 - secrets0, 'conflicts': [c1.status_code, c2.status_code], 'tenantB': other}


if __name__ == '__main__': idem()
