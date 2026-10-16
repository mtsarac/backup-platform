#!/usr/bin/env python3
"""A1 Windows acceptance on top of the owned download-smoke stack (scripts/frontend/run-download-smoke.sh).

Reuses that stack's real Windows service enrollment, DPAPI identity and tenant fixture; adds isolated SQL databases on the
Windows VM's SQL Server and the lifecycle scenarios the smoke does not cover: heartbeat/capabilities, restore discovery,
Full Backup + NewDatabase restore with content proof, cancellation, service restart and network-cut recovery.

Secrets stay in the environment (SQL credentials) or 0600 files; nothing secret is printed. Only databases named
bp_a1_win_[big_]<12 hex>[_restored] are ever created or dropped. Run through scripts/windows/run-a1-acceptance.sh.
"""
import argparse
import importlib.util
import json
import os
import re
from pathlib import Path
import subprocess
import sys
import time
import uuid

HERE = Path(__file__).resolve().parent
ROOT = HERE.parent.parent
spec = importlib.util.spec_from_file_location('quota_fixture', ROOT / 'scripts/frontend/quota-smoke-fixture.py')
shared = importlib.util.module_from_spec(spec)
spec.loader.exec_module(shared)
Api, FixtureFailure = shared.Api, shared.FixtureFailure

VM_DOMAIN, VM_INTERFACE = 'win11', '52:54:00:c5:fd:46'  # libvirt accepts the NIC's MAC address
DB_NAME = re.compile(r'bp_a1_win_(big_)?[0-9a-f]{12}(_restored)?')
TERMINAL = ('Succeeded', 'Failed', 'Cancelled', 'AgentLost', 'Skipped')
results = []


def record(name, ok, **detail):
    results.append((name, ok))
    print(json.dumps({'scenario': name, 'result': 'PASS' if ok else 'FAIL', **detail}, default=str), flush=True)


def powershell(command):
    done = subprocess.run([str(HERE / 'run-powershell.sh'), '--command', command], capture_output=True, text=True, timeout=300)
    if done.returncode:
        raise FixtureFailure('VM command failed: ' + done.stderr.replace('\n', ' ')[-300:])
    return [line.strip() for line in done.stdout.splitlines() if line.strip() and not line.startswith(('**', '#<', '<Objs'))]


def sql(script, timeout=900):
    environment = dict(os.environ, SQLCMDPASSWORD=os.environ['BACKUP_PLATFORM_TEST_MSSQL_PASSWORD'])
    target = os.environ['BACKUP_PLATFORM_TEST_MSSQL_HOST'] + ',' + os.environ.get('BACKUP_PLATFORM_TEST_MSSQL_PORT', '1433')
    done = subprocess.run(['docker', 'exec', '-i', '-e', 'SQLCMDPASSWORD', 'bp-dev-mssql', '/opt/mssql-tools18/bin/sqlcmd', '-S', target, '-U',
                           os.environ['BACKUP_PLATFORM_TEST_MSSQL_USER'], '-C', '-b', '-h', '-1', '-W', '-t', str(timeout)],
                          input=script, text=True, capture_output=True, env=environment, timeout=timeout + 30)
    if done.returncode:
        raise FixtureFailure('SQL failed: ' + (done.stdout + done.stderr).replace('\n', ' ')[-300:])
    return [line.strip() for line in done.stdout.splitlines() if line.strip()]


def checked(name):
    if not DB_NAME.fullmatch(name):
        raise FixtureFailure('Refusing a database outside the A1 naming scheme.')
    return name


def drop(name):
    sql(f"IF DB_ID(N'{checked(name)}') IS NOT NULL BEGIN ALTER DATABASE [{name}] SET SINGLE_USER WITH ROLLBACK IMMEDIATE; DROP DATABASE [{name}]; END\nGO\n")


def content(name):
    return sql(f"SET NOCOUNT ON; SELECT state_desc FROM sys.databases WHERE name=N'{checked(name)}';\nSELECT COUNT(*), CHECKSUM_AGG(CHECKSUM(Id, Marker, Payload)) FROM [{name}].dbo.Items;\nGO\n")


class Stack:
    def __init__(self, options):
        self.options = options
        self.state = shared.read_private(options.state)
        self.api = Api(options.server, options.certificate)
        self.api.login(self.state['owner']['login'], self.state['owner']['password'])
        self.tenant = '/api/tenants/' + self.state['tenantId']
        self.agent_id = self.state['agents']['local']
        self.host = os.environ['BACKUP_PLATFORM_TEST_MSSQL_HOST']
        self.port = int(os.environ.get('BACKUP_PLATFORM_TEST_MSSQL_PORT', '1433'))

    def agent(self):
        return self.api.call('GET', f'{self.tenant}/agents/{self.agent_id}')[1]

    def wait_agent(self, predicate, timeout=120, label='agent state'):
        deadline = time.monotonic() + timeout
        while time.monotonic() < deadline:
            agent = self.agent()
            if predicate(agent):
                return agent
            time.sleep(2)
        raise FixtureFailure(f'Timed out waiting for {label}.')

    def job(self, database, name):
        configuration = {'engine': 'mssql', 'host': self.host, 'port': self.port, 'database': database, 'databaseCredentialReference': self.sql_reference(),
                         'trustServerCertificate': True, 'stagingReference': 'default', 'storageTarget': 'default', 'storagePrefix': 'a1-win',
                         'copyOnly': True, 'checksumPolicy': 'Required', 'compressionPolicy': 'Preferred', 'requiredVerifiedReplicaCount': 1,
                         'destinations': [{'storageProvider': 'local', 'storageConnectionId': None, 'storageExecutionLocation': 'Agent', 'isPrimary': True}]}
        _, job = self.api.call('POST', self.tenant + '/backup-jobs', {'name': name, 'agentId': self.agent_id, 'overlapPolicy': 'Skip', 'configuration': configuration})
        return job['id']

    def start(self, job_id):
        _, run = self.api.call('POST', f'{self.tenant}/backup-jobs/{job_id}/runs', {}, headers={'Idempotency-Key': 'a1-' + uuid.uuid4().hex}, expect=(200, 201, 202))
        return run.get('id', run.get('run', {}).get('id'))

    def run(self, run_id):
        return self.api.call('GET', f'{self.tenant}/backup-runs/{run_id}')[1]

    def wait_run(self, run_id, predicate, timeout=300, interval=0.5):
        deadline = time.monotonic() + timeout
        while time.monotonic() < deadline:
            detail = self.run(run_id)
            if predicate(detail):
                return detail
            time.sleep(interval)
        raise FixtureFailure(f'Run {run_id[:8]} did not reach the awaited state; last status {self.run(run_id)["status"]}.')

    def backup(self, job_id, timeout=300):
        run_id = self.start(job_id)
        detail = self.wait_run(run_id, lambda d: d['status'] in TERMINAL, timeout)
        if detail['status'] != 'Succeeded':
            raise FixtureFailure(f'Backup ended as {detail["status"]}.')
        artifacts = [a for attempt in detail['attempts'] for a in attempt['artifacts']]
        if len(artifacts) != 1:
            raise FixtureFailure('Backup did not publish exactly one artifact.')
        return run_id, artifacts[0]

    def sql_reference(self):
        if getattr(self, '_sql_reference', None) is None:
            self._sql_reference = shared.store_sql_login(self.api, self.tenant, self.agent_id)
        return self._sql_reference

    def restore_request(self, database):
        return {'agentId': self.agent_id, 'target': {'host': self.host, 'port': self.port, 'databaseCredentialReference': self.sql_reference(), 'trustServerCertificate': True},
                'databaseName': database, 'mode': 'NewDatabase'}

    def restore_step(self, artifact_id, suffix, body, timeout=600):
        _, started = self.api.call('POST', f'{self.tenant}/backup-artifacts/{artifact_id}/{suffix}', body,
                                   headers={'Idempotency-Key': 'a1-' + uuid.uuid4().hex}, expect=(200, 201, 202))
        restore_id = started.get('id', started.get('restoreId'))
        deadline = time.monotonic() + timeout
        while time.monotonic() < deadline:
            detail = self.api.call('GET', f'{self.tenant}/restores/{restore_id}')[1]
            if detail['status'] in ('Succeeded', 'Failed', 'Cancelled', 'AgentLost'):
                return detail
            time.sleep(1)
        raise FixtureFailure(f'{suffix} timed out.')

    def vm_hashes(self):
        """SHA-256 of every file below the Windows Agent's local storage root, keyed by hash."""
        lines = powershell(f"Get-ChildItem -LiteralPath '{self.options.windows_root}\\storage' -Recurse -File | ForEach-Object {{ '{{0}}|{{1}}' -f $_.Length, (Get-FileHash -Algorithm SHA256 -LiteralPath $_.FullName).Hash.ToLowerInvariant() }}")
        return {line.split('|')[1]: int(line.split('|')[0]) for line in lines if '|' in line}

    def staging_files(self):
        return [line for line in powershell(f"Get-ChildItem -LiteralPath '{self.options.windows_root}\\staging' -Recurse -File -ErrorAction SilentlyContinue | ForEach-Object {{ $_.Name }}")]

    def storage_files(self):
        return len(self.vm_hashes())


def scenario_service(stack):
    lines = powershell(
        "$s = Get-CimInstance Win32_Service -Filter \"Name='BackupAgent'\"; "
        "\"service|$($s.State)|$($s.StartMode)|$($s.StartName)\"; "
        f"$f = Join-Path '{stack.options.windows_root}\\identity' 'agent-identity.bin'; "
        "$b = [IO.File]::ReadAllBytes($f); "
        "\"identity|$($b.Length)|\" + (($b[0..7] | ForEach-Object { $_.ToString('x2') }) -join '')")
    values = {line.split('|')[0]: line.split('|')[1:] for line in lines}
    service, identity = values['service'], values['identity']
    token_hex = identity[1]
    ok = service == ['Running', 'Auto', r'NT SERVICE\BackupAgent'] and int(identity[0]) > 0 and token_hex == '01000000d08c9ddf'
    record('windows-service-dpapi', ok, service=service, identityBytes=identity[0], dpapiBlobHeader=token_hex == '01000000d08c9ddf')


def scenario_heartbeat(stack):
    agent = stack.agent()
    names = sorted(c['identifier'] if isinstance(c, dict) else c for c in agent['capabilities'])
    heartbeat_age = None
    first = agent['lastHeartbeatAt']
    stack.wait_agent(lambda a: a['lastHeartbeatAt'] != first, 60, 'a new heartbeat')
    ok = (agent['platform'] == 'Windows' and agent['status'] == 'Active' and agent['connectivity'] == 'online' and agent['operationalStatus'] == 'Online'
          and agent.get('capabilitiesRevision') and any(n.startswith('backup.') for n in names) and any(n.startswith('restore.') or 'restore' in n for n in names))
    record('windows-heartbeat-capability-sync', ok, platform=agent['platform'], os=agent.get('osDescription'), runtime=agent.get('runtimeVersion'),
           revision=agent.get('capabilitiesRevision'), capabilityCount=len(names), capabilities=names, freshHeartbeatObserved=True)


def create_small(name):
    sql(f"SET NOCOUNT ON; CREATE DATABASE [{checked(name)}];\nGO\nUSE [{name}]; CREATE TABLE dbo.Items (Id int PRIMARY KEY, Marker nvarchar(100) NOT NULL, Payload nvarchar(max) NOT NULL);\n"
        "INSERT dbo.Items VALUES (1, N'a1_row_1', REPLICATE(N'a', 4000)), (2, N'a1_row_2', REPLICATE(N'b', 4000)), (3, N'a1_t' + NCHAR(252) + N'rk' + NCHAR(231) + N'e', REPLICATE(N'c', 4000));\nGO\n")


def create_big(name, rows):
    sql(f"SET NOCOUNT ON; CREATE DATABASE [{checked(name)}];\nGO\nUSE [{name}]; CREATE TABLE dbo.Items (Id int IDENTITY PRIMARY KEY, Marker nvarchar(100) NOT NULL DEFAULT N'blob', Payload varbinary(8000) NOT NULL);\n"
        f"INSERT dbo.Items (Payload) SELECT TOP ({rows}) CRYPT_GEN_RANDOM(8000) FROM sys.all_objects a CROSS JOIN sys.all_objects b;\nGO\n")


def scenario_backup_discovery_restore(stack, small):
    job_id = stack.job(small, 'A1 Windows Full Backup')
    run_id, artifact = stack.backup(job_id)
    hashes = stack.vm_hashes()
    on_disk = hashes.get(artifact['artifactChecksum'].lower())
    record('windows-full-backup-checksum', on_disk == artifact['sizeBytes'], sizeBytes=artifact['sizeBytes'], sha256=artifact['artifactChecksum'],
           storedFileSha256Matches=on_disk is not None, stagingLeftovers=stack.staging_files())
    _, targets = stack.api.call('GET', f'{stack.tenant}/backup-artifacts/{artifact["id"]}/restore-targets')
    agent_target = next((a for a in targets['agents'] if a['agentId'] == stack.agent_id), None)
    discovery = stack.restore_step(artifact['id'], 'restore-discovery', {'agentId': stack.agent_id, 'target': stack.restore_request('x')['target']})
    record('windows-mssql-discovery', discovery['status'] == 'Succeeded' and bool(agent_target and agent_target['canDiscover']),
           status=discovery['status'], canDiscover=agent_target and agent_target['canDiscover'], canRestore=agent_target and agent_target['canRestore'])
    restored = small + '_restored'
    request = stack.restore_request(restored)
    precheck = stack.restore_step(artifact['id'], 'restore-precheck', request)
    restore = stack.restore_step(artifact['id'], 'restore', request)
    source, copy = content(small), content(restored)
    record('windows-restore-newdatabase', precheck['status'] == 'Succeeded' and restore['status'] == 'Succeeded' and source == copy and copy[0] == 'ONLINE',
           precheck=precheck['status'], restore=restore['status'], source=source, restored=copy)
    return artifact


def scenario_cancel(stack, big):
    before = stack.storage_files()
    run_id = stack.start(stack.job(big, 'A1 Windows cancel'))
    stack.wait_run(run_id, lambda d: d['status'] == 'Running', 120, 0.3)
    stack.api.call('POST', f'{stack.tenant}/backup-runs/{run_id}/cancel', {}, headers={'Idempotency-Key': 'a1-' + uuid.uuid4().hex}, expect=(200, 202, 204))
    detail = stack.wait_run(run_id, lambda d: d['status'] in TERMINAL, 180)
    time.sleep(8)
    artifacts = [a for attempt in detail['attempts'] for a in attempt['artifacts']]
    leftovers = stack.staging_files()
    record('windows-cancel-running-backup', detail['status'] == 'Cancelled' and not artifacts and not leftovers and stack.storage_files() == before,
           status=detail['status'], artifacts=len(artifacts), stagingLeftovers=leftovers, newStoredFiles=stack.storage_files() - before)


def scenario_restart(stack, big):
    run_id = stack.start(stack.job(big, 'A1 Windows service restart'))
    stack.wait_run(run_id, lambda d: d['status'] == 'Running', 120, 0.3)
    checkpoint = stack.agent()['lastHeartbeatAt']
    powershell('Restart-Service BackupAgent -Force; (Get-Service BackupAgent).Status')
    detail = stack.wait_run(run_id, lambda d: d['status'] in TERMINAL, 900, 2)
    agent = stack.wait_agent(lambda a: a['connectivity'] == 'online' and a['lastHeartbeatAt'] > checkpoint, 120, 'online after restart')
    time.sleep(5)
    _, artifact = stack.backup(stack.job(big, 'A1 Windows after restart'), 600)
    record('windows-service-restart-recovery', agent['agentId'] == stack.agent_id and detail['status'] in ('Failed', 'AgentLost', 'Succeeded'),
           interruptedRunEndedAs=detail['status'], attempts=len(detail['attempts']), sameAgentId=agent['agentId'] == stack.agent_id,
           identityDecryptedAfterRestart=agent['connectivity'] == 'online', followUpBackupSizeBytes=artifact['sizeBytes'], stagingLeftovers=stack.staging_files())


def link(state):
    """Flip the Windows VM's virtual NIC (libvirt); the only way to cut established agent connections without root on the host."""
    done = subprocess.run(['virsh', '-c', 'qemu:///system', 'domif-setlink', VM_DOMAIN, VM_INTERFACE, state], capture_output=True, text=True, timeout=30)
    if done.returncode:
        raise FixtureFailure('virsh domif-setlink failed.')


def scenario_network_cut(stack, big):
    run_id = stack.start(stack.job(big, 'A1 Windows network cut'))
    stack.wait_run(run_id, lambda d: d['status'] == 'Running', 120, 0.3)
    link('down')
    try:
        offline = stack.wait_agent(lambda a: a['connectivity'] != 'online', 180, 'agent offline during cut')
        offline_state = offline['connectivity']
        time.sleep(45)
    finally:
        link('up')
    agent = stack.wait_agent(lambda a: a['connectivity'] == 'online', 300, 'agent online after the network returns')
    detail = stack.wait_run(run_id, lambda d: d['status'] in TERMINAL, 900, 2)
    _, artifact = stack.backup(stack.job(big, 'A1 Windows after network cut'), 600)
    record('windows-network-cut-recovery', agent['agentId'] == stack.agent_id, connectivityDuringCut=offline_state, interruptedRunEndedAs=detail['status'],
           attempts=len(detail['attempts']), backAfterRestore=True, followUpBackupSizeBytes=artifact['sizeBytes'], stagingLeftovers=stack.staging_files())


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--server', required=True)
    parser.add_argument('--certificate', required=True)
    parser.add_argument('--state', required=True)
    parser.add_argument('--windows-root', required=True)
    parser.add_argument('--big-rows', type=int, default=80000)
    parser.add_argument('--only', action='append', help='Run only these big-database scenarios (repeatable); skips the small-database scenarios')
    options = parser.parse_args()
    stack = Stack(options)
    suffix = uuid.uuid4().hex[:12]
    small, big = f'bp_a1_win_{suffix}', f'bp_a1_win_big_{suffix}'
    created = []
    try:
        for name, step in (() if options.only else (('windows-service-dpapi', lambda: scenario_service(stack)), ('windows-heartbeat-capability-sync', lambda: scenario_heartbeat(stack)))):
            try:
                step()
            except (FixtureFailure, KeyError, ValueError) as failure:
                record(name, False, error=str(failure)[:300])
        if not options.only:
            created += [small]
            create_small(small)
            try:
                scenario_backup_discovery_restore(stack, small)
            except (FixtureFailure, KeyError) as failure:
                record('windows-backup-discovery-restore', False, error=str(failure)[:300])
        created += [big]
        create_big(big, options.big_rows)
        for name, step in (('windows-cancel-running-backup', scenario_cancel), ('windows-service-restart-recovery', scenario_restart), ('windows-network-cut-recovery', scenario_network_cut)):
            if options.only and name not in options.only:
                continue
            try:
                step(stack, big)
            except (FixtureFailure, KeyError) as failure:
                record(name, False, error=str(failure)[:300])
    finally:
        link('up')
        for name in reversed(created):
            for target in (name + '_restored', name):
                try:
                    drop(target)
                except (FixtureFailure, subprocess.SubprocessError) as failure:
                    print('cleanup warning: ' + str(failure)[:200], file=sys.stderr)
    return 0 if all(ok for _, ok in results) else 1


if __name__ == '__main__':
    sys.exit(main())
