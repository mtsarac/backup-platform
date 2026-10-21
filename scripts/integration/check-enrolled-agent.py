#!/usr/bin/env python3
"""Verify enrollment/auth/heartbeat/capability sync through the real tenant API."""
import argparse
import http.cookiejar
import json
import os
import ssl
import stat
import sys
import time
import urllib.error
import urllib.parse
import urllib.request
import uuid
from datetime import datetime


class AcceptanceFailure(Exception):
    """Only fixed, secret-free messages constructed by this verifier."""


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument('--server', required=True)
    parser.add_argument('--credentials-file', required=True, help='Owner-only runtime JSON: login, password')
    parser.add_argument('--tenant-id', required=True)
    parser.add_argument('--token-id', required=True)
    parser.add_argument('--expected-agent-id')
    parser.add_argument('--heartbeat-after', help='Require a fresh heartbeat after this ISO timestamp; use for restart proof')
    parser.add_argument('--certificate', help='Explicit trusted acceptance certificate, never a TLS bypass')
    parser.add_argument('--timeout', type=int, default=90)
    options = parser.parse_args()
    uri = urllib.parse.urlsplit(options.server)
    if uri.scheme != 'https' or not uri.hostname or uri.username or uri.password or uri.query or uri.fragment or uri.path not in ('', '/'):
        raise AcceptanceFailure('Server must be an HTTPS origin without credentials, path, query or fragment.')
    if not 1 <= options.timeout <= 600:
        raise AcceptanceFailure('Timeout must be 1-600 seconds.')
    tenant = str(uuid.UUID(options.tenant_id))
    token = str(uuid.UUID(options.token_id))
    expected = str(uuid.UUID(options.expected_agent_id)) if options.expected_agent_id else None
    after = datetime.fromisoformat(options.heartbeat_after) if options.heartbeat_after else None
    fd = os.open(options.credentials_file, os.O_RDONLY | os.O_NOFOLLOW)
    with os.fdopen(fd) as handle:
        info = os.fstat(handle.fileno())
        if not stat.S_ISREG(info.st_mode) or info.st_uid != os.getuid() or stat.S_IMODE(info.st_mode) != 0o600:
            raise AcceptanceFailure('Credentials must be a current-owner regular file with mode 0600.')
        credentials = json.load(handle)
    context = ssl.create_default_context(cafile=options.certificate)
    opener = urllib.request.build_opener(urllib.request.HTTPCookieProcessor(http.cookiejar.CookieJar()), urllib.request.HTTPSHandler(context=context))
    origin = options.server.rstrip('/')

    def request(path, payload=None, antiforgery=None):
        headers = {'Accept': 'application/json'}
        data = None
        if payload is not None:
            headers['Content-Type'] = 'application/json'
            headers['X-CSRF-TOKEN'] = antiforgery
            data = json.dumps(payload).encode()
        with opener.open(urllib.request.Request(origin + path, data=data, headers=headers), timeout=10) as response:
            body = response.read()
            return json.loads(body) if body else None

    csrf = request('/api/auth/antiforgery')['requestToken']
    request('/api/auth/login', {'loginIdentifier': credentials['login'], 'password': credentials['password']}, csrf)
    credentials.clear()
    deadline = time.monotonic() + options.timeout
    while True:
        status = request(f'/api/tenants/{tenant}/agent-enrollment-tokens/{token}')
        if status['state'] in ('Expired', 'Revoked'):
            raise AcceptanceFailure('The token expired or was revoked without enrollment.')
        agent_id = status.get('agentId') if status['state'] == 'Consumed' else None
        if agent_id:
            if expected and agent_id != expected:
                raise AcceptanceFailure('Agent identity changed; expected AgentId was not preserved.')
            agent = request(f'/api/tenants/{tenant}/agents/{agent_id}')
            fresh = agent.get('lastHeartbeatAt') and (after is None or datetime.fromisoformat(agent['lastHeartbeatAt']) > after)
            if (agent['status'] == 'Active' and agent['connectivity'] == 'online' and fresh
                    and agent.get('capabilitiesReportedAt') and agent.get('capabilitiesRevision')
                    and agent.get('capabilities') and agent['capabilityRevision'] == agent['capabilitiesRevision']):
                print(json.dumps({
                    'agentId': agent_id, 'name': agent['hostName'], 'platform': agent['platform'],
                    'connectivity': agent['connectivity'], 'lastHeartbeatAt': agent['lastHeartbeatAt'],
                    'capabilityCount': len(agent['capabilities']), 'capabilitiesReportedAt': agent['capabilitiesReportedAt'],
                    'agentVersion': agent['agentVersion'], 'identityPreserved': expected is not None
                }))
                return
        if time.monotonic() >= deadline:
            raise AcceptanceFailure('Timed out waiting for real agent Online heartbeat and matching capability snapshot.')
        time.sleep(min(5, max(0, deadline - time.monotonic())))


if __name__ == '__main__':
    try:
        main()
    except AcceptanceFailure as error:
        print(f'Enrollment acceptance failed: {error}', file=sys.stderr)
        sys.exit(1)
    except urllib.error.HTTPError as error:
        print(f'Enrollment acceptance failed: HTTP {error.code}; check authentication and tenant authorization.', file=sys.stderr)
        sys.exit(1)
    except (OSError, ValueError, KeyError, TypeError, urllib.error.URLError, ssl.SSLError):
        # Do not echo exception payloads: URLs, passwords and token data may be present.
        print('Enrollment acceptance failed: check HTTPS trust, backend reachability, owner-only credentials, token state and agent service. No secrets displayed.', file=sys.stderr)
        sys.exit(1)
