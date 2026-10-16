#!/usr/bin/env python3
"""Real end-to-end acceptance against the running dev stack (scripts/dev/start.sh), as the tenant Owner, through the HTTP API only.

  backup   MSSQL bp_google_drive_acceptance -> Agent Full Backup -> chosen storage connection -> SHA-256/replica/quota evidence
  download backend-authorized download of that artifact, SHA-256 compared to the artifact checksum
  restore  precheck + NewDatabase restore into <source>_restored (never an existing database), then MSSQL row check
  delete   delete request -> retention worker -> replica gone, quota back to its previous value
  retry    (google-drive) disable the connection during a ~120 MB upload, then re-enable and retry the failed run; run `delete` afterwards

--provider google-drive needs a tenant connection that finished OAuth (hasSecret=true); nothing is mocked or simulated here.
State between steps lives in $BP_DEV_HOME/acceptance-<provider>.json (0600, no secrets). Passwords come from the accounts file and are never printed.
"""
import argparse
import contextlib
import hashlib
import importlib.util
import json
import os
from pathlib import Path
import subprocess
import sys
import time
import uuid
import tempfile

HERE = Path(__file__).parent
spec = importlib.util.spec_from_file_location('bootstrap', HERE / 'bootstrap.py')
bootstrap = importlib.util.module_from_spec(spec)
spec.loader.exec_module(bootstrap)
shared, Api, FixtureFailure = bootstrap.shared, bootstrap.Api, bootstrap.FixtureFailure

HOME = Path(os.environ.get('BP_DEV_HOME', Path.home() / '.local/share/backup-platform-dev'))
ACCOUNTS = HERE.parent.parent / 'docs' / 'local-development-accounts.txt'
SERVER = 'https://localhost:7179'
SOURCE_DB = 'bp_google_drive_acceptance'
RESTORED_DB = SOURCE_DB + '_restored'
SQL_PORT = 14331


def sa_password():
    for line in (HOME / 'secrets.env').read_text().splitlines():
        if line.startswith('BP_DEV_MSSQL_SA_PASSWORD='):
            return line.split('=', 1)[1]
    raise FixtureFailure('The dev SQL Server password is not available.')


def store_login(api, tenant, agent_id, user, password):
    """Stores a database login for the agent (encrypted by the API) and returns the reference a job or restore target uses."""
    _, stored = api.call('POST', tenant + f'/agents/{agent_id}/database-logins', {'userName': user, 'password': password})
    return stored['reference']


def sql_login(api, tenant, agent_id):
    return store_login(api, tenant, agent_id, 'sa', sa_password())


def out(**values):
    print(json.dumps(values, default=str))


def session():
    accounts = bootstrap.read_accounts(ACCOUNTS)
    platform = Api(SERVER, str(HOME / 'cert.pem'))
    platform.login(accounts['platform_admin_login'], accounts['platform_admin_password'])
    _, tenants = platform.call('GET', '/api/platform/tenants?search=Local%20Development%20Tenant&pageSize=100')
    tenant_id = bootstrap.find_by(tenants['items'], 'name', bootstrap.TENANT_NAME)['tenantId']
    owner = Api(SERVER, str(HOME / 'cert.pem'))
    owner.login(accounts['tenant_owner_login'], accounts['tenant_owner_password'])
    return owner, f'/api/tenants/{tenant_id}'


def state_path(options):
    return getattr(options, 'smoke_state', None) or HOME / f'acceptance-{options.provider}.json'


def load(options):
    return shared.read_private(str(state_path(options)))


def save(options, document):
    shared.write_private(str(state_path(options)), document)


def wait(label, fetch, done, failed, timeout=240, interval=1.0):
    deadline = time.monotonic() + timeout
    while time.monotonic() < deadline:
        value = fetch()
        if done(value):
            return value
        if failed(value):
            raise FixtureFailure(f'{label} ended in a failed state: {json.dumps(value)[:400]}')
        time.sleep(interval)
    raise FixtureFailure(f'{label} timed out.')


def sql_verify(database):
    return subprocess.run([str(HERE / 'mssql-test-db.sh'), 'verify', database], capture_output=True, text=True, check=True).stdout.strip()


def quota(api, tenant):
    return api.call('GET', tenant + '/storage-quota')[1]


def pick_connection(api, tenant, provider):
    _, document = api.call('GET', tenant + '/storage-connections')
    connections = document['items'] if isinstance(document, dict) else document
    candidates = [c for c in connections if c['providerType'] == provider and c['effectiveEnabled']
                  and (not os.environ.get('ACCEPTANCE_CONNECTION_NAME') or c['name'] == os.environ['ACCEPTANCE_CONNECTION_NAME'])]
    if provider == 'google-drive':
        candidates = [c for c in candidates if c['hasSecret']]
        if not candidates:
            raise FixtureFailure('No enabled Google Drive connection with stored credentials: finish "Google Hesabini Bagla" (OAuth) in the UI first.')
    if not candidates:
        raise FixtureFailure(f'No enabled {provider} connection.')
    return candidates[0]


@contextlib.contextmanager
def unlimited_while_unsized():
    """A fresh job has no size history, which a hard quota refuses by design (quota_size_unknown); bootstrap it through the documented
    explicit unlimited override and always restore the plan's own limit afterwards."""
    accounts = bootstrap.read_accounts(ACCOUNTS)
    platform = Api(SERVER, str(HOME / 'cert.pem'))
    platform.login(accounts['platform_admin_login'], accounts['platform_admin_password'])
    _, tenants = platform.call('GET', '/api/platform/tenants?search=Local%20Development%20Tenant&pageSize=100')
    tenant_id = bootstrap.find_by(tenants['items'], 'name', bootstrap.TENANT_NAME)['tenantId']
    path = f'/api/platform/tenants/{tenant_id}/subscription/storage-limit-override'
    platform.call('PUT', path, {'kind': 'unlimited'})
    try:
        yield
    finally:
        platform.call('PUT', path, {'kind': 'none'})


def backup(options):
    with unlimited_while_unsized():
        backup_unbounded(options)


def create_job(api, tenant, connection, provider, database):
    _, document = api.call('GET', tenant + '/agents')
    agents = document if isinstance(document, list) else document['items']
    agent = next((a for a in agents if a['hostName'] == 'dev-linux-agent' and a['connectivity'] == 'online'), None)
    if agent is None:
        raise FixtureFailure('The dev-linux-agent is not online (scripts/dev/agent.sh start).')
    configuration = {'engine': 'mssql', 'host': '127.0.0.1', 'port': SQL_PORT, 'database': database, 'integratedSecurity': False,
                     'databaseCredentialReference': sql_login(api, tenant, agent['agentId']), 'trustServerCertificate': True, 'encrypt': True,
                     'stagingReference': 'default', 'storageTarget': 'default', 'storagePrefix': '',
                     'copyOnly': True, 'checksumPolicy': 'Required', 'compressionPolicy': 'Preferred', 'requiredVerifiedReplicaCount': 1}
    drive = provider == 'google-drive'
    configuration['destinations'] = [{'storageProvider': provider, 'storageConnectionId': connection['id'] if drive else None,
                                      'storageExecutionLocation': 'Backend' if drive else 'Agent', 'isPrimary': True}]
    _, job = api.call('POST', tenant + '/backup-jobs', {'name': f'Acceptance {provider} {time.strftime("%H%M%S")}',
                                                       'agentId': agent['agentId'], 'overlapPolicy': 'Skip', 'configuration': configuration})
    return agent, job


def backup_unbounded(options):
    api, tenant = session()
    connection = pick_connection(api, tenant, options.provider)
    before = quota(api, tenant)
    agent, job = create_job(api, tenant, connection, options.provider, SOURCE_DB)
    _, run = api.call('POST', tenant + f'/backup-jobs/{job["id"]}/runs', {}, headers={'Idempotency-Key': 'dev-acc-' + uuid.uuid4().hex}, expect=(200, 201, 202))
    run_id = run.get('id', run.get('run', {}).get('id'))
    detail = wait('Backup run', lambda: api.call('GET', tenant + f'/backup-runs/{run_id}')[1],
                  lambda d: d['status'] == 'Succeeded', lambda d: d['status'] in ('Failed', 'Cancelled', 'AgentLost', 'Skipped'))
    artifacts = [a for attempt in detail['attempts'] for a in attempt['artifacts']]
    if len(artifacts) != 1:
        raise FixtureFailure('The run did not publish exactly one artifact.')
    artifact = artifacts[0]
    _, full = api.call('GET', tenant + f'/backup-artifacts/{artifact["id"]}')
    after = quota(api, tenant)
    document = {'artifactId': artifact['id'], 'checksum': artifact['artifactChecksum'], 'sizeBytes': artifact['sizeBytes'], 'jobId': job['id'],
                'runId': run_id, 'agentId': agent['agentId'], 'connectionId': connection['id'], 'usedBefore': before['usedBytes']}
    save(options, document)
    out(step='backup', status='Succeeded', artifactId=artifact['id'], sizeBytes=artifact['sizeBytes'], sha256=artifact['artifactChecksum'],
        replicas=full.get('replicas'), usedBytesBefore=before['usedBytes'], usedBytesAfter=after['usedBytes'],
        usageIncreased=after['usedBytes'] > before['usedBytes'])


def restart_api():
    subprocess.run(['bash', '-c', f'source "{HERE}/common.sh"; dev_kill api'], check=True, capture_output=True)
    subprocess.run([str(HERE / 'start.sh'), '--no-build', '--no-agent'], check=True, capture_output=True)


def retry(options):
    """Real Drive interruption and recovery: the API is restarted while a ~120 MB upload is in flight; the run's own retry policy must finish it with
    exactly one verified artifact and the interrupted attempt's partial object must not stay catalogued. Run `delete` afterwards."""
    subprocess.run([str(HERE / 'mssql-test-db.sh'), 'create-big'], capture_output=True, check=True)
    with unlimited_while_unsized():
        api, tenant = session()
        connection = pick_connection(api, tenant, 'google-drive')
        agent, job = create_job(api, tenant, connection, 'google-drive', SOURCE_DB + '_big')
        _, run = api.call('POST', tenant + f'/backup-jobs/{job["id"]}/runs', {}, headers={'Idempotency-Key': 'dev-retry-' + uuid.uuid4().hex}, expect=(200, 201, 202))
        run_id = run.get('id', run.get('run', {}).get('id'))
        fetch = lambda: api.call('GET', tenant + f'/backup-runs/{run_id}')[1]
        wait('Upload in flight', fetch, lambda d: d['status'] == 'Running', lambda d: d['status'] in ('Failed', 'Cancelled', 'AgentLost', 'Succeeded'), timeout=60, interval=0.2)
        time.sleep(float(os.environ.get('A1_UPLOAD_HEAD_START', '10')))
        restart_api()
        done = wait('Run after the backend restart', fetch, lambda d: d['status'] == 'Succeeded', lambda d: d['status'] in ('Failed', 'Cancelled', 'AgentLost', 'Skipped'), timeout=1200, interval=3)
        outcome = []
        for attempt in done['attempts']:
            for item in attempt['artifacts']:
                _, detail = api.call('GET', tenant + f'/backup-artifacts/{item["id"]}')
                outcome.append({'attempt': attempt['number'], 'attemptStatus': attempt['status'], 'trigger': attempt['trigger'], 'reason': attempt.get('reasonCode'),
                                'artifactState': detail['state'], 'replicas': [(r['status'], r['verificationState']) for r in detail['replicas']]})
        out(step='interrupted', attempts=outcome)
        if len(done['attempts']) < 2:
            raise FixtureFailure('The backend restart did not interrupt the upload; lower A1_UPLOAD_HEAD_START or enlarge the database.')
        artifacts = [item for attempt in done['attempts'] if attempt['status'] == 'Succeeded' for item in attempt['artifacts']]
        if len(artifacts) != 1:
            raise FixtureFailure('The retried run did not publish exactly one artifact.')
        artifact = artifacts[0]
        _, full = api.call('GET', tenant + f'/backup-artifacts/{artifact["id"]}')
        states = [(r['status'], r['verificationState']) for r in full.get('replicas', [])]
        save(options, {'artifactId': artifact['id'], 'checksum': artifact['artifactChecksum'], 'sizeBytes': artifact['sizeBytes'], 'jobId': job['id'],
                       'runId': run_id, 'agentId': agent['agentId'], 'connectionId': connection['id'], 'usedBefore': 0})
        out(step='retry', status='Succeeded', attempts=len(done['attempts']), sizeBytes=artifact['sizeBytes'], sha256=artifact['artifactChecksum'], replicaStates=states)


def download(options):
    api, tenant = session()
    state = load(options)
    path = tenant + f'/backup-artifacts/{state["artifactId"]}'
    _, prepared = api.call('POST', path + '/downloads', {}, expect=(200, 201, 202))
    wait('Download preparation', lambda: api.call('GET', path + '/downloads/' + prepared['sessionId'])[1],
         lambda d: d['state'] in ('Ready', 'Streaming'), lambda d: d['state'] in ('Failed', 'Cancelled'))
    digest, transferred = hashlib.sha256(), 0
    import urllib.request
    with api.opener.open(urllib.request.Request(SERVER + prepared['downloadUrl']), timeout=600) as response:
        while chunk := response.read(128 * 1024):
            digest.update(chunk)
            transferred += len(chunk)
    completed = wait('Download completion', lambda: api.call('GET', path + '/downloads/' + prepared['sessionId'])[1],
                     lambda d: d['state'] == 'Completed', lambda d: d['state'] in ('Failed', 'Cancelled'), timeout=30, interval=0.3)
    ok = transferred == state['sizeBytes'] and digest.hexdigest().lower() == state['checksum'].lower()
    out(step='download', bytes=transferred, sha256=digest.hexdigest(), matchesArtifact=ok, lifecycle=completed['state'], transferredBytes=completed['transferredBytes'])
    if not ok:
        raise FixtureFailure('Downloaded length/SHA-256 differs from the artifact.')


def restore(options):
    api, tenant = session()
    state = load(options)
    # NewDatabase restores refuse an existing target by design (TargetDatabaseExists); only this step's own previous copy is dropped.
    subprocess.run([str(HERE / 'mssql-test-db.sh'), 'drop-restored', RESTORED_DB], capture_output=True, check=True)
    path = tenant + f'/backup-artifacts/{state["artifactId"]}'
    request = {'agentId': state['agentId'], 'target': {'host': '127.0.0.1', 'port': SQL_PORT, 'databaseCredentialReference': sql_login(api, tenant, state['agentId']), 'trustServerCertificate': True},
               'databaseName': RESTORED_DB, 'mode': 'NewDatabase'}
    _, pre = api.call('POST', path + '/restore-precheck', request, headers={'Idempotency-Key': 'dev-pre-' + uuid.uuid4().hex}, expect=(200, 201, 202))
    pre_id = pre.get('id', pre.get('restoreId'))
    final = wait('Restore precheck', lambda: api.call('GET', tenant + f'/restores/{pre_id}')[1],
                 lambda d: d['status'] == 'Succeeded', lambda d: d['status'] in ('Failed', 'Cancelled', 'AgentLost'))
    out(step='restore-precheck', status=final['status'])
    _, started = api.call('POST', path + '/restore', request, headers={'Idempotency-Key': 'dev-res-' + uuid.uuid4().hex}, expect=(200, 201, 202))
    restore_id = started.get('id', started.get('restoreId'))
    final = wait('Restore', lambda: api.call('GET', tenant + f'/restores/{restore_id}')[1],
                 lambda d: d['status'] == 'Succeeded', lambda d: d['status'] in ('Failed', 'Cancelled', 'AgentLost'), timeout=400)
    source, restored = sql_verify(SOURCE_DB), sql_verify(RESTORED_DB)
    out(step='restore', status=final['status'], source=source, restored=restored, identical=source == restored and restored.startswith('ONLINE 5 '))
    if source != restored:
        raise FixtureFailure('Restored database content differs from the source.')


def delete(options):
    api, tenant = session()
    state = load(options)
    path = tenant + f'/backup-artifacts/{state["artifactId"]}'
    used_before = quota(api, tenant)['usedBytes']
    api.call('DELETE', path, expect=(200, 202))
    final = wait('Physical deletion', lambda: api.call('GET', path)[1],
                 lambda d: d['state'] == 'Deleted', lambda d: d['state'] == 'DeleteFailed', timeout=180, interval=2)
    used_after = quota(api, tenant)['usedBytes']
    out(step='delete', artifactState=final['state'], replicas=final.get('replicas'), usedBytesBefore=used_before, usedBytesAfter=used_after,
        quotaReduced=used_after < used_before)


def smoke(options):
    # Keep the operator's acceptance artifact/state untouched, including when verification fails.
    with tempfile.TemporaryDirectory(prefix='m7c1-smoke-', dir=HOME) as directory:
        options.smoke_state = Path(directory) / 'artifact.json'
        try:
            backup(options)
            download(options)
        finally:
            if options.smoke_state.exists():
                uuid.UUID(load(options)['artifactId'])
                delete(options)
            del options.smoke_state


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('step', choices=('backup', 'download', 'restore', 'delete', 'retry', 'all', 'smoke'))
    parser.add_argument('--provider', choices=('google-drive', 'local'), default='google-drive')
    options = parser.parse_args()
    steps = {'backup': backup, 'download': download, 'restore': restore, 'delete': delete, 'retry': retry, 'smoke': smoke}
    try:
        for name in (('backup', 'download', 'restore', 'delete') if options.step == 'all' else [options.step]):
            steps[name](options)
    except (FixtureFailure, OSError, ValueError, KeyError, subprocess.CalledProcessError) as failure:
        print('FAIL: ' + (str(failure) if isinstance(failure, FixtureFailure) else 'acceptance step failed: ' + type(failure).__name__ + ' ' + str(failure)[:300]), file=sys.stderr)
        return 1
    return 0


if __name__ == '__main__':
    sys.exit(main())
