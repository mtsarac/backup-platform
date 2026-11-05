"""Verify Fedora local default and vol2 physical placement and replica hashes."""
import hashlib
import lib, flows

@lib.scenario('gap/STO-01/local-default-and-volume2-physical-scope')
def main():
    t=lib.Tenant('a'); a=t.agents()['fedora-e2e']; results=[]
    for volume, root in [('default','/var/lib/backup-agent/local-store'),('vol2','/srv/e2e/volume2')]:
        job=None; art=None; conn=None
        try:
            if volume == 'vol2':
                conn=t.call('POST','/storage-connections',{'providerType':'local','name':'gap-sp-volume2','configuration':{'volume':'vol2'}})
            destination=flows.local_dest()
            if conn: destination['storageConnectionId']=conn['id']
            cfg=flows.file_job_config('e2efiles',[destination],paths=['docs'])
            job,rid,art,d=flows.run_backup(t,a,'gap-sp-volume-'+volume,cfg)
            detail=t.call('GET',f'/backup-artifacts/{art["id"]}'); rep=detail['replicas'][0]
            assert rep['verificationState']=='Verified' and rep['storageProvider']=='local', rep
            # Restrict the search to the uniquely generated artifact filename within the expected root.
            name=rep['key'].rsplit('/',1)[-1]
            rc,out=lib.fedora(f"sudo find '{root}' -type f -name '{name}' -exec sha256sum {{}} \\;")
            lines=[x for x in out.splitlines() if len(x.split())>=2 and len(x.split()[0])==64]
            assert len(lines)==1, f'expected exactly one artifact at {root}: {out}'
            actual,path=lines[0].split(maxsplit=1)
            assert actual==art['artifactChecksum'], f'{volume} hash mismatch'
            assert path.startswith(root+'/') and rep['storageTarget'] in (volume,'local' if volume=='default' else volume), rep
            results.append({'volume':volume,'runId':rid,'artifactId':art['id'],'replicaTarget':rep['storageTarget'],'sha256':actual,'path':path})
        finally:
            if art: t.raw('DELETE',f'/backup-artifacts/{art["id"]}')
            if job: t.raw('POST',f'/backup-jobs/{job["id"]}/disable',{})
            if conn: t.raw('DELETE',f'/storage-connections/{conn["id"]}')
    return {'copies':results}

if __name__=='__main__': main()
