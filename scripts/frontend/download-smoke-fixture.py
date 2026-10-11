#!/usr/bin/env python3
"""Real M4 smoke setup and bounded HTTP download verification.

Uses the existing browser smoke stack and its safe cookie/antiforgery client.
No synthetic artifacts: backup commands require an actually enrolled Agent,
then wait for the real MSSQL job to finish. Private state never goes to stdout.
"""
import argparse
import hashlib
import importlib.util
import json
from pathlib import Path
import sys
import time
import urllib.request
import uuid

spec = importlib.util.spec_from_file_location('quota_fixture', Path(__file__).with_name('quota-smoke-fixture.py'))
shared = importlib.util.module_from_spec(spec)
spec.loader.exec_module(shared)
Api, FixtureFailure = shared.Api, shared.FixtureFailure


def tenant_api(options):
    state = shared.read_private(options.state)
    api = Api(options.server, options.certificate)
    api.login(state['owner']['login'], state['owner']['password'])
    return state, api


def setup(options):
    credentials = shared.read_private(options.credentials_file)
    platform = Api(options.server, options.certificate)
    platform.login(credentials['login'], credentials['password'])
    suffix = uuid.uuid4().hex[:12]
    _, tenant = platform.call('POST', '/api/platform/tenants', {'name': 'Download smoke ' + suffix})
    tenant_id = tenant['tenantId']
    _, plans = platform.call('GET', '/api/platform/plans?enabled=true&pageSize=100')
    plan = next((item for item in plans['items'] if not item['isSystem'] and (item['storageLimitBytes'] or 0) >= 20_000_000_000), None)
    if plan is None:
        raise FixtureFailure('Seeded 20 GB-or-larger enabled plan is required for real backup admission.')
    # Fresh smoke jobs have no size history; transport acceptance uses an explicit unlimited override, not a hard-quota bypass.
    platform.call('PUT', f'/api/platform/tenants/{tenant_id}/subscription',
                  {'planId': plan['planId'], 'storageLimitOverride': {'kind': 'unlimited'}})
    owner = {'login': 'download.owner.' + suffix, 'password': 'Smoke-' + uuid.uuid4().hex + '!'}
    _, user = platform.call('POST', '/api/platform/users', {'loginIdentifier': owner['login'], 'password': owner['password'], 'displayName': 'Download Smoke Owner'})
    platform.call('POST', f'/api/platform/tenants/{tenant_id}/memberships', {'userId': user['userId'], 'role': 'Owner'})
    platform.call('PUT', f'/api/platform/tenants/{tenant_id}/agent-enrollment', {'enabled': True})
    api = Api(options.server, options.certificate)
    api.login(owner['login'], owner['password'])
    state = {'tenantId': tenant_id, 'owner': owner, 'tenantScope': 'download-' + suffix, 'agents': {}, 'artifacts': {}}
    for name in ('central', 'local'):
        _, token = api.call('POST', f'/api/tenants/{tenant_id}/agent-enrollment-tokens', {})
        shared.write_private(str(Path(options.state).with_name(name + '-token.json')), {'token': token['token']})
    shared.write_private(options.state, state)
    print(json.dumps({'tenantId': tenant_id, 'tenantScope': state['tenantScope'], 'ownerLogin': owner['login']}))


def backup(options):
    state, api = tenant_api(options)
    path = f'/api/tenants/{state["tenantId"]}'
    deadline = time.monotonic() + options.timeout
    agent = None
    while time.monotonic() < deadline:
        _, document = api.call('GET', path + '/agents')
        agents = document if isinstance(document, list) else document.get('items', [])
        agent = next((item for item in agents if item.get('hostName') == options.agent_name), None)
        if agent:
            break
        time.sleep(1)
    if agent is None:
        raise FixtureFailure('Actual enrolled Agent was not found.')
    agent_id = agent.get('agentId', agent.get('id'))
    configuration = {'engine': 'mssql', 'host': options.sql_host, 'port': options.sql_port, 'database': options.database,
                     'databaseCredentialReference': 'sql_smoke', 'trustServerCertificate': True,
                     'stagingReference': 'default', 'storageProvider': options.provider, 'storageTarget': 'default',
                     'storagePrefix': 'download-smoke', 'copyOnly': True, 'checksumPolicy': 'Required', 'compressionPolicy': 'Preferred'}
    if options.provider == 'sftp':
        if not options.storage_connection_id:
            raise FixtureFailure('SFTP backup requires an explicit storage connection.')
        configuration['storageConnectionId'] = options.storage_connection_id
        configuration['storageExecutionLocation'] = 'Backend'
        configuration['storagePrefix'] = ''
    _, job = api.call('POST', path + '/backup-jobs', {'name': 'Real download ' + options.provider, 'agentId': agent_id,
                                                   'overlapPolicy': 'Skip', 'configuration': configuration})
    _, run = api.call('POST', path + f'/backup-jobs/{job["id"]}/runs', {}, headers={'Idempotency-Key': 'download-smoke-' + uuid.uuid4().hex}, expect=(200, 201, 202))
    run_id = run.get('id', run.get('run', {}).get('id'))
    if not run_id:
        raise FixtureFailure('Backup API returned no run identifier.')
    while time.monotonic() < deadline:
        _, detail = api.call('GET', path + f'/backup-runs/{run_id}')
        status = detail['status']
        if status == 'Succeeded':
            artifacts = [artifact for attempt in detail['attempts'] for artifact in attempt['artifacts']]
            if len(artifacts) != 1:
                raise FixtureFailure('Real backup did not publish exactly one artifact.')
            artifact = artifacts[0]
            state['agents'][options.provider] = agent_id
            state['artifacts'][options.provider] = {'id': artifact['id'], 'checksum': artifact['artifactChecksum'],
                                                    'sizeBytes': artifact['sizeBytes'], 'jobId': job['id'], 'runId': run_id}
            shared.write_private(options.state, state)
            print(json.dumps({'provider': options.provider, **state['artifacts'][options.provider]}))
            return
        if status in ('Failed', 'Cancelled', 'AgentLost', 'Skipped'):
            codes = [attempt.get('failureCode') for attempt in detail.get('attempts', [])]
            raise FixtureFailure(f'Real backup ended as {status} ({codes}).')
        time.sleep(1)
    raise FixtureFailure('Actual MSSQL backup timed out.')


def verify(options):
    state, api = tenant_api(options)
    artifact = state['artifacts'][options.provider]
    path = f'/api/tenants/{state["tenantId"]}/backup-artifacts/{artifact["id"]}'
    _, session = api.call('POST', path + '/downloads', {}, expect=(200, 201, 202))
    deadline = time.monotonic() + options.timeout
    while time.monotonic() < deadline:
        _, status = api.call('GET', path + '/downloads/' + session['sessionId'])
        if status['state'] in ('Ready', 'Streaming'):
            break
        if status['state'] in ('Failed', 'Cancelled'):
            raise FixtureFailure('Download preparation failed (' + str(status.get('failureCode')) + ').')
        time.sleep(0.5)
    else:
        raise FixtureFailure('Download preparation timed out.')
    url = session['downloadUrl']
    if not url.startswith('/api/'):
        raise FixtureFailure('Download URL must be a backend-relative authorized route.')
    digest, transferred = hashlib.sha256(), 0
    with api.opener.open(urllib.request.Request(options.server.rstrip('/') + url), timeout=options.timeout) as response:
        if response.status != 200:
            raise FixtureFailure('Download did not return HTTP 200.')
        with open(options.output, 'xb') as target:
            while chunk := response.read(128 * 1024):
                digest.update(chunk)
                transferred += len(chunk)
                target.write(chunk)
    if transferred != artifact['sizeBytes'] or digest.hexdigest().lower() != artifact['checksum'].lower():
        raise FixtureFailure('Downloaded length/SHA-256 differs from the actual artifact.')
    completion_deadline = time.monotonic() + 15
    while True:
        _, completed = api.call('GET', path + '/downloads/' + session['sessionId'])
        if completed['state'] in ('Completed', 'Failed', 'Cancelled') or time.monotonic() >= completion_deadline:
            break
        time.sleep(0.1)
    if completed['state'] != 'Completed' or completed['transferredBytes'] != transferred:
        raise FixtureFailure('Download lifecycle did not record completion with exact transferred bytes.')
    print(json.dumps({'provider': options.provider, 'artifactId': artifact['id'], 'bytes': transferred,
                      'sha256': digest.hexdigest(), 'matchesArtifactChecksum': True, 'auditState': completed['state']}))


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--server', required=True)
    parser.add_argument('--certificate', required=True)
    parser.add_argument('--state', required=True)
    commands = parser.add_subparsers(dest='command', required=True)
    first = commands.add_parser('setup')
    first.add_argument('--credentials-file', required=True)
    second = commands.add_parser('backup')
    second.add_argument('--provider', choices=('central', 'local', 'sftp'), required=True)
    second.add_argument('--storage-connection-id')
    second.add_argument('--agent-name', required=True)
    second.add_argument('--sql-host', required=True)
    second.add_argument('--sql-port', type=int, default=1433)
    second.add_argument('--database', default='master')
    second.add_argument('--timeout', type=int, default=240)
    third = commands.add_parser('verify')
    third.add_argument('--provider', choices=('central', 'local', 'sftp'), required=True)
    third.add_argument('--output', required=True)
    third.add_argument('--timeout', type=int, default=240)
    options = parser.parse_args()
    try:
        {'setup': setup, 'backup': backup, 'verify': verify}[options.command](options)
    except (FixtureFailure, OSError, ValueError, KeyError) as failure:
        # Only our fixed errors are safe; HTTP and IO errors may include physical locations.
        message = str(failure) if isinstance(failure, FixtureFailure) else 'Smoke operation failed (details withheld).'
        print('FAIL: ' + message, file=sys.stderr)
        return 1
    return 0


if __name__ == '__main__':
    sys.exit(main())
