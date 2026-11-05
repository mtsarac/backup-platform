"""Interrupt only the SQL BACKUP command for this scenario's throwaway Windows database."""
import json, time
import lib, flows
from gap_windows import sql, winx

@lib.scenario('gap/JOB-12/windows-sql-connection-cut')
def run():
    t=lib.Tenant('a'); ag=t.agents()['win11-e2e']; db='gap_windows_job12'
    assert sql(f"select count(*) from sys.databases where name='{db}'").strip()=='0'
    rid=None; killed=None
    try:
        sql(f'create database [{db}]')
        sql("create table dbo.payload (id int identity primary key, value varbinary(8000)); insert dbo.payload(value) select CRYPT_GEN_RANDOM(8000) from sys.all_objects a cross join sys.all_objects b order by a.object_id offset 0 rows fetch next 20000 rows only",db=db)
        cfg=flows.mssql_cfg(t,ag); cfg['database']=db
        job=t.call('POST','/backup-jobs',{'name':'gap-windows-job12','agentId':ag['agentId'],'overlapPolicy':'Skip','configuration':flows.db_job_config('mssql',cfg,[flows.local_dest()])})
        r=t.call('POST',f"/backup-jobs/{job['id']}/runs",{}); rid=r.get('id',r.get('run',{}).get('id'))
        end=time.time()+180
        while time.time()<end:
            q=sql(f"select session_id from sys.dm_exec_requests where command='BACKUP DATABASE' and database_id=db_id('{db}')")
            if q.strip().isdigit():
                killed=int(q.strip()); sql(f'kill {killed}'); break
            d=t.call('GET',f'/backup-runs/{rid}')
            if d['status'] in lib.TERMINAL: break
            time.sleep(.2)
        d=t.wait(f'/backup-runs/{rid}',timeout=600)
        attempts=d['attempts']; arts=[a for x in attempts for a in x['artifacts']]
        rc,stage=winx(r"Get-ChildItem C:\e2e\staging -File -Recurse | Select-Object -ExpandProperty FullName")
        assert killed is not None, f"no backup SQL session observed before terminal: run={rid}, status={d['status']}"
        assert attempts[0]['status']=='Failed' and attempts[0]['reasonCode']=='DatabaseUnavailable' and not attempts[0]['artifacts'], attempts
        if d['status']=='Succeeded':
            assert len(attempts)>1 and attempts[-1]['status']=='Succeeded' and attempts[-1]['trigger']=='AutomaticRetry' and all(a['verificationState']=='Verified' for art in attempts[-1]['artifacts'] for a in art['replicas']), attempts
        return {'runId':rid,'killedSession':killed,'status':d['status'],'attempts':attempts,'remainingStagingFiles':stage.splitlines()}
    finally:
        sql(f"if db_id('{db}') is not null begin alter database [{db}] set single_user with rollback immediate; drop database [{db}] end")

if __name__=='__main__': run()
