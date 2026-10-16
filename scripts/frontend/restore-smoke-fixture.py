#!/usr/bin/env python3
"""Extend the real backup smoke fixture with tenant roles and restore security checks."""
import argparse
import importlib.util
import json
from pathlib import Path
import sys
import uuid

spec = importlib.util.spec_from_file_location('download_fixture', Path(__file__).with_name('download-smoke-fixture.py'))
shared = importlib.util.module_from_spec(spec)
spec.loader.exec_module(shared)


def setup(options):
    state = shared.shared.read_private(options.state)
    credentials = shared.shared.read_private(options.credentials_file)
    platform = shared.Api(options.server, options.certificate)
    platform.login(credentials['login'], credentials['password'])
    state['roles'] = {'Owner': state['owner']}
    for role in ('Admin', 'Operator', 'Viewer'):
        account = {'login': 'restore.' + role.lower() + '.' + uuid.uuid4().hex[:12], 'password': 'Smoke-' + uuid.uuid4().hex + '!'}
        _, user = platform.call('POST', '/api/platform/users', {'loginIdentifier': account['login'], 'password': account['password'], 'displayName': 'Restore Smoke ' + role})
        platform.call('POST', f'/api/platform/tenants/{state["tenantId"]}/memberships', {'userId': user['userId'], 'role': role})
        state['roles'][role] = account
    _, foreign = platform.call('POST', '/api/platform/tenants', {'name': 'Restore isolation ' + uuid.uuid4().hex[:12]})
    account = {'login': 'restore.foreign.' + uuid.uuid4().hex[:12], 'password': 'Smoke-' + uuid.uuid4().hex + '!'}
    _, user = platform.call('POST', '/api/platform/users', {'loginIdentifier': account['login'], 'password': account['password'], 'displayName': 'Other Tenant Owner'})
    platform.call('POST', f'/api/platform/tenants/{foreign["tenantId"]}/memberships', {'userId': user['userId'], 'role': 'Owner'})
    state['foreignTenantId'], state['foreignOwner'] = foreign['tenantId'], account
    state['sourceDatabase'], state['targetDatabase'] = options.source_database, options.target_database
    shared.shared.write_private(options.state, state)
    print(json.dumps({'roles': list(state['roles']), 'crossTenantFixtureReady': True}))


def verify(options):
    state = shared.shared.read_private(options.state)
    artifact_id = state['artifacts']['central']['id']
    agent_id = state['agents']['central']
    tenant_path = f'/api/tenants/{state["tenantId"]}'
    owner = shared.Api(options.server, options.certificate)
    owner.login(state['owner']['login'], state['owner']['password'])
    request = {'agentId': agent_id, 'target': {'host': options.sql_host, 'port': options.sql_port, 'databaseCredentialReference': shared.shared.store_sql_login(owner, tenant_path, agent_id), 'trustServerCertificate': True},
               'databaseName': state['sourceDatabase'], 'mode': 'ReplaceExistingDatabase'}
    evidence = []
    for role, credentials in state['roles'].items():
        api = shared.Api(options.server, options.certificate)
        api.login(credentials['login'], credentials['password'])
        api.call('GET', tenant_path + '/restores', expect=(200,))
        for confirmation in (None, 'not-the-target-database'):
            body = {**request, **({'confirmOverwrite': confirmation} if confirmation else {})}
            status, document = api.call('POST', tenant_path + f'/backup-artifacts/{artifact_id}/restore', body,
                                        headers={'Idempotency-Key': 'm5-guard-' + uuid.uuid4().hex}, expect=((403,) if role == 'Viewer' else (400,)))
            if role != 'Viewer' and document.get('field') != 'confirmOverwrite':
                raise shared.FixtureFailure('Replace was not refused by authoritative overwrite confirmation validation.')
            evidence.append({'role': role, 'confirmation': 'missing' if confirmation is None else 'wrong', 'httpStatus': status})
    foreign = shared.Api(options.server, options.certificate)
    foreign.login(state['foreignOwner']['login'], state['foreignOwner']['password'])
    status, _ = foreign.call('POST', f'/api/tenants/{state["foreignTenantId"]}/backup-artifacts/{artifact_id}/restore',
                             {**request, 'mode': 'NewDatabase', 'databaseName': state['targetDatabase']},
                             headers={'Idempotency-Key': 'm5-cross-' + uuid.uuid4().hex}, expect=(404,))
    evidence.append({'crossTenantArtifact': 'denied', 'httpStatus': status})
    platform = shared.Api(options.server, options.certificate)
    credentials = shared.shared.read_private(options.credentials_file)
    platform.login(credentials['login'], credentials['password'])
    platform.call('GET', tenant_path + '/restores', expect=(403,))
    print(json.dumps({'authorizationAndReplaceGuards': evidence, 'platformAdminWithoutMembership': 'denied'}))


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--server', required=True)
    parser.add_argument('--certificate', required=True)
    parser.add_argument('--state', required=True)
    parser.add_argument('--credentials-file', required=True)
    commands = parser.add_subparsers(dest='command', required=True)
    first = commands.add_parser('setup')
    first.add_argument('--source-database', required=True)
    first.add_argument('--target-database', required=True)
    second = commands.add_parser('verify')
    second.add_argument('--sql-host', required=True)
    second.add_argument('--sql-port', type=int, required=True)
    options = parser.parse_args()
    try:
        {'setup': setup, 'verify': verify}[options.command](options)
    except (shared.FixtureFailure, OSError, ValueError, KeyError) as failure:
        print('FAIL: ' + (str(failure) if isinstance(failure, shared.FixtureFailure) else 'Restore smoke fixture failed (details withheld).'), file=sys.stderr)
        return 1
    return 0


if __name__ == '__main__':
    sys.exit(main())
