"""Live artifact gap checks; only gap-jr-art-* objects are changed."""
import base64, fcntl, hashlib, io, json, os, re, shlex, subprocess, tempfile, time, uuid
import requests
import api, lib, flows, gap_common as gc, files_providers as fp
from lib import ST, scenario

T = lib.Tenant('a')
B = lib.Tenant('b')
C = gc.Ctx()
ADMIN = api.login()
CAPTURE = []

def sha(b): return hashlib.sha256(b).hexdigest()
def role(name):
    x = ST['roles'][name]
    return api.login(x['login'], x['password'])
def req(s, method, url, **kw):
    r = s.request(method, url, allow_redirects=False, timeout=90, **kw)
    if 'application/octet-stream' not in r.headers.get('content-type', ''):
        CAPTURE.append((method + ' ' + url.split('/api')[-1], r.content + str(dict(r.headers)).encode()))
    else: CAPTURE.append((method + ' content headers', str(dict(r.headers)).encode()))
    return r

def all_artifacts():
    out=[]; page=1
    while True:
        d=T.call('GET',f'/backup-artifacts?pageSize=100&page={page}'); out += d['items']
        if len(out)>=d['totalCount'] or not d['items']: return out
        page+=1

def choose(provider, encrypted, agent=None, engine=None):
    for a in all_artifacts():
        if a['state'] != 'Available' or bool(a.get('encryption')) != encrypted: continue
        if agent and a.get('agentName') != agent: continue
        if engine and (a.get('backupMetadata') or {}).get('engine') != engine: continue
        if any(r['storageProvider']==provider and r['verificationState']=='Verified' and r['status']=='Available' for r in a['replicas']):
            return T.call('GET','/backup-artifacts/'+a['id'])
    raise RuntimeError(f'No available verified fixture: {provider} encrypted={encrypted} agent={agent} engine={engine}')

def replica(a, prov): return next(r for r in a['replicas'] if r['storageProvider']==prov and r['status']=='Available' and r['verificationState']=='Verified')
def stored(a, prov):
    r=replica(a,prov); key=r['key']
    prefix=f"tenants/{T.id.replace('-', '')}/connections/{r['storageConnectionId'].replace('-', '')}/"
    if prov=='local':
        assert a['agentName']=='host-e2e'
        return open(gc.local_store_path(r),'rb').read()
    if prov=='sftp':
        p='/home/e2e-sftp/backups/backup-platform/'+prefix+key
        cmd='sudo -n cat -- '+shlex.quote(p)
        q=subprocess.run(['ssh','virtual-fedora','bash','-lc',shlex.quote(cmd)],capture_output=True,timeout=120)
    else:
        cmd='mc --insecure alias set l https://localhost:9000 "$MINIO_ROOT_USER" "$MINIO_ROOT_PASSWORD" >/dev/null 2>&1 && mc --insecure cat '+shlex.quote('l/bpe2e/'+prefix+key)
        q=subprocess.run(['docker','exec','bpe2e-minio','sh','-c',cmd],capture_output=True,timeout=120)
    if q.returncode: raise RuntimeError(f'{prov} object read failed exit={q.returncode}')
    return q.stdout

def base(a): return T.base+'/backup-artifacts/'+a['id']
def prep(s,a,prov='s3',decrypt=False):
    r=req(s,'POST',base(a)+'/downloads',json={'replicaId':replica(a,prov)['id'],'decrypt':decrypt})
    assert r.status_code==200, f'prepare {r.status_code}'
    return r.json()
def content(s,p):
    r=req(s,'GET',api.BASE+p['downloadUrl'])
    return r

def wait_download(a,sid,timeout=180,session=None):
    end=time.time()+timeout
    while time.time()<end:
        response=(session or T.s).get(base(a)+'/downloads/'+sid,timeout=30)
        assert response.status_code==200, f'download status HTTP {response.status_code}'
        state=response.json()
        if state['state'] in ('Completed','Failed','Cancelled'): return state
        time.sleep(0.5)
    raise TimeoutError(f'download {sid} did not reach terminal state')

def restore_pg(a, prov):
    db='gap_jr_art_'+uuid.uuid4().hex[:8]
    try:
        _, r=gc.start_restore(T,a['id'],gc.restore_req(C.ha,C.host_pg_target(),db,replica_id=replica(a,prov)['id']))
        d=gc.wait_restore(T,r.get('id') or r.get('restoreId'))
        assert d['status']=='Succeeded', f"restore {d['status']} {d.get('failureCode')} {d.get('reasonCode')}"
        src=gc.host_pg_digest('e2e_src'); got=gc.host_pg_digest(db)
        assert src==got, 'restore digest mismatch'
        return {'restoreId':d['id'],'digest':src}
    finally: gc.host_pg_drop(db)

def create_pg(policy, destinations, name):
    cfg=flows.db_job_config('postgres', C.host_pg_cfg(), destinations, encrypt=False, encryptionPolicy=policy, requiredVerifiedReplicaCount=len(destinations))
    j,r,_=gc.run_job(T,C.ha,'art-'+name,cfg,wait=False)
    d=None
    try:
        d=T.wait(f'/backup-runs/{r}',timeout=300)
        assert d['status']=='Succeeded', f"run {r} {d['status']} {d.get('pauseReason')}"
        aa=gc.arts(d); assert len(aa)==1, f'run {r} artifacts={len(aa)}'
        return T.call('GET','/backup-artifacts/'+aa[0]['id'])
    finally:
        T.raw('POST',f"/backup-jobs/{j['id']}/disable",{})
        if d is None or d['status']=='Paused': T.raw('POST',f'/backup-runs/{r}/cancel',{})

def create_file(name):
    dest=fp.dest('s3')
    cfg=flows.file_job_config('e2efiles',[dest],paths=['docs'],encryptionPolicy='Required')
    j,r,_=gc.run_job(T,C.fa,'art-'+name,cfg,wait=False)
    d=None
    try:
        d=T.wait(f'/backup-runs/{r}',timeout=300)
        assert d['status']=='Succeeded', f"run {r} {d['status']}"
        return T.call('GET','/backup-artifacts/'+gc.arts(d)[0]['id'])
    finally:
        T.raw('POST',f"/backup-jobs/{j['id']}/disable",{})
        if d is None or d['status']=='Paused': T.raw('POST',f'/backup-runs/{r}/cancel',{})

@scenario('gap/ART-01/s3-owner-admin-bytes')
def art01():
    a=choose('s3',False,'host-e2e','postgres'); obj=stored(a,'s3'); results=[]
    assert sha(obj)==a['artifactChecksum'] and len(obj)==a['sizeBytes']
    for name,s in [('owner',T.s),('admin',role('admin-a'))]:
        p=prep(s,a); assert not p['direct']
        r=content(s,p); assert r.status_code==200 and r.content==obj and sha(r.content)==a['artifactChecksum'] and len(r.content)==a['sizeBytes']
        results.append({'role':name,'session':p['sessionId'],'bytes':len(obj),'sha256':sha(obj)})
    return {'artifactId':a['id'],'downloads':results}

@scenario('gap/ART-02/authorization-and-leakage')
def art02():
    a=choose('s3',False,'host-e2e','postgres'); p=prep(T.s,a); u=base(a)+'/downloads'; sid=p['sessionId']; cases=[]
    sessions=[('viewer-a',role('viewer-a'),T.id),('operator-a',role('operator-a'),T.id),('viewer-b',role('viewer-b'),T.id),('owner-b',B.s,T.id),('owner-b-own',B.s,B.id),('anonymous',requests.Session(),T.id)]
    sessions[-1][1].verify=False
    for name,s,tenant in sessions:
        path=f'{api.BASE}/api/tenants/{tenant}/backup-artifacts/{a["id"]}/downloads'
        for method,url,body in [('POST',path,{}),('GET',path+'/'+sid,None),('GET',path+'/'+sid+'/content',None)]:
            r=req(s,method,url,**({'json':body} if body is not None else {})); cases.append((name,method,r.status_code))
            assert r.status_code in (401,403,404), f'{name} {method} {r.status_code}'
    no_csrf=role('admin-a'); no_csrf.headers.clear()
    r=req(no_csrf,'POST',u,json={}); cases.append(('no-csrf','POST',r.status_code)); assert r.status_code in (400,401,403)
    raw=b'\n'.join(data for _,data in CAPTURE)
    secrets=[ST['fedora']['sftp_password'],ST['host']['pg']['password'],ST['host']['mssql_sa_password'],api.env.get('E2E_MINIO_PASSWORD',''),api.env.get('E2E_SMB_PASSWORD','')]
    secrets += [x['password'] for x in ST['roles'].values()] + [ST['a']['password'],ST['b']['password']]
    assert not any(x.encode() in raw for x in secrets if len(x)>=8), 'known secret appeared in captured response'
    dangerous=[b'/var/lib/',b'local-store/',b'/home/e2e-sftp/',b'X-Amz-Signature',b'BEGIN PRIVATE KEY',b'C:\\']
    assert not any(x in raw for x in dangerous), 'physical path or secret marker in download response'
    return {'artifactId':a['id'],'session':sid,'cases':cases,'leakMarkers':0}

@scenario('gap/ART-02/csrf-less-authenticated-content')
def csrf_content():
    a=choose('s3',False,'host-e2e','postgres'); s=role('admin-a'); p=prep(s,a)
    anonymous=requests.Session(); anonymous.verify=False
    assert content(anonymous,p).status_code in (401,403,404)
    assert content(T.s,p).status_code in (401,403,404)
    s.headers.clear()
    r=content(s,p)
    assert r.status_code==200 and len(r.content)==a['sizeBytes'] and sha(r.content)==a['artifactChecksum'], f"CSRF-less content HTTP={r.status_code} bytes={len(r.content)}"
    assert wait_download(a,p['sessionId'],session=s)['state']=='Completed'
    assert content(s,p).status_code==409
    return {'artifactId':a['id'],'sessionId':p['sessionId'],'status':r.status_code,'bytes':len(r.content)}

@scenario('gap/ART-03/session-binding-reuse-parallel')
def art03():
    a=choose('s3',False,'host-e2e','postgres'); y=choose('s3',True)
    p1=prep(T.s,a); p2=prep(T.s,a); assert p1['sessionId']!=p2['sessionId']
    bad=req(T.s,'GET',base(y)+'/downloads/'+p1['sessionId']+'/content'); assert bad.status_code==404
    foreign=req(B.s,'GET',f'{api.BASE}/api/tenants/{B.id}/backup-artifacts/{a["id"]}/downloads/{p1["sessionId"]}/content'); assert foreign.status_code in (403,404)
    other=req(role('admin-a'),'GET',base(a)+'/downloads/'+p1['sessionId']); assert other.status_code==404
    states=[]
    for p in [p1,p2]:
        pre=req(T.s,'GET',base(a)+'/downloads/'+p['sessionId']).json(); r=content(T.s,p)
        post=wait_download(a,p['sessionId'],30); again=content(T.s,p)
        assert pre['state']=='Ready' and pre['transferredBytes']==0, f'pre={pre}'
        assert r.status_code==200 and sha(r.content)==a['artifactChecksum'], f'content={r.status_code} length={len(r.content)}'
        assert post['state']=='Completed' and post['transferredBytes']==a['sizeBytes'] and again.status_code==409, f'post={post} reuse={again.status_code}'
        states.append({'session':p['sessionId'],'before':pre['state'],'after':post['state'],'bytes':post['transferredBytes'],'reuse':again.status_code})
    return {'artifactId':a['id'],'wrongArtifact':bad.status_code,'foreignTenant':foreign.status_code,'otherUser':other.status_code,'sessions':states}

@scenario('gap/ART-03/concurrent-content-sessions')
def concurrent_downloads():
    import concurrent.futures, threading
    a=choose('s3',False,'host-e2e','postgres'); ps=[prep(T.s,a),prep(T.s,a)]; gate=threading.Barrier(2)
    def receive(p):
        s=api.login(ST['a']['login'],ST['a']['password'])
        gate.wait(timeout=30)
        r=content(s,p)
        assert r.status_code==200 and len(r.content)==a['sizeBytes'] and sha(r.content)==a['artifactChecksum']
        status=wait_download(a,p['sessionId'],30,session=s)
        assert status['state']=='Completed' and status['transferredBytes']==a['sizeBytes']
        return {'session':p['sessionId'],'bytes':len(r.content),'sha256':sha(r.content)}
    with concurrent.futures.ThreadPoolExecutor(max_workers=2) as ex:
        results=list(ex.map(receive,ps))
    return {'artifactId':a['id'],'downloads':results}

@scenario('gap/ART-04/s3-cipher-and-plaintext')
def art04():
    a=choose('s3',True,'win11-e2e','mssql'); obj=stored(a,'s3'); pc=prep(T.s,a); c=content(T.s,pc)
    pd=prep(T.s,a,decrypt=True); d=content(T.s,pd)
    assert c.status_code==200 and c.content==obj and obj.startswith(b'BPENC') and pc['filename'].endswith('.bpe') and sha(obj)==a['artifactChecksum']
    assert d.status_code==200 and sha(d.content)==a['sourceChecksum'] and pd['checksum']==a['sourceChecksum'] and not d.content.startswith(b'BPENC')
    plain=choose('s3',False,'host-e2e','postgres'); r=req(T.s,'POST',base(plain)+'/downloads',json={'decrypt':True})
    assert r.status_code==409 and r.json().get('error')=='artifact_not_encrypted'
    return {'artifactId':a['id'],'cipherBytes':len(obj),'cipherSha':sha(obj),'plainBytes':len(d.content),'plainSha':sha(d.content),'unencryptedDecrypt':r.status_code}

@scenario('gap/ART-06/host-pg-real-stored-bytes')
def art06():
    evidence=[]
    for policy in ('Off','Optional','Required'):
        for prov in ('local','sftp'):
            a=next(x for x in all_artifacts() if x['state']=='Available' and x['agentName']=='host-e2e' and x['jobName'].startswith('matrix '+policy+'-'+prov+' ') and x['replicas'][0]['verificationState']=='Verified')
            a=T.call('GET','/backup-artifacts/'+a['id']); blob=stored(a,prov)
            assert len(blob)==a['sizeBytes'] and sha(blob)==a['artifactChecksum']
            if policy=='Off':
                assert a['encryption'] is None and blob.startswith(b'PGDMP') and b'items' in blob
            else:
                e=a['encryption']; assert e['format']=='v2-bpe' and e['keyId'] and e['keyVersion'] and any(r['type']=='tenant-wrap' and r['keyId']==e['keyId'] and r['keyVersion']==e['keyVersion'] for r in e['recipients'])
                assert blob.startswith(b'BPENC') and b'items' not in blob and b'e2e_src' not in blob
            evidence.append({'policy':policy,'provider':prov,'artifactId':a['id'],'bytes':len(blob),'sha256':sha(blob)})
    return {'objects':evidence}

@scenario('gap/ART-06/host-pg-s3-real-stored-bytes')
def art06_s3():
    evidence=[]
    for policy in ('Off','Optional','Required'):
        a=next(x for x in all_artifacts() if x['state']=='Available' and x['agentName']=='host-e2e' and x['jobName'].startswith('gap-hostspecial-postgres-s3-'+policy+' ') and any(r['storageProvider']=='s3' and r['status']=='Available' for r in x['replicas']))
        blob=stored(a,'s3'); assert len(blob)==a['sizeBytes'] and sha(blob)==a['artifactChecksum']
        if policy=='Off':
            assert a['encryption'] is None and blob.startswith(b'PGDMP') and b'items' in blob
        else:
            e=a['encryption']; assert e['format']=='v2-bpe' and any(r['type']=='tenant-wrap' and r['keyId']==e['keyId'] and r['keyVersion']==e['keyVersion'] for r in e['recipients'])
            assert blob.startswith(b'BPENC') and b'items' not in blob and b'e2e_src' not in blob
        evidence.append({'policy':policy,'artifactId':a['id'],'bytes':len(blob),'sha256':sha(blob)})
    return {'objects':evidence}

@scenario('gap/ART-06/windows-mssql-s3-real-stored-bytes')
def art06_mssql():
    evidence=[]
    for policy in ('Off','Optional','Required'):
        a=next(x for x in all_artifacts() if x['state']=='Available' and x['agentName']=='win11-e2e' and (x.get('backupMetadata') or {}).get('engine')=='mssql' and x['jobName'].startswith('matrix '+policy+'-s3 ') and any(r['storageProvider']=='s3' and r['status']=='Available' for r in x['replicas']))
        blob=stored(a,'s3'); assert len(blob)==a['sizeBytes'] and sha(blob)==a['artifactChecksum']
        if policy=='Off':
            assert a['encryption'] is None and blob.startswith(b'MSSQLBAK')
        else:
            e=a['encryption']; assert e['format']=='v2-bpe' and any(r['type']=='tenant-wrap' and r['keyId']==e['keyId'] and r['keyVersion']==e['keyVersion'] for r in e['recipients'])
            assert blob.startswith(b'BPENC') and b'MSSQLBAK' not in blob and 'e2e_src'.encode('utf-16le') not in blob
        evidence.append({'policy':policy,'artifactId':a['id'],'bytes':len(blob),'sha256':sha(blob),'header':blob[:8].hex()})
    return {'objects':evidence}

@scenario('gap/ART-07/key-isolation')
def art07():
    root=f'{api.BASE}/api/tenants/{T.id}/encryption'; platform=f'{api.BASE}/api/platform/tenants/{T.id}/wrap-keys'
    before=req(B.s,'GET',B.base+'/encryption').json(); codes=[]
    for sname,s in [('b-owner',B.s),('b-viewer',role('viewer-b'))]:
        for method,path,body in [('GET',root,None),('PUT',root+'/recovery-key',{'publicKeyPem':'invalid'}),('DELETE',root+'/recovery-key',None),('GET',platform,None),('POST',platform+'/rotate',{})]:
            r=req(s,method,path,**({'json':body} if body is not None else {})); codes.append((sname,method,r.status_code)); assert r.status_code in (401,403,404)
    for name,s in [('owner-a',T.s),('viewer-a',role('viewer-a'))]:
        r=req(s,'GET',platform); codes.append((name,'GET platform',r.status_code)); assert r.status_code in (401,403,404)
    after=req(B.s,'GET',B.base+'/encryption').json(); assert before==after
    keys=req(ADMIN,'GET',platform); assert keys.status_code==200
    assert all(set(x)=={'version','keyId','state','createdAt','artifactCount'} for x in keys.json()['items'])
    return {'denied':codes,'keys':[{k:v for k,v in x.items() if k in ('version','state','artifactCount')} for x in keys.json()['items']],'bIndependent':True}

@scenario('gap/ART-10/api-log-secret-scan')
def art10():
    paths=('/backup-jobs','/backup-runs','/restores','/backup-artifacts','/storage-connections','/agents','/encryption','/notifications/policy','/notifications/channels','/notifications/deliveries')
    statuses={path:req(T.s,'GET',T.base+path).status_code for path in paths}
    for path in ('/api/platform/audit','/api/platform/notification-transports',f'/api/platform/tenants/{T.id}/wrap-keys'):
        statuses[path]=req(ADMIN,'GET',api.BASE+path).status_code
    assert all(s==200 for s in statuses.values()), f'endpoint statuses {statuses}'
    known=[]
    def collect(x,k=''):
        if isinstance(x,dict):
            for key,v in x.items(): collect(v,key)
        elif isinstance(x,list):
            for v in x: collect(v,k)
        elif isinstance(x,str) and len(x)>=8 and any(z in k.lower() for z in ('password','secret','token')): known.append(x.encode())
    collect(ST); collect(lib.CRED); collect(api.env)
    # The lab database password equals the public engine name, making substring matches ambiguous.
    known=[v for v in known if v != b'postgres']
    for session in (T.s,B.s,ADMIN):
        known.extend(c.value.encode() for c in session.cookies if len(c.value)>=16)
    sources={f'api:{name}':data for name,data in CAPTURE}
    commands={'docker:bp-api':['docker','logs','bp-api'], 'docker:bp-api2':['docker','logs','bp-api2'], 'journal:host-agent':['journalctl','--user','-u','bpe2e-host-agent','--no-pager'], 'journal:fedora-agent':['ssh','virtual-fedora','sudo','-n','journalctl','-u','backup-agent','--no-pager']}
    errors=[]
    for name,cmd in commands.items():
        p=subprocess.run(cmd,capture_output=True,timeout=90); sources[name]=p.stdout+p.stderr
        if p.returncode: errors.append(f'{name} exit {p.returncode}')
    rc,out=lib.win("$p='C:\\ProgramData\\BackupAgent'; if(Test-Path $p){ Get-ChildItem -Path $p -Recurse -File -ErrorAction SilentlyContinue | Where-Object { $_.Extension -in '.log','.txt' } | ForEach-Object { Get-Content -Raw -LiteralPath $_.FullName -ErrorAction SilentlyContinue } } else { exit 42 }")
    if rc: errors.append(f'windows logs exit {rc}')
    else: sources['windows:BackupAgent logs']=out.encode()
    counts={}
    for name,data in sources.items():
        n=sum(data.count(v) for v in set(known)) + len(re.findall(rb'-----BEGIN (?:RSA |EC |ENCRYPTED )?PRIVATE KEY-----',data))
        if n: counts[name]=n
    assert not counts, 'secret matches by source: '+json.dumps(counts)
    assert not errors, 'log read incomplete: '+', '.join(errors)
    return {'sources':len(sources),'secretMatches':counts,'knownValues':len(set(known)),'endpointStatuses':statuses}

@scenario('gap/ART-07/recovery-key-replacement-offline')
def recovery():
    key1,pub1=fp.gen_rsa(); key2,pub2=fp.gen_rsa()
    made=[]
    with open('/tmp/gap-recovery-key.lock','w') as lock:
        fcntl.flock(lock,fcntl.LOCK_EX)
        assert T.call('GET','/encryption')['tenantRecovery'] is None, 'recovery key slot already occupied'
        try:
            v=T.call('PUT','/encryption/recovery-key',{'publicKeyPem':pub1}); id1=v['tenantRecovery']['keyId']
            a=create_file('recovery1'); made.append(a)
            assert any(x['type']=='tenant-recovery' and x['keyId']==id1 for x in a['encryption']['recipients'])
            v=T.call('PUT','/encryption/recovery-key',{'publicKeyPem':pub2}); id2=v['tenantRecovery']['keyId']
            assert id1!=id2
            b=create_file('recovery2'); made.append(b)
            assert any(x['type']=='tenant-recovery' and x['keyId']==id2 for x in b['encryption']['recipients'])
            with tempfile.TemporaryDirectory(prefix='gap-jr-art-') as wd:
                for art,key in ((a,key1),(b,key2)):
                    rc,_,plain=fp.decrypt_offline(stored(art,'s3'),key,wd)
                    assert rc==0 and sha(plain)==art['sourceChecksum'], 'offline checksum mismatch'
                rc,_,plain=fp.decrypt_offline(stored(b,'s3'),key1,wd)
                assert rc!=0 and plain is None, 'old recovery key decrypted replacement artifact'
            assert 'PRIVATE KEY' not in json.dumps(T.call('GET','/encryption'))
        finally:
            r=T.raw('DELETE','/encryption/recovery-key')
            assert r.status_code==204 and T.call('GET','/encryption')['tenantRecovery'] is None, 'recovery key cleanup failed'
            for art in made: T.raw('DELETE','/backup-artifacts/'+art['id'])
    return {'artifactIds':[x['id'] for x in made],'oldKeyRejected':True,'bothSourceChecksumsMatch':True,'recoveryKeyRemoved':True}

@scenario('gap/ART-07/offline-pg-decrypt-restore')
def recovery_pg():
    key,pub=fp.gen_rsa(); a=None; db='gap_jr_art_'+uuid.uuid4().hex[:8]
    with open('/tmp/gap-recovery-key.lock','w') as lock:
        fcntl.flock(lock,fcntl.LOCK_EX)
        assert T.call('GET','/encryption')['tenantRecovery'] is None, 'recovery key slot already occupied'
        try:
            v=T.call('PUT','/encryption/recovery-key',{'publicKeyPem':pub}); kid=v['tenantRecovery']['keyId']
            a=create_pg('Required',[flows.local_dest()],'recovery-pg')
            assert any(r['type']=='tenant-recovery' and r['keyId']==kid for r in a['encryption']['recipients'])
            with tempfile.TemporaryDirectory(prefix='gap-jr-art-') as wd:
                rc,_,plain=fp.decrypt_offline(stored(a,'local'),key,wd)
                assert rc==0 and plain.startswith(b'PGDMP') and sha(plain)==a['sourceChecksum']
            gc.host_pg(f'create database \"{db}\"')
            pg=ST['host']['pg']
            p=subprocess.run(['docker','exec','-i','-e','PGPASSWORD='+pg['password'],gc.HOST_PG,'pg_restore','-h','127.0.0.1','-U',pg['user'],'-d',db,'--exit-on-error'],input=plain,capture_output=True,timeout=180)
            assert p.returncode==0, f'pg_restore exit {p.returncode}'
            digest=gc.host_pg_digest(db); assert digest==gc.host_pg_digest('e2e_src')
            return {'artifactId':a['id'],'sourceSha256':sha(plain),'restoredDigest':digest,'database':db}
        finally:
            try:
                gc.host_pg_drop(db)
            finally:
                r=T.raw('DELETE','/encryption/recovery-key')
                assert r.status_code==204 and T.call('GET','/encryption')['tenantRecovery'] is None, 'recovery key cleanup failed'
                if a: T.raw('DELETE','/backup-artifacts/'+a['id'])

@scenario('gap/ART-05/browser-direct-abort')
def browser_abort():
    import browser, webrtc
    a=choose('local',False,'host-e2e','postgres')
    b=browser.Browser()
    try:
        sink=webrtc.SINK.replace('write: async chunk => {','write: async chunk => { await new Promise(resolve => setTimeout(resolve, 150));')
        b.init_script(sink)
        b.goto(webrtc.UI+'/login')
        b.type('input[autocomplete=username]',ST['a']['login']); b.type('input[type=password]',ST['a']['password']); b.click_text('Sign In')
        b.wait("location.pathname !== '/login'")
        b.goto(f"{webrtc.UI}/tenants/{T.id}/artifacts/{a['id']}")
        b.click_text('Download'); b.click_text('Download Backup'); b.click_text('Save file')
        n=b.wait('window.__dl.bytes >= 65536 && window.__dl.bytes',timeout=180,what='partial direct bytes')
        sid=re.search(r'/downloads/([0-9a-f-]{36})',' '.join(b.eval("performance.getEntriesByType('resource').map(r => r.name)"))).group(1)
        assert n<a['sizeBytes'] and not b.eval('window.__dl.closed')
    finally: b.close()
    st=wait_download(a,sid)
    assert st['state'] in ('Failed','Cancelled') and st['transferredBytes']<a['sizeBytes'], f'status={st}'
    return {'artifactId':a['id'],'sessionId':sid,'receivedBeforeAbort':n,'state':st['state'],'failureCode':st['failureCode'],'transferredBytes':st['transferredBytes']}

def direct(a,user,decrypt=False):
    import browser,webrtc
    b=browser.Browser()
    try:
        b.init_script(webrtc.SINK)
        b.goto(webrtc.UI+'/login')
        b.type('input[autocomplete=username]',user['login']); b.type('input[type=password]',user['password']); b.click_text('Sign In')
        b.wait("location.pathname !== '/login'")
        b.goto(f"{webrtc.UI}/tenants/{T.id}/artifacts/{a['id']}")
        b.click_text('Download')
        if decrypt:
            b.wait("(() => { const r = document.querySelectorAll('input[type=radio]'); if(r.length<2) return false; r[1].click(); return true })()")
        b.click_text('Download decrypted file' if decrypt else 'Download Backup')
        b.click_text('Save file')
        result=b.wait("window.__dl.closed ? 'saved' : window.__dl.aborted ? 'aborted' : false",timeout=240)
        assert result=='saved', f'browser {result}'
        got=b.eval("window.__dlSha256().then(sha256 => ({bytes: window.__dl.bytes, sha256}))")
        sid=re.search(r'/downloads/([0-9a-f-]{36})',' '.join(b.eval("performance.getEntriesByType('resource').map(r => r.name)"))).group(1)
    finally: b.close()
    s=wait_download(a,sid,session=api.login(user['login'],user['password']))
    assert got['sha256']==(a['sourceChecksum'] if decrypt else a['artifactChecksum']) and s['state']=='Completed'
    if not decrypt: assert got['bytes']==a['sizeBytes']
    return {'artifactId':a['id'],'session':sid,**got}

@scenario('gap/ART-01/direct-local-sftp-owner-admin')
def direct_roles():
    out=[]
    for provider in ('local','sftp'):
        a=choose(provider,False,'host-e2e','postgres'); blob=stored(a,provider)
        assert sha(blob)==a['artifactChecksum'] and len(blob)==a['sizeBytes']
        for name,user in [('owner',ST['a']),('admin',ST['roles']['admin-a'])]:
            out.append({'provider':provider,'role':name,**direct(a,user)})
    return {'downloads':out}

@scenario('gap/ART-04/direct-sftp-cipher-and-plaintext')
def direct_encrypted():
    a=choose('sftp',True,'host-e2e','postgres'); blob=stored(a,'sftp')
    assert blob.startswith(b'BPENC') and sha(blob)==a['artifactChecksum']
    return {'ciphertext':direct(a,ST['a']), 'decrypted':direct(a,ST['a'],True)}

@scenario('gap/ART-03/missing-replica-after-prepare')
def missing_replica():
    a=create_pg('Off',[fp.dest('s3')],'missing-replica')
    a=T.call('GET','/backup-artifacts/'+a['id']); p=prep(T.s,a); r=replica(a,'s3')
    path='l/bpe2e/tenants/'+T.id.replace('-','')+'/connections/'+r['storageConnectionId'].replace('-','')+'/'+r['key']
    cmd='mc --insecure alias set l https://localhost:9000 "$MINIO_ROOT_USER" "$MINIO_ROOT_PASSWORD" >/dev/null 2>&1 && mc --insecure rm '+shlex.quote(path)
    try:
        proc=subprocess.run(['docker','exec','bpe2e-minio','sh','-c',cmd],capture_output=True,timeout=60)
        assert proc.returncode==0, f'own replica removal exit {proc.returncode}'
        result=content(T.s,p)
        status=wait_download(a,p['sessionId'],30)
        assert result.status_code==409 and status['state']=='Failed' and status['failureCode'] in ('replica_unavailable','transfer_failed'), f'content={result.status_code} status={status}'
        again=req(T.s,'POST',base(a)+'/downloads',json={'replicaId':r['id']})
        assert again.status_code==409, f'missing replica prepare={again.status_code}'
        return {'artifactId':a['id'],'sessionId':p['sessionId'],'contentStatus':result.status_code,'failureCode':status['failureCode'],'newPrepare':again.status_code}
    finally:
        T.raw('DELETE','/backup-artifacts/'+a['id'])

@scenario('gap/ART-04/tampered-ciphertext-decrypt')
def tampered():
    a=create_file('tamper'); r=replica(a,'s3'); data=bytearray(stored(a,'s3'))
    path='l/bpe2e/tenants/'+T.id.replace('-','')+'/connections/'+r['storageConnectionId'].replace('-','')+'/'+r['key']
    alias='mc --insecure alias set l https://localhost:9000 "$MINIO_ROOT_USER" "$MINIO_ROOT_PASSWORD" >/dev/null 2>&1 && '
    try:
        p=prep(T.s,a,decrypt=True)
        data[-50]^=1
        proc=subprocess.run(['docker','exec','-i','bpe2e-minio','sh','-c',alias+'mc --insecure pipe '+shlex.quote(path)],input=data,capture_output=True,timeout=60)
        assert proc.returncode==0, f'own replica tamper exit {proc.returncode}'
        result=content(T.s,p); status=wait_download(a,p['sessionId'],30)
        assert result.status_code==409 and status['state']=='Failed' and status['failureCode']=='integrity_mismatch', f'content={result.status_code} status={status}'
        return {'artifactId':a['id'],'sessionId':p['sessionId'],'contentStatus':result.status_code,'failureCode':status['failureCode']}
    finally:
        subprocess.run(['docker','exec','bpe2e-minio','sh','-c',alias+'mc --insecure rm '+shlex.quote(path)],capture_output=True,timeout=60)
        T.raw('DELETE','/backup-artifacts/'+a['id'])

if __name__=='__main__':
    import sys
    checks=[art01,direct_roles,art02,csrf_content,art03,concurrent_downloads,art04,direct_encrypted,browser_abort,art06,art06_s3,art06_mssql,art07,recovery,recovery_pg,art10,missing_replica,tampered]
    for check in checks:
        if len(sys.argv)==1 or sys.argv[1] in check.scenario: check()
