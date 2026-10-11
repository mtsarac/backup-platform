#!/usr/bin/env python3
"""Controlled fixture for the M2 quota browser smoke (scripts/frontend/browser-smoke-env.sh stack).

It does with the real HTTP APIs only what the browser smoke cannot reasonably do by hand, and nothing in the database:

  setup    PlatformAdmin creates a tenant and a tenant Admin user; the tenant Admin creates an enrollment token; a protocol-only agent
           client enrolls with it; a Queue-overlap backup job is created. Prints a JSON document (also written to --state, mode 0600).
  publish  Plays the enrolled agent for ONE run: claims the next Pending run, starts it and completes it with a synthetic Full artifact of
           --bytes bytes (one verified replica). No SQL Server is involved and no file is written anywhere; the backend only records
           metadata, which is exactly what the quota accounting reads.
  quota    Prints the tenant's own GET /storage-quota view (what the tenant UI of the next slice will read).

The agent client is a throwaway protocol client for this smoke; it is not a production agent or provider.
Secrets are read from / written to owner-only (0600) files and are never printed.
"""
import argparse
import hashlib
import http.cookiejar
import json
import os
import ssl
import stat
import sys
import urllib.error
import urllib.request
import uuid
from datetime import datetime, timedelta, timezone

PROTOCOL_HEADER = 'X-Backup-Agent-Protocol'


class FixtureFailure(Exception):
    """Only fixed, secret-free messages constructed by this script."""


def read_private(path):
    fd = os.open(path, os.O_RDONLY | os.O_NOFOLLOW)
    with os.fdopen(fd) as handle:
        info = os.fstat(handle.fileno())
        if not stat.S_ISREG(info.st_mode) or info.st_uid != os.getuid() or stat.S_IMODE(info.st_mode) != 0o600:
            raise FixtureFailure('Private files must be current-owner regular files with mode 0600.')
        return json.load(handle)


def write_private(path, document):
    fd = os.open(path, os.O_WRONLY | os.O_CREAT | os.O_TRUNC | os.O_NOFOLLOW, 0o600)
    with os.fdopen(fd, 'w') as handle:
        json.dump(document, handle)


class Api:
    def __init__(self, server, certificate):
        context = ssl.create_default_context(cafile=certificate)
        self.origin = server.rstrip('/')
        self.cookies = http.cookiejar.CookieJar()
        self.opener = urllib.request.build_opener(urllib.request.HTTPCookieProcessor(self.cookies), urllib.request.HTTPSHandler(context=context))
        self.csrf = None

    def call(self, method, path, payload=None, headers=None, expect=(200, 201, 204)):
        request_headers = {'Accept': 'application/json', **(headers or {})}
        data = None
        if payload is not None:
            request_headers['Content-Type'] = 'application/json'
            data = json.dumps(payload).encode()
        if method != 'GET' and 'Authorization' not in request_headers:
            request_headers['X-CSRF-TOKEN'] = self.antiforgery()
        request = urllib.request.Request(self.origin + path, data=data, headers=request_headers, method=method)
        try:
            with self.opener.open(request, timeout=20) as response:
                status, body = response.status, response.read()
        except urllib.error.HTTPError as error:
            status, body = error.code, error.read()
        document = json.loads(body) if body else None
        if status not in expect:
            code = document.get('error') if isinstance(document, dict) else None
            raise FixtureFailure(f'{method} {path.split("?")[0]} failed with HTTP {status}' + (f' ({code})' if code else ''))
        return status, document

    def antiforgery(self):
        if self.csrf is None:
            _, document = self.call('GET', '/api/auth/antiforgery')
            self.csrf = document['requestToken']
        return self.csrf

    def login(self, login, password):
        self.call('POST', '/api/auth/login', {'loginIdentifier': login, 'password': password})
        self.csrf = None


def agent_headers(agent):
    return {'Authorization': f'BackupAgent {uuid.UUID(agent["credentialId"]).hex}.{agent["secret"]}', PROTOCOL_HEADER: '1.0'}


def now_iso(delta=timedelta()):
    return (datetime.now(timezone.utc) + delta).strftime('%Y-%m-%dT%H:%M:%S.%fZ')


def setup(options):
    admin_credentials = read_private(options.credentials_file)
    platform = Api(options.server, options.certificate)
    platform.login(admin_credentials['login'], admin_credentials['password'])
    suffix = uuid.uuid4().hex[:8]
    _, tenant = platform.call('POST', '/api/platform/tenants', {'name': f'Quota smoke {suffix}'})
    tenant_id = tenant['tenantId']
    login = f'quota.smoke.{suffix}'
    password = f'Smoke-{uuid.uuid4().hex[:16]}!'
    _, user = platform.call('POST', '/api/platform/users', {'loginIdentifier': login, 'displayName': 'Quota Smoke Admin', 'password': password})
    platform.call('POST', f'/api/platform/tenants/{tenant_id}/memberships', {'userId': user['userId'], 'role': 'Admin'})
    platform.call('PUT', f'/api/platform/tenants/{tenant_id}/agent-enrollment', {'enabled': True})

    tenant_admin = Api(options.server, options.certificate)
    tenant_admin.login(login, password)
    _, token = tenant_admin.call('POST', f'/api/tenants/{tenant_id}/agent-enrollment-tokens', {})
    _, enrolled = Api(options.server, options.certificate).call('POST', '/api/agent/enroll', {
        'enrollmentToken': token['token'], 'installationId': str(uuid.uuid4()), 'agentVersion': '1.2.3', 'protocolVersion': '1.0',
        'platform': 'linux', 'hostName': 'quota-smoke-host', 'capabilities': ['backup.file-system', 'backup.quota-bounded-upload']})
    agent = {'agentId': enrolled['agentId'], 'credentialId': enrolled['credential']['credentialId'], 'secret': enrolled['credential']['secret']}
    reports = [{'identifier': key, 'support': 'Supported'} for key in ('backup.file-system', 'backup.quota-bounded-upload')]
    canonical = ''.join(f'{len(report["identifier"])}:{report["identifier"]};9:Supported;0;\n' for report in reports)
    tenant_admin.call('PUT', '/api/agent/capabilities',
                      {'revision': 'c1-' + hashlib.sha256(canonical.encode()).hexdigest()[:48], 'capabilities': reports}, agent_headers(agent))
    _, job = tenant_admin.call('POST', f'/api/tenants/{tenant_id}/backup-jobs', {
        'name': 'Quota smoke backup', 'agentId': agent['agentId'], 'overlapPolicy': 'Queue',
        'configuration': {'engine': 'mssql', 'database': 'master', 'host': 'sql-server', 'integratedSecurity': True, 'storageExecutionLocation': 'Backend'}})
    state = {'tenantId': tenant_id, 'tenantName': tenant['name'], 'tenantAdmin': {'login': login, 'password': password}, 'agent': agent, 'jobId': job['id']}
    write_private(options.state, state)
    print(json.dumps({'tenantId': tenant_id, 'tenantName': tenant['name'], 'tenantAdminLogin': login, 'jobId': job['id'], 'state': options.state}))


def publish(options):
    state = read_private(options.state)
    agent = state['agent']
    api = Api(options.server, options.certificate)
    _, poll = api.call('GET', '/api/agent/tasks', headers=agent_headers(agent))
    tasks = poll.get('tasks', []) if poll else []
    if not tasks:
        raise FixtureFailure('There is no pending run to complete; start a run in the UI first.')
    backup = tasks[0]['backup']
    if backup.get('maxArtifactBytes') is not None and options.bytes > backup['maxArtifactBytes']:
        raise FixtureFailure('Synthetic artifact exceeds the hard-quota bound; choose a permitted size.')
    attempt = backup['attemptId']
    lease = backup['leaseToken']
    api.call('POST', f'/api/agent/tasks/{attempt}/start', {'leaseToken': lease, 'softwareVersion': '1.2.3', 'protocolVersion': '1.0', 'platform': 'linux'}, agent_headers(agent))
    created = now_iso()
    checksum = 'b' * 64
    api.call('POST', f'/api/agent/tasks/{attempt}/complete', {
        'leaseToken': lease, 'status': 'Succeeded',
        'artifact': {
            'sizeBytes': options.bytes, 'sourceChecksum': 'a' * 64, 'artifactChecksum': checksum, 'checksumAlgorithm': 'sha256', 'createdAt': created,
            'sourceMetadataJson': json.dumps({'engine': 'mssql', 'databaseName': 'master', 'backupType': 'Full', 'backupStartedAt': now_iso(timedelta(seconds=-10)),
                                              'backupFinishedAt': now_iso(timedelta(seconds=-5)), 'backupVerification': {'state': 'Passed', 'method': 'VerifyOnly'}}),
            'appliedOptionsJson': json.dumps({'copyOnly': True, 'checksum': True, 'compression': True}),
            'replicas': [{'storageProvider': 'local', 'storageTarget': 'default', 'key': f'{attempt.replace("-", "")}/backup.bak', 'sizeBytes': options.bytes,
                          'checksum': checksum, 'checksumAlgorithm': 'sha256', 'verificationState': 'Verified', 'verificationMethod': 'ReadBack',
                          'createdAt': created, 'verifiedAt': created}]}},
        agent_headers(agent))
    print(json.dumps({'runId': backup['runId'], 'sizeBytes': options.bytes}))


def quota(options):
    state = read_private(options.state)
    api = Api(options.server, options.certificate)
    api.login(state['tenantAdmin']['login'], state['tenantAdmin']['password'])
    _, view = api.call('GET', f'/api/tenants/{state["tenantId"]}/storage-quota')
    print(json.dumps(view))


def main():
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument('--server', required=True, help='HTTPS origin of the smoke API (SMOKE_API_URL)')
    parser.add_argument('--certificate', required=True, help='The disposable smoke certificate (SMOKE_CERTIFICATE_FILE); never a TLS bypass')
    commands = parser.add_subparsers(dest='command', required=True)
    first = commands.add_parser('setup')
    first.add_argument('--credentials-file', required=True, help='SMOKE_CREDENTIALS_FILE (PlatformAdmin login, password)')
    first.add_argument('--state', required=True, help='Where to write the 0600 fixture state')
    second = commands.add_parser('publish')
    second.add_argument('--state', required=True)
    second.add_argument('--bytes', type=int, required=True)
    third = commands.add_parser('quota')
    third.add_argument('--state', required=True)
    options = parser.parse_args()
    try:
        {'setup': setup, 'publish': publish, 'quota': quota}[options.command](options)
    except FixtureFailure as failure:
        print(f'FAIL: {failure}', file=sys.stderr)
        return 1
    return 0


if __name__ == '__main__':
    sys.exit(main())
