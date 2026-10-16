#!/usr/bin/env python3
"""Driver for the Database Auto Discovery acceptance against the isolated stack of scripts/dev/discovery-stack.sh.
Talks only to the real tenant API (as the tenant Owner). Never prints credentials.

  snap                         agent status and every instance with access/reachability/last change
  wait [--min-new N]           wait until the agent is idle and a newer scan was reported
  rescan                       POST the manual rescan of the stack's agent, then wait
  bind KEY USER [--password-env VAR] [--integrated]  store the database login (password read from env VAR, default BP_DISCOVERY_DB_PASSWORD) on the instance whose key contains KEY, then verify and wait
  verify KEY                   verify the bound credential of the instance
  dbs KEY                      list the discovered databases of the instance (with jobs)
  raw METHOD PATH [JSON]       tenant-relative call, prints status and body
"""
import argparse
import importlib.util
import json
import os
from pathlib import Path
import sys
import time

here = Path(__file__).parent
spec = importlib.util.spec_from_file_location('quota_fixture', here.parent / 'frontend' / 'quota-smoke-fixture.py')
shared = importlib.util.module_from_spec(spec)
spec.loader.exec_module(shared)
Api, FixtureFailure = shared.Api, shared.FixtureFailure
bootstrap_spec = importlib.util.spec_from_file_location('bootstrap', here / 'bootstrap.py')
bootstrap = importlib.util.module_from_spec(bootstrap_spec)
bootstrap_spec.loader.exec_module(bootstrap)

HOME = Path(os.environ.get('BP_DISC_HOME', '/tmp/bp-discovery-acceptance'))
SERVER = 'https://127.0.0.1:' + os.environ.get('BP_DISC_API_PORT', '7391')


def session(prefix='tenant_owner'):
    accounts = bootstrap.read_accounts(HOME / 'accounts.txt')
    api = Api(SERVER, str(HOME / 'cert.pem'))
    api.login(accounts[prefix + '_login'], accounts[prefix + '_password'])
    _, memberships = api.call('GET', '/api/me/tenants')
    items = memberships if isinstance(memberships, list) else memberships.get('items', memberships)
    first = items[0]
    tenant_id = first.get('tenantId') or first['tenant']['tenantId']
    return api, tenant_id


AGENT_NAME = os.environ.get('DISC_AGENT', 'linux')


def agent_id(api, tenant):
    _, listing = api.call('GET', f'/api/tenants/{tenant}/agents')
    items = listing if isinstance(listing, list) else listing['items']
    return next(item['agentId'] for item in items if AGENT_NAME in (item.get('hostName') or ''))


def get(api, tenant, path, expect=(200,)):
    return api.call('GET', f'/api/tenants/{tenant}/discovery/{path}', expect=expect)[1]


def snapshot(api, tenant, include_missing=True):
    chosen = agent_id(api, tenant)
    agents = [item for item in get(api, tenant, 'agents')['items'] if item['agentId'] == chosen]
    instances = get(api, tenant, f'instances?agentId={chosen}&includeMissing=' + str(include_missing).lower())['items']
    return agents, instances


def show(api, tenant):
    agents, instances = snapshot(api, tenant)
    for agent in agents:
        sources = ','.join(f"{s['source']}={s['status']}" + (f"/{s['reasonCode']}" if s['reasonCode'] else '') for s in agent['sources'])
        print(f"AGENT {agent['agentId'][:8]} state={agent['scanState']} reason={agent['scanReason']} outcome={agent['lastOutcome']} failure={agent['lastFailureCode']} "
              f"last={agent['lastScanAt']} next={agent['nextScanDueAt']} instances={agent['instanceCount']} sources[{sources}]")
    for item in sorted(instances, key=lambda x: x['instanceKey']):
        change = item['lastChange']['kind'] if item['lastChange'] else '-'
        print(f"  {item['instanceKey']:<42} {item['engine']:<8} {item['source']:<8} {item['host']}:{item['port']} {item['presence']:<7} reach={item['reachability']:<11} "
              f"access={item['access']:<19} reason={item['accessReasonCode']} full={item['fullBackup']} dbs={item['databaseCounts']['total']}/{item['databaseCounts']['user']}/{item['databaseCounts']['backupEligible']} "
              f"cred={item['credential']['credentialReference'] or ('integrated' if item['credential']['integratedSecurity'] else '-')} change={change} ver={item['version']}")
    return agents, instances


def find(instances, key):
    matches = [item for item in instances if key in item['instanceKey']]
    if len(matches) != 1:
        raise FixtureFailure(f'{len(matches)} instances match {key!r}: {[m["instanceKey"] for m in matches]}')
    return matches[0]


def wait_scan(api, tenant, previous, timeout=180):
    deadline = time.time() + timeout
    while time.time() < deadline:
        agent = next(item for item in get(api, tenant, 'agents')['items'] if item['agentId'] == agent_id(api, tenant))
        if agent['scanState'] == 'Idle' and agent['lastScanAt'] and agent['lastScanAt'] != previous:
            return agent
        time.sleep(2)
    raise FixtureFailure('scan did not finish in time')


def last_scan(api, tenant):
    items = [item for item in get(api, tenant, 'agents')['items'] if item['agentId'] == agent_id(api, tenant)]
    return items[0]['lastScanAt'] if items else None


def main():
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    sub = parser.add_subparsers(dest='command', required=True)
    sub.add_parser('snap')
    sub.add_parser('wait')
    sub.add_parser('rescan')
    bind = sub.add_parser('bind'); bind.add_argument('key'); bind.add_argument('user'); bind.add_argument('--password-env', default='BP_DISCOVERY_DB_PASSWORD'); bind.add_argument('--integrated', action='store_true')
    bind.add_argument('--trust', action='store_true'); bind.add_argument('--no-encrypt', action='store_true')
    verify = sub.add_parser('verify'); verify.add_argument('key')
    dbs = sub.add_parser('dbs'); dbs.add_argument('key')
    raw = sub.add_parser('raw'); raw.add_argument('method'); raw.add_argument('path'); raw.add_argument('body', nargs='?')
    options = parser.parse_args()

    api, tenant = session()
    if options.command == 'snap':
        show(api, tenant)
    elif options.command == 'wait':
        wait_scan(api, tenant, None if False else '1970-01-01T00:00:00Z'); show(api, tenant)
    elif options.command == 'rescan':
        previous = last_scan(api, tenant)
        status, body = api.call('POST', f'/api/tenants/{tenant}/discovery/agents/{agent_id(api, tenant)}/rescan', expect=(202, 409, 429))
        print('rescan ->', status, json.dumps(body) if status != 202 else body['scanState'])
        if status == 202:
            wait_scan(api, tenant, previous); show(api, tenant)
    elif options.command in ('bind', 'verify'):
        _, instances = snapshot(api, tenant)
        item = find(instances, options.key)
        if options.command == 'bind':
            body = {'credentialReference': None, 'databaseLogin': None if options.integrated else {'userName': options.user, 'password': os.environ[options.password_env]}, 'integratedSecurity': options.integrated, 'encrypt': not options.no_encrypt, 'trustServerCertificate': True}
            status, _ = api.call('PUT', f'/api/tenants/{tenant}/discovery/instances/{item["id"]}/credential', body, expect=(200, 400))
            print('bind ->', status)
        previous = last_scan(api, tenant)
        status, body = api.call('POST', f'/api/tenants/{tenant}/discovery/instances/{item["id"]}/verify', expect=(202, 409, 429))
        print('verify ->', status, '' if status == 202 else json.dumps(body))
        if status == 202:
            wait_scan(api, tenant, previous); show(api, tenant)
    elif options.command == 'dbs':
        _, instances = snapshot(api, tenant)
        detail = get(api, tenant, 'instances/' + find(instances, options.key)['id'])
        print(detail['displayName'], 'access', detail['access'], 'checked', detail['databasesCheckedAt'])
        for database in detail['databases']:
            print(f"  {database['name']:<24} system={database['isSystem']!s:<5} {database['state']:<8} full={database['fullBackup']:<11} {database['fullBackupReasonCode']} {database['presence']} jobs={[j['name'] for j in database['jobs']]}")
    elif options.command == 'raw':
        body = json.loads(options.body) if options.body else None
        status, document = api.call(options.method, f'/api/tenants/{tenant}/{options.path}', body, expect=tuple(range(200, 600)))
        print(status, json.dumps(document)[:2000])


if __name__ == '__main__':
    try:
        main()
    except FixtureFailure as failure:
        print('FAIL:', failure, file=sys.stderr)
        sys.exit(1)
