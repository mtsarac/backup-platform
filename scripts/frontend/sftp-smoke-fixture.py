#!/usr/bin/env python3
"""Real SFTP API lifecycle; credentials are read from owner-only runtime state."""
import argparse
import hashlib
import importlib.util
import json
import sys
import time
import uuid
from pathlib import Path

spec = importlib.util.spec_from_file_location('connections_fixture', Path(__file__).with_name('storage-connections-smoke-fixture.py'))
shared = importlib.util.module_from_spec(spec)
spec.loader.exec_module(shared)
require = shared.require


def connection_test(owner, route, expected):
    _, outcome = owner.call('POST', route + '/test', {})
    require(outcome['test']['status'] == expected, 'SFTP roundtrip returned an unexpected status.')
    return outcome


def setup(options):
    state, owner, _ = shared.clients(options)
    config = shared.read_private(options.sftp_file)
    route = f'/api/tenants/{state["tenantId"]}/storage-connections'
    _, descriptors = owner.call('GET', f'/api/tenants/{state["tenantId"]}/storage-providers')
    descriptor = next(item for item in descriptors['items'] if item['providerType'] == 'sftp')
    require(descriptor['availability'] == 'Available' and descriptor['tenantCreatable'], 'SFTP must be tenant-creatable and available.')
    request = {'providerType': 'sftp', 'name': 'Disposable SFTP', 'configuration': config['configuration'], 'secret': {'password': config['password']}}
    _, view = owner.call('POST', route, request, expect=(200, 201))
    require(view['hasSecret'] and view['quotaScope'] == 'tenant', 'SFTP secret/quota metadata is incorrect.')
    require(config['password'] not in json.dumps(view), 'Connection response exposed a credential.')
    state['sftpConnectionId'] = view['id']
    shared.write_private(options.state, state)
    connection_test(owner, route + '/' + view['id'], 'Succeeded')
    bad = dict(config['configuration'])
    bad['hostKeyFingerprint'] = 'SHA256:' + 'A' * 43
    _, wrong = owner.call('POST', route, {'providerType': 'sftp', 'name': 'Wrong host pin', 'configuration': bad, 'secret': {'password': config['password']}}, expect=(200, 201))
    wrong_test = connection_test(owner, route + '/' + wrong['id'], 'Failed')
    code = wrong_test.get('test', {}).get('code')
    require(code == 'host_key_mismatch', f"Wrong host fingerprint must fail closed with host_key_mismatch (got {code}).")
    owner.call('DELETE', route + '/' + wrong['id'], expect=(200, 204))
    # Exercise the private-key path with an independently generated, passphrase-protected key.
    key_config = dict(config['configuration'])
    key_config['authenticationType'] = 'private-key'
    _, key = owner.call('POST', route, {'providerType': 'sftp', 'name': 'Disposable key authentication', 'configuration': key_config,
                                      'secret': {'privateKey': config['privateKey'], 'passphrase': config['passphrase']}}, expect=(200, 201))
    connection_test(owner, route + '/' + key['id'], 'Succeeded')
    owner.call('DELETE', route + '/' + key['id'], expect=(200, 204))
    print(json.dumps({'connectionTest': 'Succeeded', 'wrongHostKey': 'Failed', 'privateKeyTest': 'Succeeded'}))


def inspect(options):
    state, owner, _ = shared.clients(options)
    artifact = state['artifacts']['sftp']
    route = f'/api/tenants/{state["tenantId"]}'
    _, view = owner.call('GET', route + '/backup-artifacts/' + artifact['id'])
    replicas = view['replicas']
    require(len(replicas) == 1 and replicas[0]['status'] == 'Available' and replicas[0]['usable'], 'Real SFTP replica must be usable and Available.')
    require(replicas[0]['storageConnectionId'] == state['sftpConnectionId'], 'Replica is bound to the wrong connection.')
    _, quota = owner.call('GET', route + '/storage-quota')
    require(quota['usedBytes'] == artifact['sizeBytes'], 'Tenant SFTP quota must equal actual artifact bytes.')
    files = [item for item in Path(options.remote_root).rglob('*') if item.is_file()]
    require(len(files) == 1, 'Disposable SFTP root must contain exactly the published real artifact, without leaked probes or partials.')
    require(files[0].stat().st_size == artifact['sizeBytes'], 'Remote SFTP object length differs from artifact.')
    digest = hashlib.sha256()
    with files[0].open('rb') as handle:
        while chunk := handle.read(128 * 1024):
            digest.update(chunk)
    require(digest.hexdigest().lower() == artifact['checksum'].lower(), 'Remote SFTP SHA-256 differs from artifact.')
    state['sftpRemoteFile'] = str(files[0])
    shared.write_private(options.state, state)
    print(json.dumps({'remoteMatchesArtifact': True, 'replicaConnectionMatches': True, 'usedBytes': quota['usedBytes']}))


def access(options):
    state, owner, platform = shared.clients(options)
    base = f'/api/tenants/{state["tenantId"]}'
    connection = base + '/storage-connections/' + state['sftpConnectionId']
    artifact = state['artifacts']['sftp']
    if options.mode == 'disable':
        owner.call('POST', connection + '/disable', {})
    elif options.mode == 'lock':
        owner.call('POST', connection + '/enable', {})
        platform.call('POST', '/api/platform/storage-connections/' + state['sftpConnectionId'] + '/disable', {})
        owner.call('POST', connection + '/enable', {}, expect=(409,))
    elif options.mode == 'reject':
        platform.call('POST', '/api/platform/storage-connections/' + state['sftpConnectionId'] + '/enable', {})
        owner.call('POST', connection + '/enable', {})
        _, rejected = owner.call('PUT', connection + '/secret', {'values': {'password': 'Invalid-' + uuid.uuid4().hex}}, expect=(409,))
        require(rejected.get('code') == 'authentication_failed' or rejected.get('error') == 'authentication_failed', 'Invalid rotation must fail with authentication_failed.')
        connection_test(owner, connection, 'Succeeded')
        print(json.dumps({'candidateWrongCredentialRejected': True, 'oldCredentialStillWorks': True}))
        return
    else:
        platform.call('POST', '/api/platform/storage-connections/' + state['sftpConnectionId'] + '/enable', {})
        owner.call('POST', connection + '/enable', {})
        config = shared.read_private(options.sftp_file)
        owner.call('PUT', connection + '/secret', {'values': {'password': config['password']}}, expect=(200,))
        connection_test(owner, connection, 'Succeeded')
        print(json.dumps({'validatedSecretReplacement': True}))
        return
    _, denied = owner.call('POST', base + '/backup-jobs/' + artifact['jobId'] + '/runs', {},
                           headers={'Idempotency-Key': 'sftp-denied-' + uuid.uuid4().hex}, expect=(409,))
    require(denied.get('error') == 'admission_denied', 'Disabled SFTP must deny new backup admission.')
    _, detail = owner.call('GET', base + '/backup-artifacts/' + artifact['id'])
    require(detail['replicas'][0]['status'] == 'Available' and detail['replicas'][0]['usable'], 'Disable/lock must not make stored replicas unusable.')
    print(json.dumps({'mode': options.mode, 'newRunDenied': True, 'storedReplicaUsable': True}))


def delete(options):
    state, owner, _ = shared.clients(options)
    artifact = state['artifacts']['sftp']
    base = f'/api/tenants/{state["tenantId"]}'
    owner.call('DELETE', base + '/backup-artifacts/' + artifact['id'], expect=(202,))
    deadline = time.monotonic() + options.timeout
    while time.monotonic() < deadline:
        _, detail = owner.call('GET', base + '/backup-artifacts/' + artifact['id'])
        if all(replica['status'] == 'Deleted' for replica in detail['replicas']):
            require(detail['replicas'], 'Retention response must include the deleted replica.')
            require(not Path(state['sftpRemoteFile']).exists(), 'Retention recorded deletion before the remote file disappeared.')
            require(not any(item.is_file() for item in Path(options.remote_root).rglob('*')), 'Retention left remote data or partial files.')
            _, quota = owner.call('GET', base + '/storage-quota')
            require(quota['usedBytes'] == 0, 'Retention must release all owned SFTP quota.')
            print(json.dumps({'retentionReplica': 'Deleted', 'remoteAbsent': True, 'usedBytes': 0}))
            return
        time.sleep(1)
    raise shared.Fixture('SFTP retention deletion timed out.')


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    for name in ('server', 'certificate', 'state', 'credentials-file', 'sftp-file'):
        parser.add_argument('--' + name, required=True)
    parser.add_argument('--remote-root', required=True)
    commands = parser.add_subparsers(dest='command', required=True)
    commands.add_parser('setup')
    commands.add_parser('inspect')
    access_parser = commands.add_parser('access')
    access_parser.add_argument('--mode', choices=('disable', 'lock', 'reject', 'rotate'), required=True)
    commands.add_parser('delete').add_argument('--timeout', type=int, default=240)
    options = parser.parse_args()
    try:
        {'setup': setup, 'inspect': inspect, 'access': access, 'delete': delete}[options.command](options)
    except (shared.Fixture, OSError, ValueError, KeyError, StopIteration) as failure:
        print('FAIL: ' + (str(failure) if isinstance(failure, shared.Fixture) else 'SFTP smoke failed (details withheld).'), file=sys.stderr)
        return 1
    return 0


if __name__ == '__main__':
    sys.exit(main())
