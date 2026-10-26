#!/usr/bin/env python3
"""M7a smoke checks over the real HTTP API of the disposable smoke stack.

Runs after a REAL Central backup (actual Agent + MSSQL, see download-smoke-fixture.py). It never synthesizes artifacts and never prints
credentials or physical paths: only fixed, safe evidence goes to stdout.
"""
import argparse
import importlib.util
import json
import sys
import time
import uuid
from pathlib import Path

spec = importlib.util.spec_from_file_location('download_fixture', Path(__file__).with_name('download-smoke-fixture.py'))
shared = importlib.util.module_from_spec(spec)
spec.loader.exec_module(shared)
Fixture, Api = shared.FixtureFailure, shared.Api
read_private, write_private = shared.shared.read_private, shared.shared.write_private


def clients(options):
    state = read_private(options.state)
    credentials = read_private(options.credentials_file)
    owner = Api(options.server, options.certificate)
    owner.login(state['owner']['login'], state['owner']['password'])
    platform = Api(options.server, options.certificate)
    platform.login(credentials['login'], credentials['password'])
    return state, owner, platform


def require(condition, message):
    if not condition:
        raise Fixture(message)


def central(items):
    found = [item for item in items if item['providerType'] == 'central']
    require(len(found) == 1, 'Exactly one central (Managed Storage) connection is expected.')
    return found[0]


def inspect(options):
    state, owner, platform = clients(options)
    tenant = f'/api/tenants/{state["tenantId"]}'
    artifact = state['artifacts']['central']
    _, providers = owner.call('GET', tenant + '/storage-providers')
    by_type = {item['providerType']: item for item in providers['items']}
    require(by_type['central']['availability'] == 'Available' and by_type['local']['availability'] == 'Available', 'Local and Central must be available providers.')
    require(by_type['google-drive']['availability'] == 'Planned' and not by_type['google-drive']['tenantCreatable'] and not by_type['google-drive']['capabilities'],
            'Google Drive must remain planned, not creatable and capability-less.')
    _, listing = owner.call('GET', tenant + '/storage-connections')
    items = listing['items']
    managed = central(items)
    local = next(item for item in items if item['providerType'] == 'local')
    require(managed['ownership'] == 'PlatformManaged' and managed['quotaScope'] == 'managed' and managed['isDefault'] and managed['enabled'], 'Managed Storage default is wrong.')
    require(local['ownership'] == 'TenantManaged' and local['quotaScope'] == 'tenant' and local['isDefault'], 'Local default is wrong.')
    require(not any(managed['permissions'].values()), 'A tenant must not manage the platform-managed connection.')
    require(managed['replicaCount'] == 1 and managed['storedBytes'] == artifact['sizeBytes'], 'The Central replica is not accounted on the Managed Storage connection.')
    require(not managed['hasSecret'] and managed['secretFieldNames'] == [], 'No secret expected.')
    _, artifact_view = owner.call('GET', tenant + '/backup-artifacts/' + artifact['id'])
    replica = artifact_view['replicas'][0]
    require(replica['storageProvider'] == 'central' and replica['storageConnectionId'] == managed['id'], 'The replica does not point at the Managed Storage connection.')
    _, quota = owner.call('GET', tenant + '/storage-quota')
    require(quota['usedBytes'] == artifact['sizeBytes'], 'Quota usage must equal the real artifact size.')
    _, summary = platform.call('GET', f'/api/platform/storage-connections?tenantId={state["tenantId"]}')
    platform_items = {item['id']: item for item in summary['items']}
    require({managed['id'], local['id']} <= set(platform_items) and platform_items[managed['id']]['tenantName'], 'Platform summary lacks the tenant connections.')
    require(platform_items[managed['id']]['permissions']['canToggle'] and platform_items[managed['id']]['permissions']['canTest'], 'PlatformAdmin must manage Managed Storage.')
    require(not platform_items[local['id']]['permissions']['canEdit'] and platform_items[local['id']]['permissions']['canToggle'], 'PlatformAdmin may only toggle tenant-managed storage.')
    stage = options.stage
    previous = state.get('connections')
    if stage == 'after-upgrade':
        require(previous and previous['managedId'] != managed['id'], 'The upgrade round trip must have re-created the connection ids.')
        require(previous['usedBytes'] == quota['usedBytes'], 'Usage changed across the upgrade.')
    state['connections'] = {'managedId': managed['id'], 'localId': local['id'], 'usedBytes': quota['usedBytes']}
    write_private(options.state, state)
    print(json.dumps({'stage': stage, 'managedStorage': {'ownership': managed['ownership'], 'quotaScope': managed['quotaScope'], 'replicaCount': managed['replicaCount']},
                      'local': local['quotaScope'], 'replicaConnectionMatches': True, 'usedBytes': quota['usedBytes'], 'plannedProviders': ['google-drive']}))


def checks(options):
    state, owner, platform = clients(options)
    tenant = f'/api/tenants/{state["tenantId"]}'
    ids = state['connections']
    evidence = {}
    # Honest tests: Central is really written/verified/deleted, agent-local storage is NOT TESTABLE (never a fake success).
    _, result = platform.call('POST', f'/api/platform/storage-connections/{ids["managedId"]}/test', {})
    require(result['test']['status'] == 'Succeeded', 'Central connection test should succeed against the configured root.')
    evidence['centralTest'] = result['test']['status']
    _, result = owner.call('POST', f'{tenant}/storage-connections/{ids["localId"]}/test', {})
    require(result['test']['status'] == 'NotTestable' and result['test']['code'] == 'agent_side_storage', 'Local test must be NotTestable.')
    evidence['localTest'] = result['test']['status']
    status, _ = owner.call('POST', f'{tenant}/storage-connections/{ids["managedId"]}/test', {}, expect=(409,))
    evidence['tenantTestingManaged'] = status
    # Tenants cannot create platform-managed providers.
    status, _ = owner.call('POST', f'{tenant}/storage-connections', {'providerType': 'central', 'name': 'Mine'}, expect=(403,))
    evidence['tenantCreatesCentral'] = status
    status, _ = owner.call('PUT', f'{tenant}/storage-connections/{ids["localId"]}/secret', {'values': {'password': 'never-stored'}}, expect=(400,))
    evidence['secretOnSecretlessProvider'] = status
    # Another tenant sees nothing of this tenant.
    suffix = uuid.uuid4().hex[:12]
    _, foreign_tenant = platform.call('POST', '/api/platform/tenants', {'name': 'Connections isolation ' + suffix})
    account = {'login': 'conn.foreign.' + suffix, 'password': 'Smoke-' + uuid.uuid4().hex + '!'}
    _, user = platform.call('POST', '/api/platform/users', {'loginIdentifier': account['login'], 'password': account['password'], 'displayName': 'Foreign Owner'})
    platform.call('POST', f'/api/platform/tenants/{foreign_tenant["tenantId"]}/memberships', {'userId': user['userId'], 'role': 'Owner'})
    foreign = Api(options.server, options.certificate)
    foreign.login(account['login'], account['password'])
    foreign_path = f'/api/tenants/{foreign_tenant["tenantId"]}'
    _, foreign_list = foreign.call('GET', foreign_path + '/storage-connections')
    require(not {ids['managedId'], ids['localId']} & {item['id'] for item in foreign_list['items']}, 'Another tenant listed this tenant\'s connections.')
    status, _ = foreign.call('GET', f'{foreign_path}/storage-connections/{ids["managedId"]}', expect=(404,))
    evidence['foreignGet'] = status
    status, _ = foreign.call('GET', f'{tenant}/storage-connections', expect=(403,))
    evidence['foreignOnOtherTenantRoute'] = status
    status, _ = owner.call('GET', '/api/platform/storage-connections', expect=(403,))
    evidence['ownerOnPlatformRoute'] = status
    # The audit trail records the lifecycle without secrets.
    _, audit = platform.call('GET', f'/api/platform/audit?tenantId={state["tenantId"]}&pageSize=100')
    actions = {item['action'] for item in audit['items']}
    require({'storage_connection.tested'} <= actions, 'Connection tests must be audited.')
    evidence['auditedActions'] = sorted(action for action in actions if action.startswith('storage_connection.'))
    print(json.dumps(evidence))


def toggle(options):
    state, _, platform = clients(options)
    ids = state['connections']
    action = 'disable' if options.disable else 'enable'
    _, view = platform.call('POST', f'/api/platform/storage-connections/{ids["managedId"]}/{action}', {})
    require(view['enabled'] is (not options.disable), 'Toggle did not take effect.')
    print(json.dumps({'managedStorageEnabled': view['enabled']}))


def disabled_semantics(options):
    state, owner, _ = clients(options)
    tenant = f'/api/tenants/{state["tenantId"]}'
    artifact = state['artifacts']['central']
    status, body = owner.call('POST', tenant + f'/backup-jobs/{artifact["jobId"]}/runs', {}, headers={'Idempotency-Key': 'm7a-denied-' + uuid.uuid4().hex}, expect=(409,))
    require(body.get('error') == 'admission_denied' and body.get('code') == 'storage_connection_disabled', 'A disabled connection must refuse new runs with its own code.')
    _, artifact_view = owner.call('GET', tenant + '/backup-artifacts/' + artifact['id'])
    replica = artifact_view['replicas'][0]
    require(replica['status'] == 'Available' and replica['usable'], 'The replica on a disabled connection must stay Available.')
    _, quota = owner.call('GET', tenant + '/storage-quota')
    require(quota['usedBytes'] == artifact['sizeBytes'], 'Usage must keep counting replicas on a disabled connection.')
    _, listing = owner.call('GET', tenant + '/storage-connections')
    managed = central(listing['items'])
    require(not managed['enabled'] and managed['replicaCount'] == 1, 'The disabled connection must still list its replica.')
    print(json.dumps({'newRun': status, 'denialCode': body['code'], 'replicaStatus': replica['status'], 'usedBytes': quota['usedBytes']}))


def restore(options):
    state, owner, _ = clients(options)
    tenant = f'/api/tenants/{state["tenantId"]}'
    artifact = state['artifacts'][options.provider]
    agent_id = state['agents'][options.provider]
    request = {'agentId': agent_id, 'target': {'host': options.sql_host, 'port': options.sql_port, 'databaseCredentialReference': shared.store_sql_login(owner, tenant, agent_id), 'trustServerCertificate': True},
               'databaseName': options.database, 'mode': 'NewDatabase'}
    _, started = owner.call('POST', tenant + f'/backup-artifacts/{artifact["id"]}/restore', request,
                            headers={'Idempotency-Key': 'm7a-restore-' + uuid.uuid4().hex}, expect=(200, 202))
    restore_id = started.get('id') or started.get('restoreId') or started.get('restore', {}).get('id')
    require(restore_id, 'Restore API returned no identifier.')
    deadline = time.monotonic() + options.timeout
    while time.monotonic() < deadline:
        _, detail = owner.call('GET', tenant + f'/restores/{restore_id}')
        if detail['status'] == 'Succeeded':
            print(json.dumps({'restore': 'Succeeded', 'targetState': detail.get('targetState')}))
            return
        require(detail['status'] not in ('Failed', 'Cancelled', 'AgentLost'), f'Restore ended as {detail["status"]}.')
        time.sleep(1)
    raise Fixture('Restore timed out.')


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--server', required=True)
    parser.add_argument('--certificate', required=True)
    parser.add_argument('--state', required=True)
    parser.add_argument('--credentials-file', required=True)
    commands = parser.add_subparsers(dest='command', required=True)
    commands.add_parser('inspect').add_argument('--stage', choices=('before', 'after-upgrade'), required=True)
    commands.add_parser('checks')
    toggled = commands.add_parser('toggle')
    toggled.add_argument('--disable', action='store_true')
    commands.add_parser('disabled-semantics')
    restoring = commands.add_parser('restore')
    restoring.add_argument('--provider', choices=('central', 'sftp'), default='central')
    restoring.add_argument('--database', required=True)
    restoring.add_argument('--sql-host', required=True)
    restoring.add_argument('--sql-port', type=int, required=True)
    restoring.add_argument('--timeout', type=int, default=240)
    options = parser.parse_args()
    try:
        {'inspect': inspect, 'checks': checks, 'toggle': toggle, 'disabled-semantics': disabled_semantics, 'restore': restore}[options.command](options)
    except (Fixture, OSError, ValueError, KeyError) as failure:
        print('FAIL: ' + (str(failure) if isinstance(failure, Fixture) else 'Storage connections smoke failed (details withheld).'), file=sys.stderr)
        return 1
    return 0


if __name__ == '__main__':
    sys.exit(main())
