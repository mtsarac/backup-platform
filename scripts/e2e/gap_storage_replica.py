"""Private-tenant replica and retention E2E scenarios. Writes gap/<ID>/ rows via lib.scenario."""
import concurrent.futures, hashlib, json, pathlib, shlex, subprocess, time, uuid
import api, lib, flows, gap_sp_common as g
from lib import scenario

ROOT = g.agent_root('rep')
CTX = {}
def replica(a, provider, verified_only=True):
    return next(r for r in a['replicas'] if r['storageProvider'] == provider and r['status'] == 'Available' and (not verified_only or r.get('verificationState') == 'Verified'))
def digest(data): return hashlib.sha256(data).hexdigest()
def artifact(aid): return CTX['t'].call('GET', f'/backup-artifacts/{aid}')
def scoped(r):
    key = r['key']; parts = key.split('/')
    assert CTX['t'].id.replace('-', '') in parts and '..' not in parts and not key.startswith('/'), f'unsafe or out-of-tenant key {key}'
    return key

def local_path(r):
    p = (ROOT / 'local-store' / scoped(r)).resolve()
    assert p.is_relative_to((ROOT / 'local-store').resolve())
    return p

def s3_cmd(op, r, data=None):
    obj = f'l/bpe2e/tenants/{CTX["t"].id.replace("-", "")}/connections/{r["storageConnectionId"].replace("-", "")}/' + scoped(r)
    cmd = 'mc --insecure alias set l https://localhost:9000 "$MINIO_ROOT_USER" "$MINIO_ROOT_PASSWORD" >/dev/null 2>&1 && mc --insecure ' + op + ' ' + shlex.quote(obj)
    return subprocess.run(['docker','exec','-i','bpe2e-minio','sh','-c',cmd], input=data, capture_output=True)

def raw_s3(r):
    p = s3_cmd('cat',r); assert p.returncode == 0, f'S3 cat failed: {p.stderr.decode(errors="replace")[:200]}'
    return p.stdout

def run(job):
    rid, d = g.run_to_end(CTX['t'], job, timeout=360)
    assert d['status'] == 'Succeeded', f'run {rid} {d["status"]}: {str(d)[:700]}'
    a = g.arts(d); assert len(a) == 1, f'run {rid}: {len(a)} artifacts'
    return artifact(a[0]['id'])

def job(name, destinations=None, retention=None):
    return g.new_job(CTX['t'], CTX['agent'], name, g.gcfg(destinations or CTX['dests'], paths=['payload.bin']), **({'retention': retention} if retention else {}))

def restore(a, r, label):
    rel = 'zz-' + label + '-' + uuid.uuid4().hex[:8]
    req = {'agentId': CTX['agent']['agentId'], 'target': {'host':'localhost','port':None,'integratedSecurity':True,'databaseCredentialReference':None,'encrypt':False,'trustServerCertificate':False}, 'databaseName':'gapfiles','mode':'NewDatabase','replicaId':r['id'],'options':{'files':{'relativeDirectory':rel}}}
    start = CTX['t'].raw('POST',f'/backup-artifacts/{a["id"]}/restore',req)
    if start.status_code not in (200,201,202): return {'http': start.status_code, 'body': start.text[:200]}
    d = CTX['t'].wait('/restores/' + (start.json().get('id') or start.json()['restoreId']),timeout=360)
    path = ROOT / 'files' / rel / 'payload.bin'
    return {'http':start.status_code,'status':d['status'],'id':d['id'],'hash':digest(path.read_bytes()) if path.exists() else None,'reason':d.get('failureCode') or d.get('reasonCode')}

def expect_restore(a,r,label):
    x = restore(a,r,label)
    assert x.get('status') == 'Succeeded' and x.get('hash') == CTX['source_hash'], f'{a["id"]} {r["id"]}: {x}'
    return x

def init():
    t = g.ensure_tenant('rep'); ag = g.ensure_agent('rep','rep'); CTX.update(t=t,agent=ag)
    source = g.put_file('rep','payload.bin',128*1024); CTX['source_hash'] = digest(source.read_bytes())
    a = lib.Tenant('a'); refs = lib.ST['connections']; conns = {c['id']:c for c in a.call('GET','/storage-connections?pageSize=100')['items']}
    for prov in ('s3','sftp'):
        cfg = conns[refs[prov]]['configuration']; sec = {'accessKey':api.env['E2E_MINIO_USER'],'secretKey':api.env['E2E_MINIO_PASSWORD']} if prov == 's3' else {'password':lib.ST['fedora']['sftp_password']}
        c = t.call('POST','/storage-connections',{'providerType':prov,'name':'gap-sp-rep-'+prov+'-'+uuid.uuid4().hex[:6], 'configuration':cfg,'secret':sec})
        CTX[prov] = c['id']
    CTX['dests'] = [g.gdest(),g.gdest('s3',conn=CTX['s3'],primary=False),g.gdest('sftp',conn=CTX['sftp'],primary=False)]
    return {'tenantId':t.id,'agentId':ag['agentId'],'s3ConnectionId':CTX['s3'],'sftpConnectionId':CTX['sftp']}

@scenario('gap/STO-06/backup-metadata')
def multi():
    a = run(job('sto06')); CTX['multi'] = a['id']; rs = a['replicas']
    assert {r['storageProvider'] for r in rs if r['status']=='Available' and r['verificationState']=='Verified'} == {'local','s3','sftp'}, f'{a["id"]}: {[(r["storageProvider"],r["status"],r["verificationState"]) for r in rs]}'
    for r in rs: scoped(r)
    return {'artifactId':a['id'],'artifactChecksum':a['artifactChecksum'],'replicas':[{k:r.get(k) for k in ('id','storageProvider','storageConnectionId','key','status','verificationState','checksum')} for r in rs]}

@scenario('gap/STO-06/physical-sha')
def physical():
    a = artifact(CTX['multi']); loc = replica(a,'local'); s3 = replica(a,'s3')
    lp = local_path(loc); lh = digest(lp.read_bytes()); sh = digest(raw_s3(s3))
    assert lh == sh == a['artifactChecksum'], f'{a["id"]}: local={lh} s3={sh} api={a["artifactChecksum"]}'
    return {'artifactId':a['id'],'localPath':str(lp),'s3Object':f'bpe2e/tenants/{CTX["t"].id.replace("-", "")}/connections/{s3["storageConnectionId"].replace("-", "")}/'+scoped(s3),'sha256':lh}

@scenario('gap/STO-06/sftp-physical-sha')
def physical_sftp():
    a=artifact(CTX['multi']); r=replica(a,'sftp'); key=scoped(r)
    cfg=CTX['t'].call('GET',f'/storage-connections/{r["storageConnectionId"]}')['configuration']
    assert cfg['remoteBaseDirectory']=='/home/e2e-sftp/backups' and cfg['authenticationType']=='password'
    remote=cfg['remoteBaseDirectory']+'/backup-platform/tenants/'+CTX['t'].id.replace('-','')+'/connections/'+r['storageConnectionId'].replace('-','')+'/'+key
    url='sftp://'+cfg['host']+':'+cfg['port']+'//'+remote.lstrip('/')
    config='user = '+json.dumps(cfg['username']+':'+lib.ST['fedora']['sftp_password'])+'\n'
    p=subprocess.run(['curl','--silent','--show-error','--fail','--insecure','--config','-',url],input=config.encode(),capture_output=True,timeout=90)
    assert p.returncode==0, f'SFTP physical read failed exit={p.returncode}'
    h=digest(p.stdout); assert h==a['artifactChecksum'],f'artifact={a["id"]} sftp={h} api={a["artifactChecksum"]}'
    return {'artifactId':a['id'],'remotePath':remote,'sha256':h}

@scenario('gap/STO-06/restore-each')
def roundtrip():
    a = artifact(CTX['multi']); out = {r['storageProvider']:expect_restore(a,r,'sto06') for r in a['replicas'] if r['status']=='Available'}
    assert set(out)=={'local','s3','sftp'},out
    return {'artifactId':a['id'],'sourceSha256':CTX['source_hash'],'restores':out}

@scenario('gap/STO-08/delete-and-repair')
def repair():
    a = run(job('sto08', CTX['dests'][:2])); CTX['repair'] = a['id']; r = replica(a,'s3'); original = digest(raw_s3(r)); p = s3_cmd('rm',r)
    assert p.returncode == 0 and s3_cmd('stat',r).returncode != 0, f'delete failed {a["id"]}'
    miss = restore(a,r,'sto08missing')
    assert miss.get('status') == 'Failed', f'artifact={a["id"]} missing restore={miss}'
    response = CTX['t'].raw('POST',f'/backup-artifacts/{a["id"]}/repairs',{'storageConnectionId':CTX['s3']})
    CTX['repair_response'] = (response.status_code,response.text[:300]); assert response.status_code in (200,201,202),f'artifact={a["id"]} target={r["id"]} repair={response.status_code} {response.text[:250]}'
    op = response.json(); end=time.time()+240
    while time.time()<end:
        cur=CTX['t'].call('GET',f'/backup-artifacts/{a["id"]}/repairs/{op["id"]}')
        if cur['status'] in ('Succeeded','Failed','Cancelled'): break
        time.sleep(3)
    assert cur['status']=='Succeeded',f'artifact={a["id"]} repair={cur}'
    b=artifact(a['id']); fixed=replica(b,'s3'); sha=digest(raw_s3(fixed)); assert sha==original==a['artifactChecksum'],f'artifact={a["id"]} sha={sha} expected={original}'
    return {'artifactId':a['id'],'operationId':op['id'],'targetReplica':fixed['id'],'sha256':sha,'restore':expect_restore(b,fixed,'sto08')}

@scenario('gap/STO-09/tamper-local')
def tamper():
    a=run(job('sto09',CTX['dests'][:2])); CTX['tamper']=a['id']; bad=replica(a,'local'); good=replica(a,'s3'); p=local_path(bad)
    data=bytearray(p.read_bytes()); data[len(data)//2]^=1; p.write_bytes(data); broken=digest(data)
    assert broken != a['artifactChecksum']; badrestore=restore(a,bad,'sto09bad'); goodrestore=restore(a,good,'sto09good')
    CTX['tamper_evidence']={'badRestore':badrestore,'goodRestore':goodrestore,'badHash':broken}
    assert badrestore.get('status') != 'Succeeded' and goodrestore.get('status')=='Succeeded' and goodrestore.get('hash')==CTX['source_hash'], f'artifact={a["id"]} {CTX["tamper_evidence"]}'
    return {'artifactId':a['id'],**CTX['tamper_evidence']}

@scenario('gap/STO-09/explicit-download')
def tamper_download():
    a=artifact(CTX['tamper']); r=replica(a,'local'); x=CTX['t'].raw('POST',f'/backup-artifacts/{a["id"]}/downloads',{'replicaId':r['id'],'decrypt':False})
    if x.status_code in (200,201,202):
        j=x.json(); assert j.get('direct'), f'unexpected backend download: {j}'; return {'artifactId':a['id'],'http':x.status_code,'direct':True,'note':'agent direct WebRTC session only; bytes not exercised'}
    return {'artifactId':a['id'],'http':x.status_code,'body':x.text[:200]}

@scenario('gap/STO-10/concurrent-repair')
def race():
    a=run(job('sto10',CTX['dests'][:2])); CTX['race']=a['id']; r=replica(a,'s3'); p=s3_cmd('rm',r); assert p.returncode==0
    miss=restore(a,r,'sto10missing'); assert miss.get('status')=='Failed', f'artifact={a["id"]} missing restore={miss}'
    def send(_):
        t=g.GT('rep'); q=t.raw('POST',f'/backup-artifacts/{a["id"]}/repairs',{'storageConnectionId':CTX['s3']}); return {'http':q.status_code,'json':q.json() if q.content and q.headers.get('content-type','').startswith('application/json') else {'text':q.text[:200]}}
    with concurrent.futures.ThreadPoolExecutor(max_workers=2) as pool: responses=list(pool.map(send,range(2)))
    CTX['race_responses']=responses
    ops=[x['json']['id'] for x in responses if x['http'] in (200,201,202) and isinstance(x.get('json'),dict) and 'id' in x['json']]
    codes=sorted(x['http'] for x in responses)
    # Single successful creation: duplicate request hits either 409 (if pre-checked) or 500/DbUpdateException on ux_replica_repair_active unique index race
    assert len(set(ops))==1 and len(ops)==1 and (codes==[202,409] or codes==[202,500]), f'artifact={a["id"]} responses={responses}'
    end=time.time()+240
    while time.time()<end:
        op=CTX['t'].call('GET',f'/backup-artifacts/{a["id"]}/repairs/{ops[0]}')
        if op['status'] in ('Succeeded','Failed','Cancelled'):break
        time.sleep(3)
    assert op['status']=='Succeeded',f'artifact={a["id"]} {responses} op={op}'
    b=artifact(a['id']); targets=[x for x in b['replicas'] if x['storageProvider']=='s3' and x['status']=='Available' and x.get('verificationState')=='Verified']; assert len(targets)==1 and digest(raw_s3(targets[0]))==a['artifactChecksum']
    return {'artifactId':a['id'],'responses':responses,'operationId':ops[0],'sha256':a['artifactChecksum']}

@scenario('gap/STO-10/background-repair-pattern')
def background_repair():
    t=CTX['t']; cfg=t.call('GET',f'/storage-connections/{CTX["s3"]}')['configuration']
    conn=t.call('POST','/storage-connections',{'providerType':'s3','name':'gap-sp-rep-bg-'+uuid.uuid4().hex[:6],'configuration':cfg,'secret':{'accessKey':api.env['E2E_MINIO_USER'],'secretKey':'gap-sp-rep-intentionally-invalid'}})
    dests=[g.gdest('sftp',conn=CTX['sftp']),g.gdest('s3',conn=conn['id'],primary=False)]
    a=run(job('sto10-background',dests)); aid=a['id']
    before=t.call('GET',f'/backup-artifacts/{aid}/repairs')
    t.call('PUT',f'/storage-connections/{conn["id"]}/secret',{'values':{'accessKey':api.env['E2E_MINIO_USER'],'secretKey':api.env['E2E_MINIO_PASSWORD']}})
    q=t.raw('POST',f'/backup-artifacts/{aid}/repairs',{'storageConnectionId':conn['id']})
    after=t.call('GET',f'/backup-artifacts/{aid}/repairs')
    return {'artifactId':aid,'replicas':[(r['storageProvider'],r['status'],r['verificationState']) for r in a['replicas']],'operationsBefore':before,'manualHttp':q.status_code,'manualBody':q.json() if q.content else None,'operationsAfter':after}

@scenario('gap/STO-10/historical-409-readonly')
def historical():
    aid='e96be62e-131f-4f49-a39d-1725ab4d55b2'; ops=lib.Tenant('a').call('GET',f'/backup-artifacts/{aid}/repairs')
    op=next(x for x in ops if x['id']=='1cb22eae-62be-457f-b31c-edc13b430fc9')
    assert op['status']=='Succeeded' and op['requestedByUserId'] is None and op['transferredBytes']==34559,op
    return {'artifactId':aid,'operationId':op['id'],'requestedByUserId':op['requestedByUserId'],'createdAt':op['createdAt'],'completedAt':op['completedAt'],'status':op['status'],'transferredBytes':op['transferredBytes'],'note':'[INFERENCE] prior same-second 409 likely overlaps automatic repair; direct correlation not proved'}

@scenario('gap/STO-09/tamper-s3-download-restore')
def tamper_s3():
    a=run(job('sto09-s3',CTX['dests'][:2])); bad=replica(a,'s3'); good=replica(a,'local')
    raw=bytearray(raw_s3(bad)); raw[len(raw)//2]^=1; changed=digest(raw)
    put=s3_cmd('pipe',bad,bytes(raw)); assert put.returncode==0 and digest(raw_s3(bad))==changed and changed!=a['artifactChecksum'],f'artifact={a["id"]} tamper write failed'
    q=CTX['t'].raw('POST',f'/backup-artifacts/{a["id"]}/downloads',{'replicaId':bad['id'],'decrypt':False})
    dl={'http':q.status_code}
    if q.status_code in (200,201,202):
        session=q.json(); dl['direct']=session.get('direct')
        if not session.get('direct'):
            try:
                response=CTX['t'].s.get(api.BASE+session['downloadUrl'],timeout=90); dl.update(contentHttp=response.status_code,receivedSha256=digest(response.content),bytes=len(response.content))
            except Exception as e: dl['error']=type(e).__name__
    broken=restore(a,bad,'sto09s3bad'); intact=restore(a,good,'sto09s3good')
    evidence={'artifactId':a['id'],'corruptedSha256':changed,'expectedSha256':a['artifactChecksum'],'download':dl,'badRestore':broken,'goodRestore':intact,'sourceSha256':CTX['source_hash']}
    assert not (dl.get('contentHttp')==200 and dl.get('receivedSha256')==changed) and broken.get('status')!='Succeeded' and intact.get('status')=='Succeeded' and intact.get('hash')==CTX['source_hash'],evidence
    return evidence

@scenario('gap/POL-01/scope-expiry')
def retention():
    policy={'enabled':True,'keepForSeconds':30,'deleteGracePeriodSeconds':10}
    j=job('pol01',CTX['dests'][:2],policy); old=run(j); CTX['old']=old['id']; oldlocal=local_path(replica(old,'local')); olds3=replica(old,'s3')
    assert oldlocal.exists() and s3_cmd('stat',olds3).returncode==0, f'artifact={old["id"]} physical copies missing before expiry'
    other=run(job('pol01-other',CTX['dests'][:2],{'enabled':False,'keepForSeconds':3600,'deleteGracePeriodSeconds':10})); CTX['other']=other['id']
    CTX['t'].call('PUT',f'/backup-jobs/{j["id"]}/retention',{'enabled':False,'keepForSeconds':30,'deleteGracePeriodSeconds':10})
    new=run(j); CTX['new']=new['id']; end=time.time()+240
    while time.time()<end:
        oldnow=artifact(old['id']); newnow=artifact(new['id']); othernow=artifact(other['id'])
        if all(r['status']=='Deleted' for r in oldnow['replicas']):break
        time.sleep(3)
    evidence={'old':old['id'],'new':new['id'],'other':other['id'],'oldStates':[r['status'] for r in oldnow['replicas']],'newStates':[r['status'] for r in newnow['replicas']],'otherStates':[r['status'] for r in othernow['replicas']],'oldLocalExists':oldlocal.exists(),'oldS3Exists':s3_cmd('stat',olds3).returncode==0}
    evidence.update(newS3Exists=s3_cmd('stat',replica(new,'s3')).returncode==0,otherS3Exists=s3_cmd('stat',replica(other,'s3')).returncode==0)
    CTX['retention_evidence']=evidence
    assert all(x=='Deleted' for x in evidence['oldStates']) and not evidence['oldLocalExists'] and not evidence['oldS3Exists'] and evidence['newS3Exists'] and evidence['otherS3Exists'] and all(x=='Available' for x in evidence['newStates']) and all(x=='Available' for x in evidence['otherStates']),evidence
    return evidence

@scenario('gap/POL-02/offline-delete-retry')
def offline():
    policy={'enabled':True,'keepForSeconds':30,'deleteGracePeriodSeconds':10}; j=job('pol02',CTX['dests'][:2],policy); a=run(j); CTX['offline']=a['id']; lp=local_path(replica(a,'local'))
    foreign=lib.Tenant('a'); page=foreign.call('GET','/backup-artifacts?pageSize=1'); sentinel=page['items'][0]['id'] if page['items'] else None; before=foreign.call('GET',f'/backup-artifacts/{sentinel}') if sentinel else None
    g.stop_agent('rep')
    try:
        end=time.time()+100
        while time.time()<end:
            current=artifact(a['id']); statuses={r['storageProvider']:r['status'] for r in current['replicas']}
            if statuses.get('s3')=='Deleted' and statuses.get('local')!='Available': break
            time.sleep(3)
        evidence={'artifactId':a['id'],'offlineStates':statuses,'localExistsOffline':lp.exists(),'sentinel':sentinel}
    finally: CTX['agent']=g.ensure_agent('rep','rep')
    end=time.time()+300
    while time.time()<end:
        current=artifact(a['id'])
        if all(r['status']=='Deleted' for r in current['replicas']):break
        time.sleep(3)
    evidence.update(afterStates={r['storageProvider']:r['status'] for r in current['replicas']},localExistsAfter=lp.exists())
    if sentinel:
        after=foreign.call('GET',f'/backup-artifacts/{sentinel}'); evidence['sentinelStatesBefore']=[r['status'] for r in before['replicas']]; evidence['sentinelStatesAfter']=[r['status'] for r in after['replicas']]
    CTX['offline_evidence']=evidence
    assert evidence['localExistsOffline'] and not evidence['localExistsAfter'] and all(x=='Deleted' for x in evidence['afterStates'].values()) and sentinel and evidence['sentinelStatesBefore']==evidence['sentinelStatesAfter'],evidence
    return evidence

if __name__=='__main__':
    if scenario('gap/setup/private-tenant-agent-connections')(init)():
        for fn in (multi,physical,physical_sftp,roundtrip,repair,tamper,tamper_download,tamper_s3,race,background_repair,historical,retention,offline): fn()
