#!/usr/bin/env python3
"""Real M7d.3 acceptance on the running dev stack: ONE MSSQL backup -> Central + SFTP + Google Drive replicas, through the HTTP API only.

Driven by scripts/dev/multi-destination-acceptance.sh, which owns the disposable SFTP container and restarts the dev API/Agent with a Central root.
Nothing is mocked: every destination is a real transport (Agent writes Central directly, SFTP and Google Drive go through the authorized backend relay).

  healthy       3 destinations, required 2: one engine backup, 3 verified replicas, same SHA-256 everywhere (checked on disk for Central/SFTP)
  degraded      SFTP unreachable, required 2: the run is Succeeded, replication Degraded, the artifact stays usable
  insufficient  SFTP unreachable, required 3: the run is Succeeded, replication InsufficientReplicas (never Healthy)
Only artifacts created by this script are deleted. State: $BP_DEV_HOME/multi-acceptance.json (0600, no secrets).
"""
import hashlib
import importlib.util
import json
import os
from pathlib import Path
import subprocess
import sys
import time
import uuid

HERE = Path(__file__).parent
spec = importlib.util.spec_from_file_location('acceptance', HERE / 'acceptance.py')
acc = importlib.util.module_from_spec(spec)
spec.loader.exec_module(acc)
FixtureFailure, shared = acc.FixtureFailure, acc.shared
HOME = acc.HOME
STATE = HOME / 'multi-acceptance.json'


def load():
    return shared.read_private(str(STATE))


def save(state):
    shared.write_private(str(STATE), state)


def require(condition, message):
    if not condition:
        raise FixtureFailure(message)


def sa_password():
    for line in (HOME / 'secrets.env').read_text().splitlines():
        if line.startswith('BP_DEV_MSSQL_SA_PASSWORD='):
            return line.split('=', 1)[1]
    raise FixtureFailure('The dev SQL Server password is not available.')


def backup_sets():
    """How many full backups of the acceptance database SQL Server itself recorded (msdb): the proof of 'one engine backup'."""
    query = f"SET NOCOUNT ON; SELECT COUNT(*) FROM msdb.dbo.backupset WHERE database_name = N'{acc.SOURCE_DB}' AND type = 'D';"
    result = subprocess.run(['docker', 'exec', '-i', '-e', 'SQLCMDPASSWORD=' + sa_password(), 'bp-dev-mssql', '/opt/mssql-tools18/bin/sqlcmd',
                             '-S', 'localhost', '-U', 'sa', '-C', '-b', '-h', '-1', '-W', '-Q', query], capture_output=True, text=True, check=True)
    return int(result.stdout.strip().splitlines()[-1])


def sha256_of(path):
    digest = hashlib.sha256()
    with open(path, 'rb') as handle:
        while chunk := handle.read(128 * 1024):
            digest.update(chunk)
    return digest.hexdigest()


def find_files(root, name):
    return [p for p in Path(root).rglob(name) if p.is_file()]


def prepare(options):
    api, tenant = acc.session()
    config = shared.read_private(options['sftp_file'])
    _, descriptors = api.call('GET', tenant + '/storage-providers')
    descriptor = next(item for item in descriptors['items'] if item['providerType'] == 'sftp')
    require(descriptor['availability'] == 'Available', 'The SFTP provider is not available.')
    previous = None
    try:
        previous = load()
    except (OSError, ValueError, KeyError):
        pass
    view = None
    if previous and previous.get('sftpConnectionId'):
        # A previous run left its connection (history still references it): point it at THIS disposable SFTP so leftover replicas stay deletable.
        connection = tenant + f'/storage-connections/{previous["sftpConnectionId"]}'
        try:
            api.call('PUT', connection, {'configuration': config['configuration']}, expect=(200,))
            api.call('PUT', connection + '/secret', {'values': {'password': config['password']}}, expect=(200, 204))
            api.call('POST', connection + '/enable', {}, expect=(200, 204, 409))
            view = {'id': previous['sftpConnectionId']}
        except FixtureFailure:
            view = None
    if view is None:
        _, view = api.call('POST', tenant + '/storage-connections', {'providerType': 'sftp', 'name': 'Acceptance SFTP ' + uuid.uuid4().hex[:6],
                                                                      'configuration': config['configuration'], 'secret': {'password': config['password']}}, expect=(200, 201))
    _, outcome = api.call('POST', tenant + f'/storage-connections/{view["id"]}/test', {})
    require(outcome['test']['status'] == 'Succeeded', 'The disposable SFTP connection test did not succeed.')
    drive = acc.pick_connection(api, tenant, 'google-drive')
    _, listing = api.call('GET', tenant + '/storage-connections')
    items = listing['items'] if isinstance(listing, dict) else listing
    central = next((c for c in items if c['providerType'] == 'central' and c['isDefault'] and c['effectiveEnabled']), None)
    require(central is not None, 'No enabled default Central connection.')
    leftovers = {'leftover-' + label: entry for label, entry in (previous or {}).get('artifacts', {}).items()}
    for entry in leftovers.values():
        # A fresh disposable SFTP has no directory for an earlier run's key: recreate it so the retried deletion finds "no file" (idempotent) instead of "no directory".
        _, old = api.call('GET', tenant + f'/backup-artifacts/{entry["artifactId"]}')
        for replica in old['replicas']:
            if replica['storageProvider'] == 'sftp' and replica['status'] != 'Deleted':
                target = Path(options['sftp_root']) / Path(replica['key']).parent
                target.mkdir(parents=True, exist_ok=True)
                for directory in [target, *target.parents]:
                    if directory == Path(options['sftp_root']).parent:
                        break
                    os.chmod(directory, 0o777)
    save({'sftpConnectionId': view['id'], 'driveConnectionId': drive['id'], 'centralConnectionId': central['id'], 'sftpContainer': options['container'],
          'sftpRoot': options['sftp_root'], 'centralRoot': options['central_root'], 'artifacts': leftovers, 'jobs': (previous or {}).get('jobs', [])})
    print(json.dumps({'step': 'prepare', 'sftpConnectionTest': outcome['test']['status'], 'googleDriveConnection': 'hasSecret=' + str(drive['hasSecret']), 'central': 'enabled',
                      'leftoverArtifactsFromAnEarlierRun': len(leftovers)}))


def run_backup(label, required):
    api, tenant = acc.session()
    state = load()
    _, agents = api.call('GET', tenant + '/agents')
    agent = next((a for a in (agents if isinstance(agents, list) else agents['items']) if a['hostName'] == 'dev-linux-agent' and a['connectivity'] == 'online'), None)
    require(agent is not None, 'The dev-linux-agent is not online.')
    destinations = [
        {'storageProvider': 'central', 'storageConnectionId': state['centralConnectionId'], 'storageExecutionLocation': 'Backend', 'isPrimary': True},
        {'storageProvider': 'sftp', 'storageConnectionId': state['sftpConnectionId'], 'storageExecutionLocation': 'Backend', 'isPrimary': False},
        {'storageProvider': 'google-drive', 'storageConnectionId': state['driveConnectionId'], 'storageExecutionLocation': 'Backend', 'isPrimary': False},
    ]
    configuration = {'engine': 'mssql', 'host': '127.0.0.1', 'port': acc.SQL_PORT, 'database': acc.SOURCE_DB, 'integratedSecurity': False,
                     'databaseCredentialReference': acc.sql_login(api, tenant, agent['agentId']), 'trustServerCertificate': True, 'encrypt': True, 'stagingReference': 'default', 'storagePrefix': '',
                     'copyOnly': True, 'checksumPolicy': 'Required', 'compressionPolicy': 'Preferred', 'destinations': destinations, 'requiredVerifiedReplicaCount': required}
    sets_before, quota_before = backup_sets(), acc.quota(api, tenant)
    _, job = api.call('POST', tenant + '/backup-jobs', {'name': f'M7d3 acceptance {label} {time.strftime("%H%M%S")}', 'agentId': agent['agentId'],
                                                       'overlapPolicy': 'Skip', 'configuration': configuration}, expect=(200, 201))
    state['jobs'].append(job['id'])
    _, run = api.call('POST', tenant + f'/backup-jobs/{job["id"]}/runs', {}, headers={'Idempotency-Key': 'm7d3-' + uuid.uuid4().hex}, expect=(200, 201, 202))
    run_id = run.get('id', run.get('run', {}).get('id'))
    terminal = ('Succeeded', 'Failed', 'Cancelled', 'AgentLost')
    detail = acc.wait('Backup run', lambda: api.call('GET', tenant + f'/backup-runs/{run_id}')[1], lambda d: d['status'] in terminal, lambda d: False, timeout=420)
    sets_after, quota_after = backup_sets(), acc.quota(api, tenant)
    artifacts = [a for attempt in detail['attempts'] for a in attempt['artifacts']]
    require(detail['status'] == 'Succeeded' and len(artifacts) == 1, f'The {label} run did not publish exactly one artifact (status {detail["status"]}).')
    _, artifact = api.call('GET', tenant + f'/backup-artifacts/{artifacts[0]["id"]}')
    replicas = artifact['replicas']
    replication = detail['replication']
    state['artifacts'][label] = {'artifactId': artifact['id'], 'sizeBytes': artifact['sizeBytes'], 'checksum': artifact['artifactChecksum'], 'runId': run_id}
    save(state)
    evidence = {'step': label, 'runStatus': detail['status'], 'attempts': detail['currentAttemptNumber'], 'sqlServerFullBackupsTaken': sets_after - sets_before,
                'artifactCount': 1, 'sha256': artifact['artifactChecksum'], 'sizeBytes': artifact['sizeBytes'],
                'replication': {k: replication[k] for k in ('status', 'verifiedReplicas', 'requiredVerifiedReplicas', 'destinationCount', 'policySatisfied')},
                'destinations': [{'provider': d['storageProvider'], 'state': d['state'], 'failureCode': d['failureCode']} for d in replication['destinations']],
                'replicas': [{'provider': r['storageProvider'], 'verification': r['verificationState'], 'method': r['verificationMethod'], 'status': r['status'],
                              'sameChecksum': r['checksum'] == artifact['artifactChecksum']} for r in replicas],
                'logicalUsedDelta': quota_after['usedBytes'] - quota_before['usedBytes'], 'reservedAfter': quota_after['reservedBytes']}
    require(sets_after - sets_before == 1, 'SQL Server must have taken exactly ONE full backup.')
    require(detail['currentAttemptNumber'] == 1, 'The run needed a retry.')
    require(all(r['sameChecksum'] and r['verification'] == 'Verified' for r in evidence['replicas']), 'A replica is not a verified copy of the one artifact.')
    require(evidence['logicalUsedDelta'] == artifact['sizeBytes'] and quota_after['reservedBytes'] == 0, 'Logical quota must count the artifact once and leak no reservation.')
    if label == 'healthy':
        require((replication['status'], replication['verifiedReplicas']) == ('Healthy', 3) and len(replicas) == 3, 'Expected 3 verified replicas and Healthy.')
        name = Path(replicas[0]['key']).name
        central = find_files(state['centralRoot'], name)
        sftp = find_files(state['sftpRoot'], name)
        require(len(central) == 1 and len(sftp) == 1, 'The artifact must exist exactly once on Central and once on SFTP.')
        evidence['onDisk'] = {'centralSha256Matches': sha256_of(central[0]) == artifact['artifactChecksum'], 'sftpSha256Matches': sha256_of(sftp[0]) == artifact['artifactChecksum']}
        require(all(evidence['onDisk'].values()), 'A stored copy differs from the artifact SHA-256.')
    elif label == 'degraded':
        sftp_state = next(d for d in replication['destinations'] if d['storageProvider'] == 'sftp')
        require(replication['status'] == 'Degraded' and replication['policySatisfied'] and replication['verifiedReplicas'] == 2 and sftp_state['state'] == 'Failed',
                'Expected Degraded (2 verified, policy satisfied) with the SFTP destination Failed.')
        require(artifact['state'] == 'Available' and sorted(r['storageProvider'] for r in replicas) == ['central', 'google-drive'], 'The usable artifact must keep its two verified copies.')
    else:
        require(replication['status'] == 'InsufficientReplicas' and not replication['policySatisfied'] and replication['verifiedReplicas'] == 2,
                'Expected InsufficientReplicas (2 of 3 required).')
        require(artifact['state'] == 'Available', 'The artifact must stay usable.')
    leftovers = [p.name for p in Path(state['sftpRoot']).rglob('*') if p.is_file()]
    evidence['sftpFilesOnDisk'] = len(leftovers)
    print(json.dumps(evidence))


def delete_artifacts(labels):
    api, tenant = acc.session()
    state = load()
    for label in labels:
        entry = state['artifacts'][label]
        path = tenant + f'/backup-artifacts/{entry["artifactId"]}'
        used_before = acc.quota(api, tenant)['usedBytes']
        api.call('DELETE', path, expect=(200, 202, 409))
        leftover = label.startswith('leftover-')
        try:
            final = acc.wait(f'Physical deletion ({label})', lambda: api.call('GET', path)[1], lambda d: d['state'] == 'Deleted',
                             lambda d: d['state'] == 'DeleteFailed' and not leftover, timeout=30 if leftover else 240, interval=2)
        except FixtureFailure:
            if not leftover:
                raise
            # An earlier run's artifact whose SFTP replica is still in retry backoff: it is retried by retention on its own schedule; it never blocks this acceptance.
            print(json.dumps({'step': 'delete-' + label, 'status': 'still retrying (retention backoff); not part of this acceptance'}))
            continue
        used_after = acc.quota(api, tenant)['usedBytes']
        name = Path(final['replicas'][0]['key']).name
        remaining = find_files(state['centralRoot'], name) + find_files(state['sftpRoot'], name)
        print(json.dumps({'step': 'delete-' + label, 'artifactState': final['state'], 'replicaStatuses': {r['storageProvider']: r['status'] for r in final['replicas']},
                          'usedBytesBefore': used_before, 'usedBytesAfter': used_after, 'filesRemainingOnCentralOrSftp': len(remaining)}))
        require(final['state'] == 'Deleted' and used_after < used_before and not remaining, f'Deleting the {label} artifact did not remove every copy.')


def cleanup():
    api, tenant = acc.session()
    state = load()
    for job_id in state['jobs']:
        api.call('POST', tenant + f'/backup-jobs/{job_id}/disable', {}, expect=(200, 204, 404, 409))
    status, _ = api.call('DELETE', tenant + f'/storage-connections/{state["sftpConnectionId"]}', expect=(200, 204, 409))
    if status == 409:
        api.call('POST', tenant + f'/storage-connections/{state["sftpConnectionId"]}/disable', {}, expect=(200, 204))
    print(json.dumps({'step': 'cleanup', 'sftpConnection': 'deleted' if status != 409 else 'disabled (history still references it)'}))


def main():
    args = sys.argv[1:]
    try:
        command = args[0]
        if command == 'prepare':
            prepare({'sftp_file': args[1], 'container': args[2], 'sftp_root': args[3], 'central_root': args[4]})
        elif command == 'run':
            run_backup(args[1], int(args[2]))
        elif command == 'delete':
            delete_artifacts(args[1:])
        elif command == 'delete-leftovers':
            delete_artifacts([label for label in load()['artifacts'] if label.startswith('leftover-')])
        elif command == 'tenant-id':
            _, tenant = acc.session()
            print(tenant.rsplit('/', 1)[1])
        elif command == 'cleanup':
            cleanup()
        else:
            raise FixtureFailure('Unknown command.')
    except (FixtureFailure, OSError, ValueError, KeyError, IndexError, subprocess.CalledProcessError) as failure:
        print('FAIL: ' + (str(failure) if isinstance(failure, FixtureFailure) else 'acceptance step failed: ' + type(failure).__name__ + ' ' + str(failure)[:300]), file=sys.stderr)
        return 1
    return 0


if __name__ == '__main__':
    sys.exit(main())
