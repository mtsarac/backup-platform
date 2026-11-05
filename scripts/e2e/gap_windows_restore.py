"""Windows MSSQL live restore precheck cases, with only a disposable target database."""
import json
import lib, flows
from gap_windows import MS, sql

@lib.scenario('gap/RST-11/windows-mssql-precheck')
def run():
    t=lib.Tenant('a'); ag=t.agents()['win11-e2e']; cfg=flows.mssql_cfg(t,ag)
    job,rid,art,detail=flows.run_backup(t,ag,'gap-windows-rst11',flows.db_job_config('mssql',cfg,[flows.local_dest()]),timeout=600)
    db='gap_windows_rst11'; missing='gap_windows_rst11_missing'; aid=art['id']
    target={'host':'127.0.0.1','port':MS['port'],'databaseCredentialReference':cfg['ref'],'encrypt':True,'trustServerCertificate':True}
    try:
        assert sql(f"select count(*) from sys.databases where name in ('{db}','{missing}')").strip() == '0'
        sql(f'create database [{db}]')
        reports={}
        for name, mode in [(missing,'NewDatabase'),(db,'NewDatabase'),(db,'ReplaceExistingDatabase'),(missing,'ReplaceExistingDatabase')]:
            req={'agentId':ag['agentId'],'target':target,'databaseName':name,'mode':mode}
            if mode=='ReplaceExistingDatabase': req['confirmOverwrite']=name
            r=t.raw('POST',f'/backup-artifacts/{aid}/restore-precheck',req)
            if r.status_code not in (200,201,202): reports[name+':'+mode]={'http':r.status_code,'body':r.text[:500]}; continue
            x=r.json(); op=t.wait(f"/restores/{x.get('id',x.get('restoreId'))}",timeout=300)
            reports[name+':'+mode]=op
        print('gap-windows-precheck-evidence',json.dumps({'backupRun':rid,'artifact':aid,'reports':reports},default=str),flush=True)
        assert reports[missing+':NewDatabase']['status']=='Succeeded'
        assert reports[db+':NewDatabase']['failureCode']=='TargetDatabaseExists'
        assert reports[db+':ReplaceExistingDatabase']['status']=='Succeeded'
        assert reports[missing+':ReplaceExistingDatabase']['failureCode']=='TargetDatabaseNotFound'
        assert all(r['targetState']=='NotModified' for r in reports.values())
        return {'backupRun':rid,'artifact':aid,'reports':reports}
    finally:
        sql(f"if db_id('{db}') is not null drop database [{db}]")

if __name__=='__main__': run()
