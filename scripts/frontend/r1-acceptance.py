#!/usr/bin/env python3
"""R1 isolated restore verification acceptance against the owned smoke stack (scripts/frontend/run-r1-smoke.sh).

Everything runs through the real product: the HTTP API, the real Linux Agents (SQL Server and PostgreSQL) on disposable database containers, the
real central storage and, for process loss, SIGKILL of the agent and a restart of the API. Only databases created by this run (bp_r1_* sources and
the platform's own bp_verify_* disposable databases of the run's tenant) are ever created or dropped. Secrets stay in the environment or in 0600
files; nothing secret is printed. One PASS/FAIL JSON line per scenario.

Subcommands: tokens (enrollment tokens for the run's agents), run (the scenarios).
"""
import argparse
import importlib.util
import json
import os
from pathlib import Path
import re
import signal
import subprocess
import sys
import time
import uuid

HERE = Path(__file__).resolve().parent
spec = importlib.util.spec_from_file_location('quota_fixture', HERE / 'quota-smoke-fixture.py')
shared = importlib.util.module_from_spec(spec)
spec.loader.exec_module(shared)
Api, FixtureFailure = shared.Api, shared.FixtureFailure

TERMINAL = ('Succeeded', 'Failed', 'Cancelled', 'AgentLost')
VERIFY_NAME = re.compile(r'bp_verify_[0-9a-f]{32}')
results = []


def record(name, ok, **detail):
    results.append((name, ok))
    print(json.dumps({'scenario': name, 'result': 'PASS' if ok else 'FAIL', **detail}, default=str), flush=True)


def scenario(name):
    def decorate(function):
        function.scenario_name = name
        return function
    return decorate


class Run:
    def __init__(self, options):
        self.options = options
        self.state = shared.read_private(options.state)
        self.api = Api(options.server, options.certificate)
        self.api.login(self.state['owner']['login'], self.state['owner']['password'])
        self.tenant = '/api/tenants/' + self.state['tenantId']
        self.work = Path(options.work)
        self.agents = {}
        self.artifacts = {}
        self.checksums = {}
        self.paths = {}

    # ---- servers (only the disposable containers of this run) ----

    def mssql(self, script, timeout=120):
        environment = dict(os.environ, SQLCMDPASSWORD=os.environ['BACKUP_PLATFORM_TEST_MSSQL_PASSWORD'])
        done = subprocess.run(['docker', 'exec', '-i', '-e', 'SQLCMDPASSWORD', self.options.mssql_container, '/opt/mssql-tools18/bin/sqlcmd', '-S', 'localhost', '-U',
                               os.environ['BACKUP_PLATFORM_TEST_MSSQL_USER'], '-C', '-b', '-h', '-1', '-W', '-t', str(timeout)],
                              input=script, text=True, capture_output=True, env=environment, timeout=timeout + 30)
        if done.returncode:
            raise FixtureFailure('SQL Server command failed: ' + (done.stdout + done.stderr).replace('\n', ' ')[-300:])
        return [line.strip() for line in done.stdout.splitlines() if line.strip()]

    def pg(self, sql, database='postgres', timeout=120):
        environment = dict(os.environ, PGPASSWORD=os.environ['BACKUP_PLATFORM_TEST_POSTGRES_PASSWORD'])
        done = subprocess.run(['docker', 'exec', '-i', '-e', 'PGPASSWORD', self.options.pg_container, 'psql', '-U', 'postgres', '-d', database, '-X', '-A', '-t', '-v', 'ON_ERROR_STOP=1'],
                              input=sql, text=True, capture_output=True, env=environment, timeout=timeout)
        if done.returncode:
            raise FixtureFailure('PostgreSQL command failed: ' + (done.stdout + done.stderr).replace('\n', ' ')[-300:])
        return [line.strip() for line in done.stdout.splitlines() if line.strip()]

    def leftovers(self, engine):
        if engine == 'mssql':
            return [name for name in self.mssql("SET NOCOUNT ON; SELECT name FROM sys.databases WHERE name LIKE 'bp[_]verify[_]%';\nGO\n") if VERIFY_NAME.fullmatch(name)]
        return [name for name in self.pg("SELECT datname FROM pg_database WHERE datname LIKE 'bp\\_verify\\_%'") if VERIFY_NAME.fullmatch(name)]

    # ---- fixtures ----

    def agent_id(self, name):
        deadline = time.monotonic() + 120
        while time.monotonic() < deadline:
            _, document = self.api.call('GET', self.tenant + '/agents')
            for item in document if isinstance(document, list) else document.get('items', []):
                if item.get('hostName') == name:
                    return item.get('agentId', item.get('id'))
            time.sleep(1)
        raise FixtureFailure('Enrolled agent not found: ' + name)

    def wait_capability(self, agent_id, capability, timeout=120):
        deadline = time.monotonic() + timeout
        while time.monotonic() < deadline:
            _, agent = self.api.call('GET', f'{self.tenant}/agents/{agent_id}')
            capabilities = agent.get('capabilities') or []
            if isinstance(capabilities, dict):
                capabilities = capabilities.get('capabilities', [])
            if any(isinstance(item, dict) and item.get('identifier') == capability and item.get('support') == 'Supported' for item in capabilities):
                return
            time.sleep(2)
        raise FixtureFailure(f'Agent never reported {capability}; saw {sorted(item.get("identifier") for item in capabilities if isinstance(item, dict))[:40]} status={agent.get("status")} connectivity={agent.get("connectivity")}')

    def backup(self, engine, database, agent_id, name, credential, host=None, port=None, agent_local=False):
        configuration = {'engine': engine, 'host': host or (self.options.mssql_host if engine == 'mssql' else self.options.pg_host),
                         'port': port or (self.options.mssql_port if engine == 'mssql' else self.options.pg_port), 'database': database,
                         'databaseCredentialReference': credential, 'encrypt': engine == 'mssql', 'trustServerCertificate': engine == 'mssql',
                         'stagingReference': 'default', 'storageTarget': 'default', 'storagePrefix': 'r1', 'copyOnly': engine == 'mssql',
                         'checksumPolicy': 'Required', 'compressionPolicy': 'Preferred', 'requiredVerifiedReplicaCount': 1,
                         'destinations': [{'storageProvider': 'local' if agent_local else 'central', 'storageConnectionId': None,
                                           'storageExecutionLocation': 'Agent' if agent_local else 'Backend', 'isPrimary': True}]}
        _, job = self.api.call('POST', self.tenant + '/backup-jobs', {'name': name, 'agentId': agent_id, 'overlapPolicy': 'Skip', 'configuration': configuration})
        _, run = self.api.call('POST', f'{self.tenant}/backup-jobs/{job["id"]}/runs', {}, headers={'Idempotency-Key': 'r1-' + uuid.uuid4().hex}, expect=(200, 201, 202))
        run_id = run.get('id', run.get('run', {}).get('id'))
        deadline = time.monotonic() + 600
        while time.monotonic() < deadline:
            _, detail = self.api.call('GET', f'{self.tenant}/backup-runs/{run_id}')
            if detail['status'] == 'Succeeded':
                artifacts = [artifact for attempt in detail['attempts'] for artifact in attempt['artifacts']]
                if len(artifacts) != 1:
                    raise FixtureFailure('The backup did not publish exactly one artifact.')
                self.checksums[artifacts[0]['id']] = artifacts[0]['artifactChecksum'].lower()
                return artifacts[0]['id']
            if detail['status'] in ('Failed', 'Cancelled', 'AgentLost', 'Skipped'):
                raise FixtureFailure(f'Backup ended as {detail["status"]}.')
            time.sleep(1)
        raise FixtureFailure('Backup timed out.')

    # ---- verification helpers ----

    def target(self, engine, credential, host=None, port=None):
        if engine == 'mssql':
            return {'host': host or self.options.mssql_host, 'port': port or self.options.mssql_port, 'databaseCredentialReference': credential, 'encrypt': True, 'trustServerCertificate': True}
        return {'host': self.options.pg_host, 'port': self.options.pg_port, 'databaseCredentialReference': credential, 'encrypt': False, 'trustServerCertificate': False}

    def verify(self, artifact_id, agent_id, engine, credential, api=None, expect=(200, 201, 202), key=None, host=None, port=None):
        return (api or self.api).call('POST', f'{self.tenant}/backup-artifacts/{artifact_id}/restore-verifications', {'agentId': agent_id, 'target': self.target(engine, credential, host, port)},
                                      headers={'Idempotency-Key': key or 'r1-' + uuid.uuid4().hex}, expect=expect)

    def restore(self, restore_id, api=None):
        return (api or self.api).call('GET', f'{self.tenant}/restores/{restore_id}')[1]

    def wait(self, restore_id, predicate, timeout=300, interval=0.4, label='state'):
        deadline = time.monotonic() + timeout
        last = None
        while time.monotonic() < deadline:
            last = self.restore(restore_id)
            if predicate(last):
                return last
            if last['status'] in TERMINAL and label.startswith('the ') and 'running' in label:
                break
            time.sleep(interval)
        raise FixtureFailure(f'Timed out waiting for {label}; last status {last and last["status"]} stage {last and last["progress"]["stage"]} failure {last and last.get("failureCode")}.')

    def finish(self, restore_id, timeout=300):
        return self.wait(restore_id, lambda view: view['status'] in TERMINAL, timeout, label='the verification to end')

    def wait_cleanup(self, restore_id, state, timeout=300):
        return self.wait(restore_id, lambda view: view['cleanup']['state'] == state, timeout, 1, f'cleanup {state}')

    def agent_pid(self, name):
        return int((self.work / 'agents' / name / 'pid').read_text())

    def kill_agent(self, name):
        os.kill(self.agent_pid(name), signal.SIGKILL)

    def hold(self, name, hold):
        path = self.work / 'agents' / name / 'hold'
        path.touch() if hold else path.unlink(missing_ok=True)

    def restart_api(self):
        port = self.options.server.rsplit(':', 1)[1].rstrip('/')
        found = subprocess.run(['ss', '-ltnpH', f'sport = :{port}'], capture_output=True, text=True).stdout
        pid = int(re.search(r'pid=(\d+)', found).group(1))
        cmdline = Path(f'/proc/{pid}/cmdline').read_bytes().split(b'\0')[:-1]
        environment = dict(item.split(b'=', 1) for item in Path(f'/proc/{pid}/environ').read_bytes().split(b'\0') if b'=' in item)
        cwd = os.readlink(f'/proc/{pid}/cwd')
        (Path(self.options.state).parent / 'api-restart-allowed').touch()
        os.kill(pid, signal.SIGTERM)
        for attempt in range(300):
            if not Path(f'/proc/{pid}').exists():
                break
            if attempt == 200:  # a shutdown that hangs is still a restart: the platform must not depend on a graceful one
                os.kill(pid, signal.SIGKILL)
            time.sleep(0.2)
        log = open(self.work / 'api-restarted.log', 'ab')
        replacement = subprocess.Popen([item.decode() for item in cmdline], cwd=cwd, env={key.decode(): value.decode() for key, value in environment.items()},
                                       stdout=log, stderr=log, start_new_session=True)
        (self.work / 'api.pid').write_text(str(replacement.pid))
        deadline = time.monotonic() + 90
        while time.monotonic() < deadline:
            try:
                self.api.call('GET', '/api/auth/antiforgery')
                self.api.csrf = None
                return
            except Exception:
                time.sleep(0.5)
        raise FixtureFailure('The API did not come back after the restart: ' + '; '.join(line.strip()[:200] for line in (self.work / 'api-restarted.log').read_text(errors='replace').splitlines() if line.strip() and not line.lstrip().startswith('at '))[:900])

    def audit_actions(self, restore_id):
        credentials = shared.read_private(self.options.credentials_file)
        platform = Api(self.options.server, self.options.certificate)
        platform.login(credentials['login'], credentials['password'])
        _, document = platform.call('GET', f'/api/platform/audit?tenantId={self.state["tenantId"]}&pageSize=100')
        return sorted({item['action'] for item in document.get('items', []) if restore_id in json.dumps(item)})


# ---- scenarios ----

def healthy(run, engine):
    artifact, agent = run.artifacts[engine], run.agents[engine]
    credential = 'sql_smoke' if engine == 'mssql' else 'pg_verify'
    before = run.leftovers(engine)
    start = time.monotonic()
    _, created = run.verify(artifact, agent, engine, credential)
    view = run.finish(created['id'])
    duration = time.monotonic() - start
    ok = (view['status'] == 'Succeeded' and view['verificationOutcome'] == 'Restored' and view['targetState'] == 'NotModified' and view['cleanup']['state'] == 'Completed'
          and all(check['status'] == 'Passed' for check in view['result']['verification']['checks']) and view['result']['verification']['cleanup'] == 'Dropped'
          and run.leftovers(engine) == before and VERIFY_NAME.fullmatch(view['databaseName']))
    record(f'{engine}-healthy-artifact-restored', ok, restoreId=view['id'], checks=[check['checkId'] for check in view['result']['verification']['checks']],
           tables=view['result']['verification'].get('userTableCount'), seconds=round(duration, 1), leftovers=len(run.leftovers(engine)))
    return view


def central_replica(run, artifact_id):
    """The stored file of an artifact, found by the SHA-256 the platform recorded for it (storage keys are not exposed by the API)."""
    if artifact_id not in run.paths:
        import hashlib
        for path in Path(run.options.central_root).rglob('*'):
            if path.is_file() and path.stat().st_size > 0:
                digest = hashlib.sha256()
                with path.open('rb') as handle:
                    for chunk in iter(lambda: handle.read(1 << 20), b''):
                        digest.update(chunk)
                for known, checksum in run.checksums.items():
                    if digest.hexdigest() == checksum:
                        run.paths[known] = path
    if artifact_id not in run.paths:
        raise FixtureFailure('Could not locate the central replica file.')
    return run.paths[artifact_id]


def tamper_central(run, artifact_id, mode):
    """Changes the stored replica itself (bit flip / truncate / delete) so the agent meets the real corrupt or missing file."""
    path = central_replica(run, artifact_id)
    backup = path.with_name(path.name + '.r1-keep')
    path.rename(backup)
    if mode == 'missing':
        return path, backup
    data = bytearray(backup.read_bytes())
    if mode == 'bitflip':
        data[len(data) // 2] ^= 0xFF
    elif mode == 'truncate':
        data = data[: len(data) // 2]
    path.write_bytes(data)
    return path, backup


def untamper(path, backup):
    path.unlink(missing_ok=True)
    backup.rename(path)


def corrupt(run, engine, mode, expected_codes):
    artifact, agent = run.artifacts[engine], run.agents[engine]
    credential = 'sql_smoke' if engine == 'mssql' else 'pg_verify'
    before = run.leftovers(engine)
    path, backup = tamper_central(run, artifact, mode)
    try:
        _, created = run.verify(artifact, agent, engine, credential)
        view = run.finish(created['id'])
    finally:
        untamper(path, backup)
    ok = (view['status'] == 'Failed' and view['failureCode'] in expected_codes and view['verificationOutcome'] == 'Failed' and run.leftovers(engine) == before
          and view['cleanup']['state'] == 'Completed')
    record(f'{engine}-{mode}-artifact-failed', ok, failureCode=view['failureCode'], reason=view.get('reason'), cleanup=view['cleanup']['state'])


def credential_failure(run, engine, credential):
    artifact, agent = run.artifacts[engine], run.agents[engine]
    before = run.leftovers(engine)
    _, created = run.verify(artifact, agent, engine, credential)
    view = run.finish(created['id'])
    ok = view['status'] == 'Failed' and view['failureCode'] in ('AuthenticationFailed', 'InvalidConfiguration', 'PermissionDenied') and run.leftovers(engine) == before
    record(f'{engine}-credential-failure-{credential}', ok, failureCode=view['failureCode'], reason=view.get('reason'), cleanup=view['cleanup']['state'])


def unreachable_server(run, engine):
    artifact, agent = run.artifacts[engine], run.agents[engine]
    port = 9 if engine == 'mssql' else 8
    target = run.target(engine, 'sql_smoke' if engine == 'mssql' else 'pg_verify') | {'port': port}
    _, created = run.api.call('POST', f'{run.tenant}/backup-artifacts/{artifact}/restore-verifications', {'agentId': agent, 'target': target},
                              headers={'Idempotency-Key': 'r1-' + uuid.uuid4().hex}, expect=(202,))
    view = run.finish(created['id'])
    record(f'{engine}-server-unreachable', view['status'] == 'Failed' and view['failureCode'] in ('DatabaseUnavailable', 'OperationTimedOut', 'PreCheckFailed'),
           failureCode=view['failureCode'], cleanup=view['cleanup']['state'])


def cancel_and_retry(run, engine):
    artifact, agent = run.artifacts[engine + '-big'], run.agents[engine]
    credential = 'sql_smoke' if engine == 'mssql' else 'pg_verify'
    before = run.leftovers(engine)
    _, created = run.verify(artifact, agent, engine, credential)
    restore_id = created['id']
    run.wait(restore_id, lambda view: view['progress']['stage'] in ('Restoring', 'Finalizing', 'Verifying'), 300, 0.2, 'the restore to be running')
    run.api.call('POST', f'{run.tenant}/restores/{restore_id}/cancel', None, expect=(200,))
    view = run.finish(restore_id)
    gone = run.wait(restore_id, lambda item: item['cleanup']['state'] == 'Completed', 180, 1, 'cleanup completed')
    cancelled_ok = view['status'] == 'Cancelled' and view['verificationOutcome'] == 'NotRun' and gone['cleanup']['state'] == 'Completed' and run.leftovers(engine) == before
    # Retry = a new verification of the same artifact; it must pass and leave nothing either.
    _, retry = run.verify(artifact, agent, engine, credential)
    retried = run.finish(retry['id'], 600)
    record(f'{engine}-cancel-then-retry', cancelled_ok and retried['verificationOutcome'] == 'Restored' and run.leftovers(engine) == before,
           cancelled=view['status'], outcome=view['verificationOutcome'], retried=retried['verificationOutcome'], leftovers=len(run.leftovers(engine)))


def agent_crash_recovery(run, engine, with_server_outage):
    """SIGKILL of the agent mid-verification. With the outage variant the removal also fails for real: SQL Server is stopped, and for PostgreSQL (whose server
    also hosts the platform's own database) a superuser session is held on the disposable database, which the CREATEDB-only owner may not terminate."""
    name, container = ('mssql' if engine == 'mssql' else 'pg'), (run.options.mssql_container if engine == 'mssql' else run.options.pg_container)
    artifact, agent = run.artifacts[engine + '-big'], run.agents[engine]
    credential = 'sql_smoke' if engine == 'mssql' else 'pg_verify'
    before = run.leftovers(engine)
    run.hold(name, True)
    _, created = run.verify(artifact, agent, engine, credential)
    restore_id = created['id']
    run.wait(restore_id, lambda view: view['progress']['stage'] in ('Restoring', 'Finalizing', 'Verifying'), 300, 0.2, 'the restore to be running')
    database = run.restore(restore_id)['databaseName']
    holder = None
    if with_server_outage and engine == 'pg':
        environment = dict(os.environ, PGPASSWORD=os.environ['BACKUP_PLATFORM_TEST_POSTGRES_PASSWORD'])
        holder = subprocess.Popen(['docker', 'exec', '-i', '-e', 'PGPASSWORD', container, 'psql', '-U', 'postgres', '-d', database, '-X', '-c', 'SELECT pg_sleep(1800)'],
                                  env=environment, stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL)
        time.sleep(2)
    run.kill_agent(name)
    if with_server_outage and engine == 'mssql':
        subprocess.run(['docker', 'stop', '-t', '5', container], check=True, capture_output=True)
    lost = run.wait(restore_id, lambda view: view['status'] in TERMINAL, 420, 2, 'the lease to expire')
    pending = lost['cleanup']['state'] == 'Pending' and lost['status'] == 'AgentLost'
    detail = {'status': lost['status'], 'cleanupAfterLoss': lost['cleanup']['state']}
    if with_server_outage:
        run.hold(name, False)
        failed = run.wait(restore_id, lambda view: view['cleanup']['attempts'] >= 1 and view['cleanup']['lastFailureCode'], 240, 2, 'a failed cleanup attempt')
        detail |= {'failedAttempts': failed['cleanup']['attempts'], 'failureCode': failed['cleanup']['lastFailureCode'], 'reason': failed['cleanup']['reason'],
                   'databaseStillThere': database in run.leftovers(engine) if engine == 'pg' else None}
        if engine == 'mssql':
            subprocess.run(['docker', 'start', container], check=True, capture_output=True)
            wait_server(run, engine)
        else:
            run.pg(f"SELECT pg_terminate_backend(pid) FROM pg_stat_activity WHERE datname = '{database}'")
            holder.kill()
            holder.wait()
        run.api.call('POST', f'{run.tenant}/restores/{restore_id}/cleanup/retry', None, expect=(202,))
    else:
        run.hold(name, False)
    done = run.wait_cleanup(restore_id, 'Completed', 420)
    purge_killed_agent_staging(run, engine)
    ok = pending and done['cleanup']['state'] == 'Completed' and run.leftovers(engine) == before and (not with_server_outage or detail['failedAttempts'] >= 1)
    record(f'{engine}-agent-crash{"-and-cleanup-failure" if with_server_outage else ""}-recovered', ok, finalAttempts=done['cleanup']['attempts'], leftovers=len(run.leftovers(engine)), **detail)


def purge_killed_agent_staging(run, engine):
    """A SIGKILLed agent cannot delete its staged copy of the backup (the product's staging sweeper does after 12 hours). This disposable runtime is a tmpfs, so the
    harness reclaims the space itself between crash scenarios; only files of this run's own staging directory, which no agent is using any more, are removed."""
    directory = Path(os.environ['BACKUP_PLATFORM_TEST_MSSQL_STAGING_AGENT_DIR'] if engine == 'mssql' else run.work / 'pg-staging')
    for path in directory.glob('*'):
        if path.is_file() and path.name.startswith('restore'):
            path.unlink(missing_ok=True)


def wait_server(run, engine, timeout=180):
    deadline = time.monotonic() + timeout
    while time.monotonic() < deadline:
        try:
            run.leftovers(engine)
            return
        except Exception:
            time.sleep(3)
    raise FixtureFailure('The database server did not come back.')


def backend_restart(run, engine):
    artifact, agent = run.artifacts[engine + '-big'], run.agents[engine]
    credential = 'sql_smoke' if engine == 'mssql' else 'pg_verify'
    before = run.leftovers(engine)
    _, created = run.verify(artifact, agent, engine, credential)
    restore_id = created['id']
    run.wait(restore_id, lambda view: view['progress']['stage'] in ('Restoring', 'Finalizing', 'Verifying'), 300, 0.2, 'the restore to be running')
    run.restart_api()
    view = run.finish(restore_id, 600)
    record(f'{engine}-backend-restart-during-verification', view['status'] == 'Succeeded' and view['verificationOutcome'] == 'Restored' and run.leftovers(engine) == before,
           status=view['status'], outcome=view['verificationOutcome'], cleanup=view['cleanup']['state'])


def insufficient_staging(run):
    artifact, agent = run.artifacts['pg'], run.agents['pg-small']
    before = run.leftovers('pg')
    _, created = run.verify(artifact, agent, 'pg', 'pg_verify')
    view = run.finish(created['id'])
    record('pg-insufficient-staging-capacity', view['status'] == 'Failed' and view['failureCode'] == 'InsufficientDiskSpace' and run.leftovers('pg') == before,
           failureCode=view['failureCode'], reason=view.get('reason'))


def isolation(run):
    foreign = Api(run.options.server, run.options.certificate)
    foreign.login(run.state['foreignOwner']['login'], run.state['foreignOwner']['password'])
    foreign_tenant = '/api/tenants/' + run.state['foreignTenantId']
    _, created = run.verify(run.artifacts['pg'], run.agents['pg'], 'pg', 'pg_verify')
    run.finish(created['id'])
    own = created['id']
    denied = []
    # Another tenant's owner is a member of their own tenant only: the tenant id of the path is theirs, so ids of tenant A are simply not found.
    for method, path, body in (('GET', f'{foreign_tenant}/restores/{own}', None), ('POST', f'{foreign_tenant}/restores/{own}/cancel', None),
                               ('POST', f'{foreign_tenant}/restores/{own}/cleanup/retry', None),
                               ('POST', f'{foreign_tenant}/restores/{own}/rollback-removal', {'confirmDatabaseName': 'x'}),
                               ('POST', f'{foreign_tenant}/backup-artifacts/{run.artifacts["pg"]}/restore-verifications', {'agentId': run.agents['pg'], 'target': run.target('pg', 'pg_verify')})):
        status = foreign.call(method, path, body, headers={'Idempotency-Key': 'r1-' + uuid.uuid4().hex}, expect=(400, 403, 404))[0]
        denied.append(status)
    # And the foreign owner cannot reach tenant A by naming its tenant id.
    cross = foreign.call('GET', f'{run.tenant}/restores/{own}', expect=(401, 403, 404))[0]
    _, own_list = foreign.call('GET', f'{foreign_tenant}/restores?purpose=Verification')
    # Roles within tenant A.
    roles = {}
    for role in ('Viewer', 'Operator', 'Admin'):
        api = Api(run.options.server, run.options.certificate)
        api.login(run.state['roles'][role]['login'], run.state['roles'][role]['password'])
        roles[role] = api.call('POST', f'{run.tenant}/backup-artifacts/{run.artifacts["pg"]}/restore-verifications',
                               {'agentId': run.agents['pg'], 'target': run.target('pg', 'pg_verify')}, headers={'Idempotency-Key': 'r1-' + uuid.uuid4().hex}, expect=(202, 403))[0]
    viewer = Api(run.options.server, run.options.certificate)
    viewer.login(run.state['roles']['Viewer']['login'], run.state['roles']['Viewer']['password'])
    viewer_read = viewer.call('GET', f'{run.tenant}/restores/{own}', expect=(200,))[0]
    operator = Api(run.options.server, run.options.certificate)
    operator.login(run.state['roles']['Operator']['login'], run.state['roles']['Operator']['password'])
    operator_removal = operator.call('POST', f'{run.tenant}/restores/{own}/rollback-removal', {'confirmDatabaseName': 'x'}, expect=(403,))[0]
    ok = all(status in (400, 403, 404) for status in denied) and cross in (401, 403, 404) and own_list['totalCount'] == 0 and roles == {'Viewer': 403, 'Operator': 202, 'Admin': 202} \
        and viewer_read == 200 and operator_removal == 403
    record('tenant-isolation-and-roles', ok, foreignStatuses=denied, crossTenant=cross, roles=roles, operatorRollbackRemoval=operator_removal)


def rollback_copy(run):
    """A normal PostgreSQL replace restore keeps the replaced database as bp_old_*; the platform never removes it on its own, only an administrator who repeats its name."""
    suffix = uuid.uuid4().hex[:12]
    target = f'bp_r1_target_{suffix}'
    decoy = 'bp_old_' + uuid.uuid4().hex
    run.pg(f'CREATE DATABASE {target}')
    run.pg("CREATE TABLE marker (v text); INSERT INTO marker VALUES ('before-replace')", database=target)
    run.pg(f'CREATE DATABASE {decoy}')
    artifact, agent = run.artifacts['pg'], run.agents['pg']
    body = {'agentId': agent, 'databaseName': target, 'mode': 'ReplaceExistingDatabase', 'confirmOverwrite': target,
            'target': run.target('pg', 'pg_smoke'), 'options': {'terminateActiveConnections': True}}
    _, created = run.api.call('POST', f'{run.tenant}/backup-artifacts/{artifact}/restore', body, headers={'Idempotency-Key': 'r1-' + uuid.uuid4().hex}, expect=(202,))
    view = run.finish(created['id'])
    cleanup = view['cleanup'] or {}
    retained = cleanup.get('databaseName')
    names = run.pg("SELECT datname FROM pg_database WHERE datname LIKE 'bp\\_old\\_%' ORDER BY 1")
    kept = view['status'] == 'Succeeded' and cleanup.get('kind') == 'RollbackCopy' and cleanup.get('state') == 'Retained' and retained in names and decoy in names
    restored_rows = run.pg('SELECT count(*) FROM items', database=target)
    path = f'{run.tenant}/restores/{created["id"]}/rollback-removal'
    admin = Api(run.options.server, run.options.certificate)
    admin.login(run.state['roles']['Admin']['login'], run.state['roles']['Admin']['password'])
    wrong = admin.call('POST', path, {'confirmDatabaseName': decoy}, expect=(400,))[0]
    early = admin.call('POST', path, {'confirmDatabaseName': retained}, expect=(409,))[0] if not cleanup.get('canRequest') else None
    deadline = time.monotonic() + 120
    while time.monotonic() < deadline and not run.restore(created['id'])['cleanup']['canRequest']:
        time.sleep(2)
    accepted = admin.call('POST', path, {'confirmDatabaseName': retained}, expect=(202,))[0]
    done = run.wait_cleanup(created['id'], 'Completed', 240)
    after = run.pg("SELECT datname FROM pg_database WHERE datname LIKE 'bp\\_old\\_%' ORDER BY 1")
    ok = kept and wrong == 400 and early in (409, None) and accepted == 202 and done['cleanup']['state'] == 'Completed' and retained not in after and decoy in after and restored_rows == ['20000']
    record('pg-rollback-copy-retained-and-removed-only-by-admin-request', ok, retained=retained, earlyRequest=early, decoyUntouched=decoy in after, restoredRows=restored_rows)
    run.pg(f'DROP DATABASE IF EXISTS {decoy} WITH (FORCE)')
    run.pg(f'DROP DATABASE IF EXISTS {target} WITH (FORCE)')



# ---- Windows VM (real SQL Server on the VM, real Windows service Agent with DPAPI identity) ----

def win_sql(run, script, timeout=900):
    secret = json.loads(Path(run.options.win_sql_secret).read_text())
    environment = dict(os.environ, SQLCMDPASSWORD=secret['password'])
    done = subprocess.run(['docker', 'exec', '-i', '-e', 'SQLCMDPASSWORD', run.options.mssql_container, '/opt/mssql-tools18/bin/sqlcmd', '-S', f'{run.options.win_sql_host},{run.options.win_sql_port}',
                           '-U', secret['userName'], '-C', '-b', '-h', '-1', '-W', '-t', str(timeout)], input=script, text=True, capture_output=True, env=environment, timeout=timeout + 30)
    if done.returncode:
        raise FixtureFailure('VM SQL Server command failed: ' + (done.stdout + done.stderr).replace('\n', ' ')[-300:])
    return [line.strip() for line in done.stdout.splitlines() if line.strip()]


def win_leftovers(run):
    return [name for name in win_sql(run, "SET NOCOUNT ON; SELECT name FROM sys.databases WHERE name LIKE 'bp[_]verify[_]%';\nGO\n") if VERIFY_NAME.fullmatch(name)]


def powershell(run, command, timeout=300):
    done = subprocess.run([str(HERE.parent / 'windows' / 'run-powershell.sh'), '--command', command], capture_output=True, text=True, timeout=timeout)
    if done.returncode:
        raise FixtureFailure('VM command failed: ' + done.stderr.replace('\n', ' ')[-300:])
    return [line.strip() for line in done.stdout.splitlines() if line.strip() and not line.startswith(('**', '#<', '<Objs'))]


def win_agent(run, action):
    arguments = ['-Action', action, '-RootDirectory', run.options.windows_root]
    if action == 'start':
        arguments += ['-BackendUri', run.options.win_backend]
    done = subprocess.run([str(HERE.parent / 'windows' / 'run-powershell.sh'), '--file', str(HERE.parent / 'windows' / 'R1-Console-Agent.ps1'), '--', *arguments], capture_output=True, text=True, timeout=300)
    if done.returncode:
        raise FixtureFailure('VM agent control failed: ' + done.stderr.replace('\n', ' ')[-300:])


def win_replica(run, artifact_id, action):
    checksum = run.checksums[artifact_id].upper()
    root = run.options.windows_root
    find = (f"$f = Get-ChildItem -LiteralPath '{root}\\storage' -Recurse -File | Where-Object {{ $_.Name -notlike '*.r1-keep' -and (Get-FileHash -Algorithm SHA256 -LiteralPath $_.FullName).Hash -eq '{checksum}' }} | Select-Object -First 1; "
            "if (-not $f) { throw 'replica not found' }; ")
    if action == 'corrupt':
        powershell(run, find + "Copy-Item -LiteralPath $f.FullName -Destination ($f.FullName + '.r1-keep'); $s = [IO.File]::Open($f.FullName, 'Open', 'ReadWrite'); $p = [int64]($s.Length / 2); $s.Position = $p; $b = $s.ReadByte(); $s.Position = $p; $s.WriteByte($b -bxor 0xFF); $s.Close()")
    else:
        powershell(run, f"Get-ChildItem -LiteralPath '{root}\\storage' -Recurse -File -Filter '*.r1-keep' | ForEach-Object {{ Move-Item -Force -LiteralPath $_.FullName -Destination $_.FullName.Substring(0, $_.FullName.Length - 7) }}")


def windows_phase(run, selected):
    options = run.options
    suffix = uuid.uuid4().hex[:12]
    small, big = f'bp_r1_win_small_{suffix}', f'bp_r1_win_big_{suffix}'
    win_sql(run, f"SET NOCOUNT ON; CREATE DATABASE [{small}];\nGO\nUSE [{small}]; CREATE TABLE dbo.Items (Id int PRIMARY KEY, Marker nvarchar(100)); INSERT dbo.Items VALUES (1, N'r1-win'), (2, N'r1-win-b');\nGO\n")
    win_sql(run, f"SET NOCOUNT ON; CREATE DATABASE [{big}];\nGO\nUSE [{big}]; CREATE TABLE dbo.Rows (Id int IDENTITY(1,1) PRIMARY KEY, A int NOT NULL, B uniqueidentifier NOT NULL DEFAULT NEWID(), C char(100) NOT NULL);\n"
                 "INSERT dbo.Rows (A, C) SELECT TOP (4000000) ABS(CHECKSUM(NEWID())), REPLICATE(CONVERT(char(36), NEWID()), 2) + REPLICATE('x', 28) FROM sys.all_objects a CROSS JOIN sys.all_objects b;\n"
                 "CREATE INDEX IX_A ON dbo.Rows (A); CREATE INDEX IX_B ON dbo.Rows (B); CREATE INDEX IX_C ON dbo.Rows (C);\nGO\n", timeout=1800)
    agent = run.agent_id(options.agent_win)
    run.wait_capability(agent, 'mssql.restore.verification', 180)
    host, port = options.win_sql_host, options.win_sql_port
    artifacts = {}
    for key, database in (('small', small), ('big', big)):
        artifacts[key] = run.backup('mssql', database, agent, f'R1 win {key}', 'sql_smoke', host=host, port=port, agent_local=True)
    before = win_leftovers(run)
    _, created = run.verify(artifacts['small'], agent, 'mssql', 'sql_smoke', host=host, port=port)
    view = run.finish(created['id'], 600)
    record('windows-mssql-healthy-artifact-restored', view['status'] == 'Succeeded' and view['verificationOutcome'] == 'Restored' and view['cleanup']['state'] == 'Completed'
           and win_leftovers(run) == before, restoreId=view['id'], checks=[check['checkId'] for check in view['result']['verification']['checks']], leftovers=len(win_leftovers(run)))
    win_replica(run, artifacts['small'], 'corrupt')
    try:
        _, created = run.verify(artifacts['small'], agent, 'mssql', 'sql_smoke', host=host, port=port)
        view = run.finish(created['id'], 600)
    finally:
        win_replica(run, artifacts['small'], 'restore')
    record('windows-mssql-corrupt-artifact-failed', view['status'] == 'Failed' and view['failureCode'] == 'ChecksumMismatch' and win_leftovers(run) == before,
           failureCode=view['failureCode'], cleanup=view['cleanup']['state'])
    _, created = run.verify(artifacts['big'], agent, 'mssql', 'sql_smoke', host=host, port=port)
    restore_id = created['id']
    run.wait(restore_id, lambda item: item['progress']['stage'] in ('Restoring', 'Finalizing', 'Verifying'), 600, 0.3, 'the VM restore to be running')
    # The agent process is killed mid-run (a real crash) and started again the way a supervisor would.
    win_agent(run, 'stop')
    win_agent(run, 'start')
    lost = run.wait(restore_id, lambda item: item['status'] in TERMINAL, 420, 2, 'the interrupted verification to end')
    done = run.wait_cleanup(restore_id, 'Completed', 420)
    record('windows-agent-crash-during-verification-recovered', done['cleanup']['state'] == 'Completed' and win_leftovers(run) == before,
           status=lost['status'], outcome=lost.get('verificationOutcome'), cleanupAttempts=done['cleanup']['attempts'], leftovers=len(win_leftovers(run)))
    for name in (small, big):
        win_sql(run, f"IF DB_ID(N'{name}') IS NOT NULL BEGIN ALTER DATABASE [{name}] SET SINGLE_USER WITH ROLLBACK IMMEDIATE; DROP DATABASE [{name}]; END\nGO\n")


def audit(run, restore_id):
    actions = run.audit_actions(restore_id)
    record('audit-trail', {'restore.verification.requested', 'restore.verification.completed'} <= set(actions), actions=actions)


def setup_databases(run):
    suffix = uuid.uuid4().hex[:12]
    run.mssql("SET NOCOUNT ON; CREATE DATABASE [bp_r1_small_%s];\nGO\nUSE [bp_r1_small_%s]; CREATE TABLE dbo.Items (Id int PRIMARY KEY, Marker nvarchar(100)); INSERT dbo.Items VALUES (1, N'r1'), (2, N'r1b');\nGO\n" % (suffix, suffix))
    run.mssql("SET NOCOUNT ON; CREATE DATABASE [bp_r1_big_%s];\nGO\nUSE [bp_r1_big_%s]; CREATE TABLE dbo.Rows (Id int IDENTITY(1,1) PRIMARY KEY, A int NOT NULL, B uniqueidentifier NOT NULL DEFAULT NEWID(), C char(100) NOT NULL);\n"
              "INSERT dbo.Rows (A, C) SELECT TOP (6000000) ABS(CHECKSUM(NEWID())), REPLICATE(CONVERT(char(36), NEWID()), 2) + REPLICATE('x', 28) FROM sys.all_objects a CROSS JOIN sys.all_objects b;\n"
              "CREATE INDEX IX_A ON dbo.Rows (A); CREATE INDEX IX_B ON dbo.Rows (B); CREATE INDEX IX_C ON dbo.Rows (C);\nGO\n" % (suffix, suffix), timeout=1800)
    run.pg("CREATE DATABASE bp_r1_small_%s" % suffix)
    run.pg("CREATE TABLE items (id int PRIMARY KEY, marker text); INSERT INTO items SELECT g, md5(g::text) FROM generate_series(1, 20000) g; CREATE TABLE tail (id int PRIMARY KEY, payload text); "
           "INSERT INTO tail SELECT g, (SELECT string_agg(md5(random()::text || i::text || g::text), '') FROM generate_series(1, 40) i) FROM generate_series(1, 20000) g", database='bp_r1_small_' + suffix)
    run.pg("CREATE DATABASE bp_r1_big_%s" % suffix)
    run.pg("CREATE TABLE blob (id int PRIMARY KEY, payload text); INSERT INTO blob SELECT g, (SELECT string_agg(md5(random()::text || i::text), '') FROM generate_series(1, 300000) i) FROM generate_series(1, 150) g",
           database='bp_r1_big_' + suffix, timeout=1800)
    return suffix


def prepare(run):
    suffix = setup_databases(run)
    run.agents = {'mssql': run.agent_id(run.options.agent_mssql), 'pg': run.agent_id(run.options.agent_pg), 'pg-small': run.agent_id(run.options.agent_pg_small)}
    for key, capability in (('mssql', 'mssql.restore.verification'), ('pg', 'postgres.restore.verification'), ('pg-small', 'postgres.restore.verification')):
        run.wait_capability(run.agents[key], capability)
    run.artifacts['mssql'] = run.backup('mssql', f'bp_r1_small_{suffix}', run.agents['mssql'], 'R1 mssql small', 'sql_smoke')
    run.artifacts['mssql-big'] = run.backup('mssql', f'bp_r1_big_{suffix}', run.agents['mssql'], 'R1 mssql big', 'sql_smoke')
    run.artifacts['pg'] = run.backup('postgres', f'bp_r1_small_{suffix}', run.agents['pg'], 'R1 pg small', 'pg_smoke')
    run.artifacts['pg-big'] = run.backup('postgres', f'bp_r1_big_{suffix}', run.agents['pg'], 'R1 pg big', 'pg_smoke')
    for artifact_id in run.checksums:
        central_replica(run, artifact_id)
    return suffix


def run_all(options):
    run = Run(options)
    prepare(run)
    only = set(options.only or [])

    def selected(name):
        return not only or name in only

    first = None
    for engine in ('mssql', 'pg'):
        if selected('healthy'):
            view = healthy(run, engine)
            first = first or view
    if first and selected('healthy'):
        audit(run, first['id'])
    if selected('corrupt'):
        for engine in ('mssql', 'pg'):
            corrupt(run, engine, 'bitflip', ('ChecksumMismatch',))
            corrupt(run, engine, 'truncate', ('ChecksumMismatch',))
            corrupt(run, engine, 'missing', ('ReplicaUnavailable', 'StorageUnavailable'))
    if selected('credentials'):
        credential_failure(run, 'mssql', 'sql_bad')
        credential_failure(run, 'pg', 'pg_bad')
        credential_failure(run, 'mssql', 'sql_unknown_reference')
        unreachable_server(run, 'mssql')
        unreachable_server(run, 'pg')
    if selected('capacity'):
        insufficient_staging(run)
    if selected('rollback'):
        rollback_copy(run)
    if selected('cancel'):
        for engine in ('mssql', 'pg'):
            cancel_and_retry(run, engine)
    if selected('isolation'):
        isolation(run)
    if selected('backend-restart'):
        for engine in ('mssql', 'pg'):
            backend_restart(run, engine)
    if selected('crash'):
        for engine in ('mssql', 'pg'):
            agent_crash_recovery(run, engine, with_server_outage=False)
    if selected('crash-outage'):
        for engine in ('mssql', 'pg'):
            agent_crash_recovery(run, engine, with_server_outage=True)
    if options.windows_root and (not only or 'windows' in only):
        windows_phase(run, selected)
    failed = [name for name, ok in results if not ok]
    print(json.dumps({'scenarios': len(results), 'failed': failed}))
    if options.keep:
        (Path(options.work) / 'acceptance-done').write_text('ok')
    return 1 if failed else 0


def tokens(options):
    state = shared.read_private(options.state)
    api = Api(options.server, options.certificate)
    api.login(state['owner']['login'], state['owner']['password'])
    for name in options.names:
        _, token = api.call('POST', f'/api/tenants/{state["tenantId"]}/agent-enrollment-tokens', {})
        shared.write_private(str(Path(options.work) / f'{name}-token.json'), {'token': token['token']})


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('command', choices=('tokens', 'run'))
    parser.add_argument('--server', required=True)
    parser.add_argument('--certificate', required=True)
    parser.add_argument('--state', required=True)
    parser.add_argument('--work', required=True)
    parser.add_argument('--names', nargs='*', default=[])
    parser.add_argument('--mssql-container')
    parser.add_argument('--pg-container')
    parser.add_argument('--mssql-host')
    parser.add_argument('--mssql-port', type=int)
    parser.add_argument('--pg-host')
    parser.add_argument('--pg-port', type=int)
    parser.add_argument('--central-root')
    parser.add_argument('--credentials-file')
    parser.add_argument('--agent-mssql')
    parser.add_argument('--agent-pg')
    parser.add_argument('--agent-pg-small')
    parser.add_argument('--windows-root')
    parser.add_argument('--agent-win')
    parser.add_argument('--win-sql-host')
    parser.add_argument('--win-sql-port', type=int)
    parser.add_argument('--win-sql-secret')
    parser.add_argument('--win-backend')
    parser.add_argument('--only', nargs='*')
    parser.add_argument('--keep', action='store_true')
    options = parser.parse_args()
    try:
        sys.exit(tokens(options) if options.command == 'tokens' else run_all(options))
    except FixtureFailure as failure:
        print('FAIL: ' + str(failure), file=sys.stderr)
        sys.exit(1)


if __name__ == '__main__':
    main()
