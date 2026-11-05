"""Private-tenant bandwidth E2E measurements. No credentials are printed."""
import bisect, datetime as dt, json, math, os, pathlib, shutil, statistics, sys, threading, time, uuid
from zoneinfo import ZoneInfo
import gap_sp_common as G
import lib

MB = 1024 * 1024
ROOT = G.agent_root
OUT = pathlib.Path('/tmp/gap-sp/bw-measurements.jsonl')

class Sampler:
    def __init__(self, path):
        self.path = path; self.rows = []; self.stop_event = threading.Event()
        self.thread = threading.Thread(target=self.run, daemon=True)
    def run(self):
        while not self.stop_event.is_set():
            total = 0
            for root, dirs, files in os.walk(self.path):
                for f in files:
                    try: total += os.stat(os.path.join(root, f)).st_size
                    except FileNotFoundError: pass
            self.rows.append((time.time(), total))
            self.stop_event.wait(.5)
    def start(self): self.thread.start(); return self
    def stop(self): self.stop_event.set(); self.thread.join()

def at(rows, stamp):
    ts = [r[0] for r in rows]; i = bisect.bisect_left(ts, stamp)
    if i <= 0: return rows[0][1]
    if i >= len(rows): return rows[-1][1]
    a,b = rows[i-1],rows[i]
    return a[1] + (b[1]-a[1]) * (stamp-a[0])/(b[0]-a[0])

def stats(rows, start=None, end=None):
    assert len(rows) >= 3, 'insufficient destination samples'
    if start is None:
        i = next(i for i, x in enumerate(rows) if x[1] > 0)
        start = rows[max(0, i-1)][0]
    if end is None:
        final = max(x[1] for x in rows)
        end = next(x[0] for x in rows if x[1] == final)
    assert end-start >= 1, f'measurement window too short: {end-start}'
    one = [(at(rows, x+1)-at(rows, x)) for x in range(math.ceil(start), math.floor(end))]
    five = sorted((at(rows, x+5)-at(rows, x))/5 for x in range(math.ceil(start), math.floor(end)-4))
    return {'start': round(start,3), 'end': round(end,3), 'seconds': round(end-start,2), 'bytes': round(at(rows,end)-at(rows,start)), 'mean_Bps': round((at(rows,end)-at(rows,start))/(end-start)), 'stdev_1s_Bps': round(statistics.pstdev(one)) if one else None, 'p95_5s_Bps': round(five[math.ceil(.95*len(five))-1]) if five else None, 'max_5s_Bps': round(max(five)) if five else None}

def save(label, rows, extra):
    with OUT.open('a') as f: f.write(json.dumps({'case':label,'samples':rows,**extra},default=str)+'\n')

def policy(t, scope, value): return t.call('PUT','/upload-bandwidth/'+scope,{'bytesPerSecond':value})
def assign(t, scope, value): return t.call('PUT','/upload-bandwidth/'+scope+'/profile',{'profileId':value})
def window(day, start, end, rate): return {'dayOfWeek':day,'startTime':start,'endTime':end,'bytesPerSecond':rate}
def profile(t, label, zone, windows): return t.call('POST','/bandwidth-profiles',{'name':'gap-sp-bw-'+label+'-'+uuid.uuid4().hex[:5], 'timeZoneId':zone, 'windows':windows})['id']
def wd(d): return (d.weekday()+1)%7

def job(t, agent, name, size):
    G.put_file(agent, name+'/source.bin', size)
    return G.new_job(t, t.agents()['gap-sp-'+agent], 'bw-'+name, G.gcfg([G.gdest('local')], paths=[name]))

def cleanup(t, j, detail=None):
    try:
        if detail:
            for a in G.arts(detail):
                r = t.raw('DELETE', '/backup-artifacts/'+a['id'])
                if r.status_code not in (200,202,204): print('cleanup artifact',r.status_code,a['id'],flush=True)
        t.raw('POST','/backup-jobs/'+j['id']+'/disable',{})
        policy(t,'jobs/'+j['id'],None); assign(t,'jobs/'+j['id'],None)
    except Exception as e: print('cleanup error',repr(e),flush=True)

def monitor(t, j, agent, timeout=300, callback=None):
    rid=G.start_run(t,j); s=Sampler(ROOT(agent)/'local-store'/t.id.replace('-','')/j['id'].replace('-','')).start()
    stage=[]; detail=None; start=time.time()
    try:
        while time.time()-start < timeout:
            detail=t.call('GET','/backup-runs/'+rid); a=detail['attempts'][-1]
            stage.append((round(time.time(),3),detail['status'],a.get('progressStage'),a.get('progressAt'),a.get('pauseReason'),a.get('reasonCode')))
            if callback: callback(t, rid, detail, s)
            if detail['status'] in lib.TERMINAL: break
            time.sleep(1)
        else: raise TimeoutError(rid)
        return rid,detail,s.rows,stage
    finally: s.stop()

def cap_assert(s, rate):
    assert s['mean_Bps'] <= 1.12*rate, f'mean={s["mean_Bps"]}, ceiling={rate}; {s}'
    assert s['p95_5s_Bps'] is None or s['p95_5s_Bps'] <= 1.15*rate, f'p95={s["p95_5s_Bps"]}, expected={rate}; {s}'
    s['limit_Bps']=rate; s['relative_error_pct']=round((s['mean_Bps']/rate-1)*100,2)
    return s

def midnight(zone='Asia/Kathmandu', agent='bwt'):
    t=G.GT('bwt'); now=dt.datetime.now(ZoneInfo(zone)); mid=(now+dt.timedelta(days=1)).replace(hour=0,minute=0,second=0,microsecond=0); epoch=mid.timestamp(); day=wd(mid-dt.timedelta(days=1))
    assert 60 < epoch-time.time() < 1800, f'midnight is {epoch-time.time():.0f}s away'
    j=job(t,agent,'midnight-'+agent,112*MB); p=None; d=None
    try:
        p=profile(t,'midnight',zone,[window(day,'23:00:00','23:59:30',256*1024),window(day,'23:59:30','00:00:30',MB),window((day+1)%7,'00:00:30','01:00:00',512*1024)])
        assign(t,'jobs/'+j['id'],p)
        time.sleep(max(0,epoch-62-time.time()))
        rid,d,rows,stages=monitor(t,j,agent,timeout=300)
        data={'zone':zone,'midnight_utc':dt.datetime.fromtimestamp(epoch,dt.timezone.utc).isoformat(), 'run':rid,'status':d['status'],'stages':stages,'before':stats(rows,epoch-55,epoch-35),'pre_midnight':stats(rows,epoch-23,epoch-3),'post_midnight':stats(rows,epoch+3,epoch+23),'after':stats(rows,epoch+36,epoch+56)}
        save('midnight-'+agent,rows,data)
        assert d['status']=='Succeeded',d['status']
        for label,rate in [('before',256*1024),('pre_midnight',MB),('post_midnight',MB),('after',512*1024)]: cap_assert(data[label],rate)
        return data
    finally:
        cleanup(t,j,d)
        if p: t.raw('DELETE','/bandwidth-profiles/'+p)
        shutil.rmtree(ROOT(agent)/'files'/('midnight-'+agent),ignore_errors=True)

if __name__=='__main__' and len(sys.argv)>1 and sys.argv[1]=='midnight':
    lib.scenario('gap/POL-09/midnight-'+(sys.argv[3] if len(sys.argv)>3 else 'bwt'))(lambda:midnight(sys.argv[2] if len(sys.argv)>2 else 'Asia/Kathmandu',sys.argv[3] if len(sys.argv)>3 else 'bwt'))()

def static_case(label, limits, rate, size=16*MB):
    t=G.GT('bw'); j=job(t,'bw',label,size); d=None; scopes=[]
    try:
        for scope,value in limits:
            scope=scope.replace('{job}',j['id']).replace('{agent}',t.agents()['gap-sp-bw']['agentId'])
            policy(t,scope,value); scopes.append(scope)
        rid,d,rows,stages=monitor(t,j,'bw')
        s=stats(rows); s.update(run=rid,status=d['status'],stage_observations=stages)
        save(label,rows,s)
        assert d['status']=='Succeeded',d['status']
        return cap_assert(s,rate)
    finally:
        for scope in scopes: policy(t,scope,None)
        cleanup(t,j,d); shutil.rmtree(ROOT('bw')/'files'/label,ignore_errors=True)

def static_change():
    t=G.GT('bw'); j=job(t,'bw','change',80*MB); d=None; changes=[]
    scope='tenant'; policy(t,scope,MB)
    def callback(t,rid,d,s):
        if not s.rows or s.rows[-1][1] < 8*MB: return
        if not changes:
            policy(t,scope,3*MB); changes.append(('raise',time.time()))
        elif len(changes)==1 and time.time()-changes[0][1] > 12:
            policy(t,scope,None); changes.append(('remove',time.time()))
    try:
        rid,d,rows,stages=monitor(t,j,'bw',callback=callback)
        assert len(changes)==2,changes
        before=stats(rows,changes[0][1]-6,changes[0][1])
        after=stats(rows,changes[0][1]+5,changes[1][1]-1)
        post=[(x,y) for x,y in rows if x>=changes[1][1]]
        first=next((x for x in range(math.ceil(changes[1][1]),math.floor(rows[-1][0])) if at(rows,x+1)-at(rows,x)>4*MB),None)
        data={'run':rid,'status':d['status'],'changes':changes,'before':before,'after':after,'remove_detected_lag_s':round(first-changes[1][1],2) if first else None,'stage_observations':stages}
        save('change',rows,data); assert d['status']=='Succeeded',d['status']
        cap_assert(before,MB); cap_assert(after,3*MB)
        assert first is not None and first-changes[1][1]<8, data
        return data
    finally: policy(t,scope,None); cleanup(t,j,d); shutil.rmtree(ROOT('bw')/'files'/'change',ignore_errors=True)

def shared():
    t=G.GT('bw'); j1=job(t,'bw','share1',10*MB); j2=job(t,'bw2','share2',10*MB); details={}; out={}
    policy(t,'tenant',512*1024)
    def run(key,j,agent):
        try: out[key]=monitor(G.GT('bw'),j,agent,timeout=180)
        except Exception as e: out[key]=e
    try:
        a=threading.Thread(target=run,args=('one',j1,'bw')); b=threading.Thread(target=run,args=('two',j2,'bw2'))
        a.start(); b.start(); a.join(); b.join()
        assert not any(isinstance(v,Exception) for v in out.values()),str(out)
        r1,d1,s1,st1=out['one']; r2,d2,s2,st2=out['two']; details={'one':d1,'two':d2}
        assert d1['status']==d2['status']=='Succeeded',(d1['status'],d2['status'])
        start=max(next(x for x,y in s1 if y>0),next(x for x,y in s2 if y>0))+8
        end=min(next(x for x,y in s1 if y==max(z for _,z in s1)),next(x for x,y in s2 if y==max(z for _,z in s2)))-1
        sone=stats(s1,start,end); stwo=stats(s2,start,end)
        xs=range(math.ceil(start),math.floor(end)-4)
        combined=sorted((at(s1,x+5)-at(s1,x)+at(s2,x+5)-at(s2,x))/5 for x in xs)
        data={'runs':[r1,r2],'one':sone,'two':stwo,'combined_mean_Bps':sone['mean_Bps']+stwo['mean_Bps'],'combined_p95_5s_Bps':round(combined[math.ceil(.95*len(combined))-1]),'combined_max_5s_Bps':round(max(combined)),'limit_Bps':512*1024}
        save('shared',s1,{'other_samples':s2,**data})
        assert data['combined_mean_Bps']<=1.1*512*1024 and data['combined_p95_5s_Bps']<=1.15*512*1024,data
        return data
    finally:
        policy(t,'tenant',None)
        for key,j,agent in [('one',j1,'bw'),('two',j2,'bw2')]:
            cleanup(t,j,details.get(key) or (out[key][1] if isinstance(out.get(key),tuple) else None))
            shutil.rmtree(ROOT(agent)/'files'/('share1' if key=='one' else 'share2'),ignore_errors=True)

def validation():
    t=G.GT('bw'); day=wd(dt.datetime.now(dt.timezone.utc)); w=window(day,'10:00:00','11:00:00',16000)
    cases={'overlap':[w,window(day,'10:30:00','11:30:00',16000)],'equal':[window(day,'10:00:00','10:00:00',16000)],'low':[window(day,'10:00:00','11:00:00',15999)],'wrap_overlap':[window(6,'23:00:00','01:00:00',16000),window(0,'00:30:00','02:00:00',16000)]}
    results={}
    for name,ws in cases.items():
        r=t.raw('POST','/bandwidth-profiles',{'name':'gap-sp-invalid-'+name,'timeZoneId':'UTC','windows':ws}); results[name]=r.status_code
        if r.ok: t.raw('DELETE','/bandwidth-profiles/'+r.json()['id'])
    r=t.raw('POST','/bandwidth-profiles',{'name':'gap-sp-invalid-zone','timeZoneId':'No/Such_Zone','windows':[w]}); results['zone']=r.status_code
    if r.ok: t.raw('DELETE','/bandwidth-profiles/'+r.json()['id'])
    r=t.raw('POST','/bandwidth-profiles',{'name':'gap-sp-zero','timeZoneId':'UTC','windows':[window(day,'10:00:00','11:00:00',0)]}); results['zero']=r.status_code
    if r.ok: t.call('DELETE','/bandwidth-profiles/'+r.json()['id'])
    assert all(results[k]==400 for k in cases) and results['zone']==400 and results['zero']==200,results
    return results

def precedence():
    t=G.GT('bw'); j=job(t,'bw','precedence',8*MB); ps=[]; ds=[]; readings={}
    a='agents/'+t.agents()['gap-sp-bw']['agentId']; js='jobs/'+j['id']
    day=wd(dt.datetime.now(dt.timezone.utc))
    try:
        for name,rate in [('tenant',128*1024),('agent',256*1024),('job',MB)]:
            p=profile(t,name,'UTC',[window(day,'00:00:00','23:59:59',rate)]); ps.append(p)
            assign(t,{'tenant':'tenant','agent':a,'job':js}[name],p)
        for name,rate in [('job',MB),('agent',256*1024),('tenant',128*1024)]:
            rid,d,rows,stages=monitor(t,j,'bw'); ds.append(d); s=stats(rows); s.update(run=rid,status=d['status'])
            save('precedence-'+name,rows,s); assert d['status']=='Succeeded',d['status']; readings[name]=cap_assert(s,rate)
            if name=='job': assert s['p95_5s_Bps'] is not None and s['p95_5s_Bps'] > 1.15*256*1024, s
            if name=='agent': assert s['p95_5s_Bps'] is not None and s['p95_5s_Bps'] > 1.15*128*1024, s
            if name=='job': assign(t,js,None)
            elif name=='agent': assign(t,a,None)
        t.call('DELETE','/bandwidth-profiles/'+ps[0]); ps[0]=None
        settings=t.call('GET','/upload-bandwidth'); assert settings['tenant']['profileId'] is None,settings
        readings['assignment_after_delete']=settings['tenant']
        return readings
    finally:
        assign(t,'tenant',None); assign(t,a,None); assign(t,js,None)
        for p in ps:
            if p: t.raw('DELETE','/bandwidth-profiles/'+p)
        for d in ds: cleanup(t,j,d)
        cleanup(t,j); shutil.rmtree(ROOT('bw')/'files'/'precedence',ignore_errors=True)

def static_profile(outside=False):
    t=G.GT('bw'); label='static-outside' if outside else 'static-profile'; j=job(t,'bw',label,24*MB); p=None; d=None
    day=wd(dt.datetime.now(dt.timezone.utc)); day=(day+1)%7 if outside else day; js='jobs/'+j['id']
    try:
        policy(t,'tenant',512*1024)
        p=profile(t,label,'UTC',[window(day,'00:00:00','23:59:59',4*MB)]); assign(t,js,p)
        rid,d,rows,stages=monitor(t,j,'bw'); s=stats(rows); s.update(run=rid,status=d['status'],tenant_static_Bps=512*1024,profile_Bps=None if outside else 4*MB)
        save(label,rows,s); assert d['status']=='Succeeded',d['status']
        assert s['mean_Bps'] <= 1.12*512*1024, 'static tenant ceiling bypassed: '+json.dumps(s)
        return s
    finally:
        assign(t,js,None); policy(t,'tenant',None)
        if p: t.raw('DELETE','/bandwidth-profiles/'+p)
        cleanup(t,j,d); shutil.rmtree(ROOT('bw')/'files'/label,ignore_errors=True)

def run_main():
    cases=[('POL-07/job',lambda:static_case('job',[('jobs/{job}',2*MB)],2*MB,24*MB)),
           ('POL-07/agent',lambda:static_case('agent',[('agents/{agent}',MB)],MB,16*MB)),
           ('POL-07/tenant',lambda:static_case('tenant',[('tenant',MB)],MB,16*MB)),
           ('POL-07/job-agent-min',lambda:static_case('min',[('jobs/{job}',2*MB),('agents/{agent}',MB)],MB,16*MB)),
           ('POL-07/tenant-shared',shared),('POL-07/change-remove',static_change),
           ('POL-08/validation',validation),('POL-08/precedence-delete',precedence),
           ('POL-08/static-with-active-profile',lambda:static_profile(False)),('POL-08/static-with-outside-profile',lambda:static_profile(True))]
    for name,fn in cases: lib.scenario('gap/'+name)(fn)()
    t=G.GT('bw'); settings=t.call('GET','/upload-bandwidth'); profiles=t.call('GET','/bandwidth-profiles')
    lib.scenario('gap/POL-07/cleanup')(lambda: ({'settings':settings,'profiles':profiles} if settings['tenant']=={'bytesPerSecond':None,'profileId':None} and not settings['agents'] and not settings['jobs'] and not profiles else (_ for _ in ()).throw(AssertionError(str((settings,profiles))))) )()

if __name__=='__main__' and len(sys.argv)>1 and sys.argv[1]=='main': run_main()

def clockstr(ts): return dt.datetime.fromtimestamp(ts,dt.timezone.utc).strftime('%H:%M:%S')

def boundary():
    t=G.GT('bwp'); j=job(t,'bwp','boundary',65*MB); p=None; d=None
    day=wd(dt.datetime.now(dt.timezone.utc)); a=math.ceil(time.time())+65; b=a+30
    try:
        p=profile(t,'boundary','UTC',[window(day,'00:00:00',clockstr(a),256*1024),window(day,clockstr(a),clockstr(b),MB),window(day,clockstr(b),'23:59:59',512*1024)])
        assign(t,'jobs/'+j['id'],p)
        rid,d,rows,stages=monitor(t,j,'bwp',timeout=240)
        data={'run':rid,'status':d['status'],'start_boundary_utc':dt.datetime.fromtimestamp(a,dt.timezone.utc).isoformat(),'end_boundary_utc':dt.datetime.fromtimestamp(b,dt.timezone.utc).isoformat(),
              'before':stats(rows,a-30,a-5),'inside':stats(rows,a+4,b-4),'after':stats(rows,b+4,b+24),'stages':stages}
        save('boundary',rows,data); assert d['status']=='Succeeded',d['status']
        for name,rate in [('before',256*1024),('inside',MB),('after',512*1024)]: cap_assert(data[name],rate)
        return data
    finally:
        assign(t,'jobs/'+j['id'],None)
        if p: t.raw('DELETE','/bandwidth-profiles/'+p)
        cleanup(t,j,d); shutil.rmtree(ROOT('bwp')/'files'/'boundary',ignore_errors=True)

def zero_pause():
    import hashlib
    t=G.GT('bwp'); j=job(t,'bwp','zero',48*MB); p=None; d=None; rid=None; rid2=None
    day=wd(dt.datetime.now(dt.timezone.utc)); a=math.ceil(time.time())+25; b=a+65
    try:
        p=profile(t,'zero','UTC',[window(day,'00:00:00',clockstr(a),MB),window(day,clockstr(a),clockstr(b),0),window(day,clockstr(b),'23:59:59',MB)])
        assign(t,'jobs/'+j['id'],p); rid=G.start_run(t,j)
        s=Sampler(ROOT('bwp')/'local-store'/t.id.replace('-','')/j['id'].replace('-','')).start()
        transitions=[]
        try:
            d=t.wait('/backup-runs/'+rid,timeout=100,interval=1)
            transitions.append(('first_pause',time.time(),d['status'],d['attempts'][-1].get('reasonCode'),d.get('reasonCode')))
            assert d['status']=='Paused',transitions
            # Observed once: the destination grew by one 64 KiB step right after the status turned Paused, so the baseline is taken 3 s later.
            pause_start=time.time(); time.sleep(3); size0=s.rows[-1][1]; time.sleep(22); size1=s.rows[-1][1]
            d=t.call('GET','/backup-runs/'+rid)
            assert d['status']=='Paused' and size1==size0, {'status':d['status'],'sizes':[size0,size1]}
            r=t.raw('POST','/backup-runs/'+rid+'/resume',{}); transitions.append(('resume_in_window',time.time(),r.status_code))
            if r.ok:
                time.sleep(5)
                d=t.wait('/backup-runs/'+rid,timeout=25,interval=1)
                transitions.append(('after_inside_resume',time.time(),d['status'],d['attempts'][-1].get('reasonCode'),d.get('reasonCode')))
                assert d['status']=='Paused',transitions
            job2=job(t,'bwp','zero-start',1*MB); p2=None
            try:
                assign(t,'jobs/'+job2['id'],p); rid2=G.start_run(t,job2)
                d2=t.wait('/backup-runs/'+rid2,timeout=25,interval=1)
                transitions.append(('started_inside_zero',time.time(),d2['status'],d2['attempts'][-1].get('reasonCode'),d2.get('reasonCode')))
                assert d2['status']=='Paused',transitions
                t.raw('POST','/backup-runs/'+rid2+'/cancel',{})
                d2=t.wait('/backup-runs/'+rid2,timeout=30,interval=1,stop_on=lib.TERMINAL)
            finally:
                cleanup(t,job2,locals().get('d2')); shutil.rmtree(ROOT('bwp')/'files'/'zero-start',ignore_errors=True)
            time.sleep(max(0,b+8-time.time()))
            d=t.call('GET','/backup-runs/'+rid); transitions.append(('after_window',time.time(),d['status'],d['attempts'][-1].get('reasonCode'),d.get('reasonCode')))
            assert d['status']=='Paused',transitions
            r=t.raw('POST','/backup-runs/'+rid+'/resume',{}); transitions.append(('resume_after_window',time.time(),r.status_code)); assert r.ok,r.status_code
            d=t.wait('/backup-runs/'+rid,timeout=180,interval=1); assert d['status']=='Succeeded',d['status']
        finally: s.stop()
        art=G.arts(d)[0]
        req={'agentId':t.agents()['gap-sp-bwp']['agentId'],'target':{'host':'localhost','port':None,'integratedSecurity':True,'databaseCredentialReference':None,'encrypt':False,'trustServerCertificate':False},'databaseName':'gapfiles','mode':'NewDatabase','options':{'files':{'relativeDirectory':'gap-bw-restore'}}}
        restore=t.call('POST','/backup-artifacts/'+art['id']+'/restore',req)
        restored=t.wait('/restores/'+restore.get('id',restore.get('restoreId')),timeout=180)
        assert restored['status']=='Succeeded',restored['status']
        source=ROOT('bwp')/'files'/'zero'/'source.bin'; matches=list((ROOT('bwp')/'files'/'gap-bw-restore').rglob('source.bin'))
        assert len(matches)==1,str(matches)
        digest=lambda path:hashlib.sha256(path.read_bytes()).hexdigest()
        sha=digest(source); got=digest(matches[0]); assert sha==got,(sha,got)
        data={'run':rid,'transitions':transitions,'pause_seconds':round(time.time()-pause_start,1),'paused_22s_start_bytes':size0,'paused_22s_end_bytes':size1,'sha256_source':sha,'sha256_restored':got,'restore_status':restored['status'],'window_start':a,'window_end':b}
        save('zero',s.rows,data); return data
    finally:
        if rid and (not d or d['status'] not in lib.TERMINAL): t.raw('POST','/backup-runs/'+rid+'/cancel',{})
        assign(t,'jobs/'+j['id'],None)
        if p: t.raw('DELETE','/bandwidth-profiles/'+p)
        cleanup(t,j,d); shutil.rmtree(ROOT('bwp')/'files'/'zero',ignore_errors=True); shutil.rmtree(ROOT('bwp')/'files'/'gap-bw-restore',ignore_errors=True)

def run_time():
    for name,fn in [('gap/POL-09/boundaries',boundary),('gap/POL-09/zero-pause-resume',zero_pause)]:
        lib.scenario(name)(fn)()
    t=G.GT('bwp'); settings=t.call('GET','/upload-bandwidth'); profiles=t.call('GET','/bandwidth-profiles')
    lib.scenario('gap/POL-09/cleanup')(lambda: ({'settings':settings,'profiles':profiles} if settings['tenant']=={'bytesPerSecond':None,'profileId':None} and not settings['agents'] and not settings['jobs'] and not profiles else (_ for _ in ()).throw(AssertionError(str((settings,profiles))))) )()

if __name__=='__main__' and len(sys.argv)>1 and sys.argv[1]=='time': run_time()

class RemoteSampler(Sampler):
    def __init__(self, jobid):
        super().__init__(None); self.jobid=jobid.replace('-',''); self.errors=[]
    def run(self):
        import subprocess
        cmd="sudo -n find /home/e2e-sftp/backups -type f -path '*"+self.jobid+"*' -printf '%s\\n'"
        while not self.stop_event.is_set():
            r=subprocess.run(['ssh','-o','BatchMode=yes','virtual-fedora',cmd],capture_output=True,text=True,timeout=15)
            if r.returncode: self.errors.append(r.stderr[:200]); break
            self.rows.append((time.time(),sum(int(x) for x in r.stdout.splitlines() if x.isdigit())))
            self.stop_event.wait(.5)

def relay_sftp():
    import api
    t=G.GT('bwr'); base=lib.Tenant('a')
    cfg=next(c for c in base.call('GET','/storage-connections')['items'] if c['id']==lib.ST['connections']['sftp'])['configuration']
    conn=t.call('POST','/storage-connections',{'providerType':'sftp','name':'gap-sp-bw-relay-'+uuid.uuid4().hex[:4],'configuration':cfg,'secret':{'password':lib.ST['fedora']['sftp_password']}})
    j=None; d=None; rid=None
    try:
        G.put_file('bwr','relay/source.bin',20*MB)
        j=G.new_job(t,t.agents()['gap-sp-bwr'],'bw-relay',G.gcfg([G.gdest('sftp',conn=conn['id'],loc='Backend')],paths=['relay']))
        policy(t,'jobs/'+j['id'],MB); rid=G.start_run(t,j); sample=RemoteSampler(j['id']).start(); stages=[]
        try:
            deadline=time.time()+240
            while time.time()<deadline:
                d=t.call('GET','/backup-runs/'+rid); a=d['attempts'][-1]
                stages.append((time.time(),d['status'],a.get('progressStage'),a.get('progressAt')))
                if d['status'] in lib.TERMINAL or d['status']=='Paused': break
                time.sleep(1)
            else: raise TimeoutError(rid)
        finally: sample.stop()
        data={'run':rid,'status':d['status'],'destination':'SFTP Backend relay','stages':stages,'sampler_errors':sample.errors}
        if any(x[1] for x in sample.rows): data['transfer']=stats(sample.rows)
        save('relay-sftp',sample.rows,data)
        assert d['status']=='Succeeded',json.dumps(data)
        assert 'transfer' in data and not sample.errors, data
        cap_assert(data['transfer'],MB)
        return data
    finally:
        if rid and (not d or d['status'] not in lib.TERMINAL): t.raw('POST','/backup-runs/'+rid+'/cancel',{})
        if j: cleanup(t,j,d)
        shutil.rmtree(ROOT('bwr')/'files'/'relay',ignore_errors=True)
        r=t.raw('DELETE','/storage-connections/'+conn['id'])
        print('relay_connection_delete_status',r.status_code,flush=True)

if __name__=='__main__' and len(sys.argv)>1 and sys.argv[1]=='relay':
    lib.scenario('gap/POL-07/backend-relay-sftp')(relay_sftp)()

if __name__=='__main__' and len(sys.argv)>1 and sys.argv[1]=='retry':
    for name,fn in [('gap/POL-07/job-remeasured',lambda:static_case('job2',[('jobs/{job}',2*MB)],2*MB,24*MB)),
                    ('gap/POL-07/tenant-remeasured',lambda:static_case('tenant2',[('tenant',MB)],MB,16*MB)),
                    ('gap/POL-07/job-agent-remeasured',lambda:static_case('min2',[('jobs/{job}',2*MB),('agents/{agent}',MB)],MB,16*MB)),
                    ('gap/POL-07/change-remove-remeasured',static_change),
                    ('gap/POL-08/precedence-delete-remeasured',precedence),
                    ('gap/POL-08/static-with-outside-profile-remeasured',lambda:static_profile(True))]:
        lib.scenario(name)(fn)()
    t=G.GT('bw'); settings=t.call('GET','/upload-bandwidth'); profiles=t.call('GET','/bandwidth-profiles')
    lib.scenario('gap/POL-07/cleanup-remeasured')(lambda: ({'settings':settings,'profiles':profiles} if settings['tenant']=={'bytesPerSecond':None,'profileId':None} and not settings['agents'] and not settings['jobs'] and not profiles else (_ for _ in ()).throw(AssertionError(str((settings,profiles))))) )()

def dst_component():
    import subprocess
    r=subprocess.run(['dotnet','fsi','scripts/e2e/gap_bw_dst.fsx'],capture_output=True,text=True,timeout=90)
    assert r.returncode==0, r.stderr[:1000]
    lines=r.stdout.splitlines()
    checks=[('Berlin spring skipped','limit=<null>'),('Berlin spring end skipped','nextTransition=3/28/2027 1:00:00'),
            ('Berlin fall repeated','nextTransition=10/31/2027 1:15:00'),('Berlin fall midnight crossing','nextTransition=10/31/2027 2:00:00'),
            ('New York fall repeated','nextTransition=11/7/2027 6:15:00'),('Lord Howe fall half-hour repeated','nextTransition=4/3/2027 3:10:00')]
    assert all(any(a in line and b in line for line in lines) for a,b in checks),lines
    return {'layer':'production domain DLL, offline component (NOT live DST)','samples':lines}

if __name__=='__main__' and len(sys.argv)>1 and sys.argv[1]=='dst':
    lib.scenario('gap/POL-09/dst-component')(dst_component)()

def relay_s3():
    import api, subprocess
    t=G.GT('bwr'); base=lib.Tenant('a')
    cfg=next(c for c in base.call('GET','/storage-connections')['items'] if c['id']==lib.ST['connections']['s3'])['configuration']
    conn=t.call('POST','/storage-connections',{'providerType':'s3','name':'gap-sp-bw-s3-'+uuid.uuid4().hex[:4],'configuration':cfg,'secret':{'accessKey':api.env['E2E_MINIO_USER'],'secretKey':api.env['E2E_MINIO_PASSWORD']}})
    j=None; d=None; rid=None
    try:
        G.put_file('bwr','relay-s3/source.bin',20*MB)
        j=G.new_job(t,t.agents()['gap-sp-bwr'],'bw-s3',G.gcfg([G.gdest('s3',conn=conn['id'],loc='Backend')],paths=['relay-s3']))
        policy(t,'jobs/'+j['id'],MB); rid=G.start_run(t,j); stages=[]
        deadline=time.time()+240
        while time.time()<deadline:
            d=t.call('GET','/backup-runs/'+rid); a=d['attempts'][-1]
            stages.append((time.time(),d['status'],a.get('progressStage'),a.get('progressAt')))
            if d['status'] in lib.TERMINAL or d['status']=='Paused': break
            time.sleep(.5)
        else: raise TimeoutError(rid)
        assert d['status']=='Succeeded',(d['status'],d.get('reason'))
        art=G.arts(d)[0]
        started=next(x[0] for x in stages if x[2]=='Uploading')
        ended=next(x[0] for x in stages if x[0]>started and x[2]!='Uploading')
        cmd=['docker','exec','bpe2e-minio','find','/bitnami/minio/data/bpe2e','-type','f','-path','*'+j['id'].replace('-','')+'*','-printf','%s\\n']
        result=subprocess.run(cmd,capture_output=True,text=True,timeout=30)
        physical=sum(int(x) for x in result.stdout.splitlines() if x.isdigit())
        data={'run':rid,'status':d['status'],'destination':'S3 Backend relay','artifact_bytes':art['sizeBytes'],'minio_physical_bytes':physical,'minio_find_status':result.returncode,'upload_stage_seconds':round(ended-started,2),'estimated_Bps':round(art['sizeBytes']/(ended-started)),'limit_Bps':MB,'stages':stages}
        assert result.returncode==0 and physical>0,data
        assert data['estimated_Bps']<=1.2*MB,data
        return data
    finally:
        if rid and (not d or d['status'] not in lib.TERMINAL): t.raw('POST','/backup-runs/'+rid+'/cancel',{})
        if j: cleanup(t,j,d)
        shutil.rmtree(ROOT('bwr')/'files'/'relay-s3',ignore_errors=True)
        r=t.raw('DELETE','/storage-connections/'+conn['id'])
        print('s3_connection_delete_status',r.status_code,flush=True)

if __name__=='__main__' and len(sys.argv)>1 and sys.argv[1]=='s3':
    lib.scenario('gap/POL-07/backend-relay-s3')(relay_s3)()

def pause_reason_db():
    import subprocess
    t=G.GT('bwp'); j=job(t,'bwp','reason',2*MB); p=None; rid=None; d=None
    day=wd(dt.datetime.now(dt.timezone.utc))
    try:
        p=profile(t,'reason','UTC',[window(day,'00:00:00','23:59:59',0)]); assign(t,'jobs/'+j['id'],p)
        rid=G.start_run(t,j); d=t.wait('/backup-runs/'+rid,timeout=70,interval=1)
        assert d['status']=='Paused',d['status']
        safe=str(uuid.UUID(rid))
        sql=f"""SELECT "PauseReason" FROM backup_run_attempts WHERE "RunId"='{safe}' ORDER BY "Number" DESC LIMIT 1"""
        q=subprocess.run(['docker','exec','-e','QUERY='+sql,'77d3b642919c','sh','-c','psql -U "$POSTGRES_USER" -d "$POSTGRES_DB" -Atc "$QUERY"'],text=True,capture_output=True,timeout=30)
        assert q.returncode==0,q.stderr[:500]
        assert q.stdout.strip()=='2',q.stdout
        return {'run':rid,'status':d['status'],'database_PauseReason':2,'meaning':'TimeRule (TransferPauseReason enum)'}
    finally:
        if rid: t.raw('POST','/backup-runs/'+rid+'/cancel',{})
        assign(t,'jobs/'+j['id'],None)
        if p: t.raw('DELETE','/bandwidth-profiles/'+p)
        cleanup(t,j,d); shutil.rmtree(ROOT('bwp')/'files'/'reason',ignore_errors=True)

def remove_cap():
    t=G.GT('bw'); j=job(t,'bw','remove',24*MB); d=None; change=[]
    try:
        policy(t,'tenant',256*1024)
        def callback(t,rid,d,s):
            if s.rows and s.rows[-1][1]>=3*MB and not change:
                policy(t,'tenant',None); change.append(time.time())
        rid,d,rows,stages=monitor(t,j,'bw',timeout=180,callback=callback)
        assert d['status']=='Succeeded' and change,(d['status'],change)
        before=stats(rows,change[0]-6,change[0])
        detection=next((x for x in range(math.ceil(change[0]),math.floor(rows[-1][0])-1) if at(rows,x+1)-at(rows,x)>512*1024),None)
        data={'run':rid,'before':before,'removed_at':change[0],'detected_lag_s':round(detection-change[0],2) if detection else None,'status':d['status']}
        save('remove-cap',rows,data)
        assert detection is not None and detection-change[0]<8,data
        return data
    finally: policy(t,'tenant',None); cleanup(t,j,d); shutil.rmtree(ROOT('bw')/'files'/'remove',ignore_errors=True)

if __name__=='__main__' and len(sys.argv)>1 and sys.argv[1]=='extra':
    lib.scenario('gap/POL-09/pause-reason-db')(pause_reason_db)()
    lib.scenario('gap/POL-07/removal-256k')(remove_cap)()

def evidence_boundary():
    r=next(json.loads(x) for x in OUT.open() if json.loads(x).get('case')=='boundary')
    rows=r['samples']; a=dt.datetime.fromisoformat(r['start_boundary_utc']).timestamp(); b=dt.datetime.fromisoformat(r['end_boundary_utc']).timestamp()
    data={k:r[k] for k in ('run','start_boundary_utc','end_boundary_utc','before','inside','after')}
    for label,rate in [('before',256*1024),('inside',MB),('after',512*1024)]: cap_assert(data[label],rate)
    data['start_before_2s_Bps']=round((at(rows,a)-at(rows,a-2))/2)
    data['start_after_2s_Bps']=round((at(rows,a+2)-at(rows,a))/2)
    data['end_before_2s_Bps']=round((at(rows,b)-at(rows,b-2))/2)
    data['end_after_2s_Bps']=round((at(rows,b+4)-at(rows,b+2))/2)
    assert data['start_after_2s_Bps']>1.5*data['start_before_2s_Bps'] and data['end_before_2s_Bps']>1.5*data['end_after_2s_Bps'],data
    data['precision']='0.5s file samples; reported transition rates aggregate 2s, not exact instant'
    return data

def evidence_zero():
    r=next(json.loads(x) for x in OUT.open() if json.loads(x).get('case')=='zero')
    times={x[0]:x[1] for x in r['transitions']}; s=[n for x,n in r['samples'] if times['resume_in_window']<=x<=times['after_inside_resume']]
    assert len(s)>20 and max(s)==min(s),s
    return {'run':r['run'],'resume_inside_window_status':200,'returned_to_paused':True,'resume_observation_seconds':round(times['after_inside_resume']-times['resume_in_window'],2),'min_bytes':min(s),'max_bytes':max(s),'samples':len(s)}

if __name__=='__main__' and len(sys.argv)>1 and sys.argv[1]=='evidence':
    lib.scenario('gap/POL-09/boundaries-analyzed')(evidence_boundary)()
    lib.scenario('gap/POL-09/zero-resume-no-growth')(evidence_zero)()

if __name__=='__main__' and len(sys.argv)>1 and sys.argv[1]=='precedence':
    lib.scenario('gap/POL-08/precedence-distinct-rates')(precedence)()

def midnight_evidence(agent):
    r=next(json.loads(x) for x in OUT.open() if json.loads(x).get('case')=='midnight-'+agent)
    data={k:r[k] for k in ('zone','run','status','midnight_utc','before','pre_midnight','post_midnight','after')}
    assert data['status']=='Succeeded',data
    for label,rate in [('before',256*1024),('pre_midnight',MB),('post_midnight',MB),('after',512*1024)]: cap_assert(data[label],rate)
    midnight=dt.datetime.fromisoformat(data['midnight_utc']).timestamp()
    data['across_midnight_10s']=stats(r['samples'],midnight-5,midnight+5)
    assert data['pre_midnight']['mean_Bps'] > data['before']['mean_Bps']*1.5 and data['post_midnight']['mean_Bps'] > data['before']['mean_Bps']*1.5,data
    assert data['across_midnight_10s']['mean_Bps']>data['before']['mean_Bps'],data
    return data

def change_evidence():
    r=next(json.loads(x) for x in OUT.open() if json.loads(x).get('case')=='change')
    rows=r['samples']; raise_at=r['changes'][0][1]; remove_at=r['changes'][1][1]
    later=[x for x in range(math.ceil(remove_at),math.floor(rows[-1][0])) if at(rows,x+1)-at(rows,x)>4*MB]
    raised=[x for x in range(math.ceil(raise_at),math.floor(remove_at)) if at(rows,x+1)-at(rows,x)>1.3*MB]
    assert later and raised, {'remove':later,'raise':raised}
    return {'run':r['run'],'before':r['before'],'after_raise':r['after'],'raise_detection_lag_s':round(raised[0]-raise_at,2),'remove_detection_lag_s':round(later[0]-remove_at,2)}

if __name__=='__main__' and len(sys.argv)>1 and sys.argv[1]=='analyze':
    lib.scenario('gap/POL-07/change-remove-samples')(change_evidence)()
    lib.scenario('gap/POL-09/midnight-analyzed-'+sys.argv[2])(lambda:midnight_evidence(sys.argv[2]))()

def perf_summary():
    rows=[json.loads(x) for x in OUT.open()]
    results={}
    for label,limit in [('job2',2*MB),('agent',MB),('tenant2',MB),('min2',MB)]:
        r=next(x for x in reversed(rows) if x['case']==label)
        selected={k:r[k] for k in ('seconds','bytes','mean_Bps','stdev_1s_Bps','p95_5s_Bps','max_5s_Bps')}
        results[label]=cap_assert(selected,limit)
    relay=next(x for x in reversed(rows) if x['case']=='relay-sftp')
    results['relay_sftp']=relay['transfer']
    zero=evidence_zero(); results['zero_resume']=zero
    assert zero['min_bytes']==zero['max_bytes'],zero
    return {'sampling':'destination size every ~0.5s, mean over first growth to last byte; p95/max sliding 5s, stdev 1s windows','rates':results}

if __name__=='__main__' and len(sys.argv)>1 and sys.argv[1]=='perf':
    lib.scenario('gap/PERF-05/cap-and-pause')(perf_summary)()
