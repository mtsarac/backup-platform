"""Precheck MSSQL restore using a disposable login lacking CREATE DATABASE permission."""
import secrets, sys
import lib, flows
from gap_windows import MS, sql

@lib.scenario('gap/RST-11/windows-mssql-permission')
def run():
    t=lib.Tenant('a'); ag=t.agents()['win11-e2e']; user='gap_windows_restricted'; password='Aa1!' + secrets.token_hex(12)
    aid=sys.argv[1]
    assert sql(f"select count(*) from sys.server_principals where name='{user}'").strip()=='0'
    try:
        sql(f"create login [{user}] with password='{password}', check_policy=off")
        ref=flows.login_ref(t,ag,user,password)
        req={'agentId':ag['agentId'],'target':{'host':'127.0.0.1','port':MS['port'],'databaseCredentialReference':ref,'encrypt':True,'trustServerCertificate':True},'databaseName':'gap_windows_perm_missing','mode':'NewDatabase'}
        r=t.call('POST',f'/backup-artifacts/{aid}/restore-precheck',req)
        d=t.wait(f"/restores/{r.get('id',r.get('restoreId'))}",timeout=300)
        assert d['status']=='Failed' and d['targetState']=='NotModified',d
        assert d['failureCode'] in ('PermissionDenied','PreCheckFailed'),d
        checks = d['result']['preCheck']
        assert any(c['checkId']=='permission.restore' and c['status']=='Failed' and c['reasonCode']=='restore-permission-denied' for c in checks), checks
        return {'restoreId':d['id'],'failureCode':d['failureCode'],'reason':d['reason'],'checks':checks,'targetState':d['targetState']}
    finally:
        sql(f"if exists(select 1 from sys.server_principals where name='{user}') drop login [{user}]")

if __name__=='__main__': run()
