"""Private storage security E2E; records only statuses, never credentials."""
import json, secrets, subprocess, sys, time, uuid
import api, lib, flows, gap_sp_common as g

P = 'gap-sp-sec-'
T = A = None
connections, jobs, artifacts = [], [], []

def run(label, fn):
    if len(sys.argv)>1 and not any(arg in label for arg in sys.argv[1:]): return
    try:
        result = fn()
        lib.record('gap/' + label, True, **(result or {}))
    except Exception as e:
        lib.record('gap/' + label, False, error=type(e).__name__ + ': ' + (str(e) if isinstance(e, AssertionError) else 'operation failed'))

def conn(provider, cfg, sec):
    r = T.raw('POST', '/storage-connections', {'providerType': provider, 'name': P + uuid.uuid4().hex[:8], 'configuration': cfg, 'secret': sec})
    assert r.status_code in (200, 201), 'create HTTP ' + str(r.status_code)
    c = r.json(); connections.append(c['id']); return c

def test(c):
    r = T.raw('POST', '/storage-connections/' + c['id'] + '/test', {})
    assert r.status_code == 200, 'test HTTP ' + str(r.status_code)
    x = r.json()['test']; return x['status'], x.get('code')

def s3cfg(endpoint=None):
    return {'endpoint': endpoint or 'https://' + api.env['PUBLIC_HOST'] + ':9000', 'bucket': 'bpe2e', 'region': 'us-east-1'}

def credentials(): return {'accessKey': api.env['E2E_MINIO_USER'], 'secretKey': api.env['E2E_MINIO_PASSWORD']}

def job(c, provider, label):
    cfg = g.gcfg([g.gdest(provider, conn=c['id'], primary=True)])
    j = g.new_job(T, A, 'sec-' + label, cfg, retryPolicy={'maxAttempts': 1}); jobs.append(j['id'])
    rid = g.start_run(T, j)
    end = time.time() + 180
    while time.time() < end:
        d = T.call('GET', '/backup-runs/' + rid)
        failed = [x for x in d['attempts'] if x['status'] in ('Failed', 'Cancelled', 'AgentLost')]
        if failed or d['status'] in lib.TERMINAL or d['status'] == 'Paused':
            if d['status'] not in lib.TERMINAL: T.raw('POST', '/backup-runs/' + rid + '/cancel', {})
            artifacts.extend(a['id'] for a in g.arts(d))
            return d, failed
        time.sleep(2)
    T.raw('POST', '/backup-runs/' + rid + '/cancel', {})
    raise AssertionError('run timeout')

def negative_s3(endpoint=None, secret=None):
    c = conn('s3', s3cfg(endpoint), secret or credentials()); status, code = test(c)
    assert status == 'Failed', 'unexpected test status ' + str((status, code))
    return {'test': status, 'code': code}

def mc(*args):
    command = 'mc --insecure alias set gap https://localhost:9000 "$MINIO_ROOT_USER" "$MINIO_ROOT_PASSWORD" >/dev/null 2>&1 && exec mc --insecure "$@"'
    return subprocess.run(['docker', 'exec', 'bpe2e-minio', 'sh', '-c', command, 'sh', *args], stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL).returncode

def restricted():
    user = P + uuid.uuid4().hex[:8]; pw = secrets.token_urlsafe(24)
    assert mc('admin', 'user', 'add', 'gap', user, pw) == 0, 'MinIO user create failed'
    try:
        assert mc('admin', 'policy', 'attach', 'gap', 'readonly', '--user', user) == 0, 'MinIO readonly policy attach failed'
        c = conn('s3', s3cfg(), {'accessKey': user, 'secretKey': pw})
        st, code = test(c); d, bad = job(c, 's3', 'readonly')
        assert bad and d['status'] != 'Succeeded', 'readonly account unexpectedly wrote backup'
        return {'connectionTest': [st, code], 'run': d['status'], 'reasonCode': bad[0].get('reasonCode')}
    finally:
        assert mc('admin', 'user', 'remove', 'gap', user) == 0, 'MinIO temporary user cleanup failed'

def restricted_read():
    user=P+uuid.uuid4().hex[:8]; pw=secrets.token_urlsafe(24); key=P+uuid.uuid4().hex
    assert mc('admin','user','add','gap',user,pw)==0, 'MinIO user create failed'
    policy={'Version':'2012-10-17','Statement':[{'Effect':'Deny','Action':['s3:GetObject'],'Resource':['arn:aws:s3:::bpe2e/'+key]}]}
    policy_path='/tmp/'+user+'.json'
    created=False
    try:
        script='cat > "$1"'
        result=subprocess.run(['docker','exec','-i','bpe2e-minio','sh','-c',script,'sh',policy_path],input=json.dumps(policy),capture_output=True,text=True)
        assert result.returncode==0, 'policy file create failed'
        assert mc('admin','policy','create','gap',user,policy_path)==0, 'policy create failed'
        created=True
        assert mc('admin','policy','attach','gap','readonly','--user',user)==0, 'readonly attach failed'
        assert mc('admin','policy','attach','gap',user,'--user',user)==0, 'deny policy attach failed'
        script='printf gap-sp-sec-test | mc --insecure pipe \"$1\" >/dev/null'
        result=subprocess.run(['docker','exec','bpe2e-minio','sh','-c',script,'sh','gap/bpe2e/'+key],capture_output=True)
        assert result.returncode==0, 'own object creation failed'
        script="""d=/tmp/$1; trap 'rm -rf "$d"' EXIT; mc --config-dir "$d" --insecure alias set restricted https://localhost:9000 "$2" "$3" >/dev/null 2>&1 && mc --config-dir "$d" --insecure cat "restricted/bpe2e/$4" """
        result=subprocess.run(['docker','exec','bpe2e-minio','sh','-c',script,'sh',user,user,pw,key],capture_output=True,text=True)
        denied = (result.stderr + result.stdout).lower()
        # mc words a 403 as "Insufficient permissions"; MinIO's own S3 error is AccessDenied.
        assert result.returncode != 0 and ('access denied' in denied or 'insufficient permissions' in denied), 'restricted read did not return access denied: ' + denied.replace(pw.lower(), '***')[:200]
        return {'read':'access_denied','ownObject':True,'layer':'MinIO policy enforcement only; the platform read path is not exercised'}
    finally:
        mc('rm','--force','gap/bpe2e/'+key)
        rc=mc('admin','user','remove','gap',user)
        prc=mc('admin','policy','remove','gap',user) if created else 0
        subprocess.run(['docker','exec','bpe2e-minio','rm','-f',policy_path],stdout=subprocess.DEVNULL,stderr=subprocess.DEVNULL)
        assert rc==0 and prc==0, 'MinIO temporary account or policy cleanup failed'

def secret_update():
    mark = 'gap-sp-sec-' + secrets.token_hex(16)
    c = conn('s3', s3cfg(), credentials()); cid = c['id']; before = test(c)
    assert before[0] == 'Succeeded', 'baseline test ' + str(before)
    r = T.raw('PUT', '/storage-connections/' + cid + '/secret', {'values': {'accessKey': 'gap-sp-sec-invalid', 'secretKey': mark}})
    assert r.status_code == 200, 'secret update HTTP ' + str(r.status_code)
    after = test(c); assert after[0] == 'Failed', 'wrong secret test ' + str(after)
    responses = [r.text, T.raw('GET', '/storage-connections/' + cid).text, T.raw('GET', '/storage-connections').text]
    assert all(mark not in v for v in responses), 'secret value appeared in API'
    r = T.raw('PUT', '/storage-connections/' + cid + '/secret', {'values': credentials()})
    assert r.status_code == 200, 'restore secret HTTP ' + str(r.status_code)
    restored = test(c); assert restored[0] == 'Succeeded', 'restored test ' + str(restored)
    return {'baseline': before, 'invalid': after, 'restored': restored, 'plaintextResponses': 3}

def isolation():
    c = conn('s3', s3cfg(), credentials()); b = lib.Tenant('b'); cid = c['id']
    probes = [('read','GET','',None), ('test','POST','/test',{}), ('secret','PUT','/secret',{'values': {'accessKey':'invalid','secretKey':'invalid-secret'}}), ('update','PUT','',{'name': P+'hijack'})]
    out = {}
    for label, method, suffix, body in probes:
        r = b.raw(method, '/storage-connections/' + cid + suffix, body); out[label] = r.status_code
    assert all(x in (403,404) for x in out.values()), 'cross tenant status ' + str(out)
    assert test(c)[0] == 'Succeeded', 'connection altered by foreign tenant'
    agents = list(b.agents().values()); assert agents, 'tenant B agent unavailable'
    cfg = g.gcfg([g.gdest('s3', conn=cid, primary=True)])
    r = b.raw('POST','/backup-jobs', {'name':P+'foreign-'+uuid.uuid4().hex[:6], 'agentId':agents[0]['agentId'], 'overlapPolicy':'Skip', 'configuration':cfg})
    out['jobCreate'] = r.status_code
    if r.status_code in (200,201):
        jid=r.json()['id']; r2=b.raw('POST','/backup-jobs/'+jid+'/runs',{}); out['jobRun']=r2.status_code
        if r2.status_code in (200,201,202):
            rid=r2.json().get('id'); b.raw('POST','/backup-runs/'+rid+'/cancel',{})
        b.raw('POST','/backup-jobs/'+jid+'/disable',{}); b.raw('DELETE','/backup-jobs/'+jid)
        assert r2.status_code in (400,403,404,409,422), 'foreign job run admitted ' + str(out)
    else: assert r.status_code in (400,403,404,409,422), 'foreign job admission unexpected ' + str(out)
    return out

def isolation_download():
    b=lib.Tenant('b')
    j=g.new_job(T,A,'sec-download',g.gcfg([g.gdest('local')]),retryPolicy={'maxAttempts':1}); jobs.append(j['id'])
    rid,d=g.run_to_end(T,j,timeout=180)
    assert d['status']=='Succeeded', 'own backup status '+d['status']
    aa=g.arts(d); assert len(aa)==1, 'own artifact missing'
    aid=aa[0]['id']; artifacts.append(aid)
    out={}
    for name,method,path,body in [('read','GET','',None),('availability','GET','/download-availability',None),('download','POST','/downloads',{})]:
        r=b.raw(method,'/backup-artifacts/'+aid+path,body); out[name]=r.status_code
    assert all(code in (403,404) for code in out.values()), 'foreign artifact responses '+str(out)
    return out

def smb_case(host, share, expected):
    c=conn('smb',{'host':host,'share':share},{'username':api.env['E2E_SMB_USER'],'password':api.env['E2E_SMB_PASSWORD']})
    d,bad=job(c,'smb','smb')
    assert (d['status'] == 'Succeeded') == expected, 'SMB status '+d['status']+' reason '+str(bad[0].get('reasonCode') if bad else None)
    return {'status':d['status'],'reasonCode':bad[0].get('reasonCode') if bad else None}

def ssrf(url):
    c=conn('s3',s3cfg(url),credentials()); status,code=test(c)
    # A refused address answers like SFTP does (the agent reaches private storage), the same for every port; a refusal that came from a real connection attempt would be network_unavailable or a hang.
    assert (status, code) in (('NotTestable', 'agent_side_storage'), ('Failed', 'network_denied')), 'endpoint not denied '+str((status,code))
    return {'test':status,'code':code}

def sftp_case(host, allowed):
    cfg={'host':host,'port':'22','username':'gap-sp-sec-invalid','authenticationType':'password','remoteBaseDirectory':'/gap-sp-sec','hostKeyFingerprint':'SHA256:AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA'}
    cursor=subprocess.run(['journalctl','--user','-u','gap-sp-sec-agent','-n','0','--show-cursor','--no-pager'],capture_output=True,text=True,check=True).stdout.split('-- cursor: ')[-1].strip()
    c=conn('sftp',cfg,{'password':'gap-sp-sec-invalid'}); d,bad=job(c,'sftp','sftp')
    assert bad, 'agent did not report failure: '+d['status']
    logs=subprocess.run(['journalctl','--user','-u','gap-sp-sec-agent','--after-cursor',cursor,'--no-pager','-o','cat'],capture_output=True,text=True,check=True).stdout
    denied='Target SFTP IP address belongs to a private, loopback, or link-local network and is not explicitly allowed.' in logs
    reached='SFTP host key does not match configured fingerprint.' in logs
    assert (reached if allowed else denied), 'missing agent evidence: denied='+str(denied)+' hostKeyMismatch='+str(reached)
    return {'reasonCode':bad[0].get('reasonCode'),'networkDenied':denied,'hostKeyMismatch':reached}

def main():
    global T,A
    T=g.ensure_tenant('sec'); A=g.ensure_agent('sec','sec',sftp_allow=['192.168.122.0/24'])
    (g.agent_root('sec')/'files'/'gap-sp-sec.txt').write_text('gap-sp-sec test\n')
    try:
        # A closed port on the allow-listed lab network: a loopback endpoint is now refused (STO-14), which is not the "wrong endpoint" case.
        run('STO-03/wrong-endpoint',lambda:negative_s3('https://'+api.env['PUBLIC_HOST']+':1'))
        run('STO-03/wrong-access-key',lambda:negative_s3(secret={'accessKey':'gap-sp-sec-invalid','secretKey':secrets.token_urlsafe(20)}))
        run('STO-03/restricted-write',restricted)
        run('STO-03/restricted-read',restricted_read)
        run('STO-12/secret-lifecycle',secret_update)
        run('STO-13/tenant-b-isolation',isolation)
        run('STO-13/tenant-b-download',isolation_download)
        for name,host,allowed in [('loopback','127.0.0.1',False),('private-outside','10.77.61.254',False),('private-inside','192.168.122.115',True),('link-local','169.254.0.1',False),('ipv6-loopback','::1',False),('ipv6-ula','fd00::1',False),('mapped-loopback','::ffff:127.0.0.1',False),('mapped-inside','::ffff:192.168.122.115',True)]:
            run('STO-14/sftp-'+name,lambda h=host,a=allowed:sftp_case(h,a))
        for name,url in [('loopback','https://127.0.0.1:1'),('private','https://10.77.61.254:1'),('link-local','https://169.254.0.1:1'),('ipv6','https://[::1]:1')]:
            run('STO-14/s3-'+name,lambda u=url:ssrf(u))
        run('STO-04/smb-reachable',lambda:smb_case(api.env['PUBLIC_HOST'],'Data',True))
        run('STO-04/smb-unreachable',lambda:smb_case('192.0.2.1','gap-sp-sec',False))
    finally:
        for aid in artifacts: T.raw('DELETE','/backup-artifacts/'+aid)
        for jid in jobs:
            T.raw('POST','/backup-jobs/'+jid+'/disable',{}); T.raw('DELETE','/backup-jobs/'+jid)
        for cid in connections: T.raw('DELETE','/storage-connections/'+cid)
        (g.agent_root('sec')/'files'/'gap-sp-sec.txt').unlink(missing_ok=True)
        g.stop_agent('sec')

if __name__ == '__main__': main()
