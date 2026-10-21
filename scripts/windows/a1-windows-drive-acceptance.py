#!/usr/bin/env python3
"""A1: the dev stack's Windows Agent (scripts/windows/dev-windows-agent.sh) backs up an isolated VM SQL database to Google Drive through the backend relay.

Scenarios: Full Backup -> Drive ReadBack verification -> backend download SHA-256 -> NewDatabase restore on the Windows Agent from the Drive replica ->
service restart during a ~150 MB upload (automatic retry) -> cancellation during an upload -> delete/cleanup. Only bp_a1_win_* databases and artifacts created
here are touched; the Drive connection is chosen by ACCEPTANCE_CONNECTION_NAME (default: the first connected one). Run through run-a1-acceptance-drive.sh
(Windows SQL profile in the environment). One PASS/FAIL JSON line per scenario.
"""
import hashlib
import importlib.util
import json
import os
import sys
import time
import urllib.request
import uuid
from pathlib import Path

HERE = Path(__file__).resolve().parent


def load(name, path):
    spec = importlib.util.spec_from_file_location(name, path)
    module = importlib.util.module_from_spec(spec)
    sys.modules[name] = module
    spec.loader.exec_module(module)
    return module


sys.path.insert(0, str(HERE.parent / 'dev'))
dev = load('dev_acceptance', HERE.parent / 'dev' / 'acceptance.py')
win = load('a1_windows', HERE / 'a1-windows-acceptance.py')
FixtureFailure = win.FixtureFailure
TERMINAL = win.TERMINAL
with open(Path(os.environ.get('BP_DEV_HOME', Path.home() / '.local/share/backup-platform-dev')) / 'dev-windows-agent.json') as f:
    ROOT = json.load(f)['root']
STAGING = ROOT + '\\staging'
HOST, PORT = os.environ['BACKUP_PLATFORM_TEST_MSSQL_HOST'], int(os.environ.get('BACKUP_PLATFORM_TEST_MSSQL_PORT', '1433'))


class Drive:
    def __init__(self):
        self.api, self.tenant = dev.session()
        self.connection = dev.pick_connection(self.api, self.tenant, 'google-drive')
        agents = self.api.call('GET', self.tenant + '/agents')[1]
        agents = agents if isinstance(agents, list) else agents['items']
        agent = next((a for a in agents if a['hostName'] == 'dev-windows-agent' and a['connectivity'] == 'online'), None)
        if agent is None:
            raise FixtureFailure('dev-windows-agent is not online.')
        self.agent_id = agent['agentId']

    def job(self, database, name):
        configuration = {'engine': 'mssql', 'host': HOST, 'port': PORT, 'database': database, 'databaseCredentialReference': self.sql_reference(), 'trustServerCertificate': True,
                         'stagingReference': 'default', 'storageTarget': 'default', 'storagePrefix': '', 'copyOnly': True, 'checksumPolicy': 'Required',
                         'compressionPolicy': 'Preferred', 'requiredVerifiedReplicaCount': 1,
                         'destinations': [{'storageProvider': 'google-drive', 'storageConnectionId': self.connection['id'], 'storageExecutionLocation': 'Backend', 'isPrimary': True}]}
        return self.api.call('POST', self.tenant + '/backup-jobs', {'name': name, 'agentId': self.agent_id, 'overlapPolicy': 'Skip', 'configuration': configuration})[1]['id']

    def start(self, job_id):
        _, run = self.api.call('POST', f'{self.tenant}/backup-jobs/{job_id}/runs', {}, headers={'Idempotency-Key': 'a1d-' + uuid.uuid4().hex}, expect=(200, 201, 202))
        return run.get('id', run.get('run', {}).get('id'))

    def run(self, run_id):
        return self.api.call('GET', f'{self.tenant}/backup-runs/{run_id}')[1]

    def wait_run(self, run_id, predicate, timeout, interval=1):
        deadline = time.monotonic() + timeout
        while time.monotonic() < deadline:
            detail = self.run(run_id)
            if predicate(detail):
                return detail
            time.sleep(interval)
        raise FixtureFailure(f'Run {run_id[:8]} timed out in {self.run(run_id)["status"]}.')

    def artifact(self, artifact_id):
        return self.api.call('GET', f'{self.tenant}/backup-artifacts/{artifact_id}')[1]

    def good_artifact(self, detail):
        found = [a for attempt in detail['attempts'] if attempt['status'] == 'Succeeded' for a in attempt['artifacts']]
        if len(found) != 1:
            raise FixtureFailure('The run did not publish exactly one artifact.')
        return found[0]

    def sql_reference(self):
        if getattr(self, '_sql_reference', None) is None:
            self._sql_reference = dev.store_login(self.api, self.tenant, self.agent_id, os.environ['BACKUP_PLATFORM_TEST_MSSQL_USER'], os.environ['BACKUP_PLATFORM_TEST_MSSQL_PASSWORD'])
        return self._sql_reference

    def restore(self, artifact_id, suffix, database):
        body = {'agentId': self.agent_id, 'target': {'host': HOST, 'port': PORT, 'databaseCredentialReference': self.sql_reference(), 'trustServerCertificate': True},
                'databaseName': database, 'mode': 'NewDatabase'}
        _, started = self.api.call('POST', f'{self.tenant}/backup-artifacts/{artifact_id}/{suffix}', body, headers={'Idempotency-Key': 'a1d-' + uuid.uuid4().hex}, expect=(200, 201, 202))
        restore_id = started.get('id', started.get('restoreId'))
        deadline = time.monotonic() + 900
        while time.monotonic() < deadline:
            detail = self.api.call('GET', f'{self.tenant}/restores/{restore_id}')[1]
            if detail['status'] in ('Succeeded', 'Failed', 'Cancelled', 'AgentLost'):
                return detail
            time.sleep(1)
        raise FixtureFailure('Restore timed out.')

    def download_sha(self, artifact_id):
        path = f'{self.tenant}/backup-artifacts/{artifact_id}'
        _, prepared = self.api.call('POST', path + '/downloads', {}, expect=(200, 201, 202))
        deadline = time.monotonic() + 120
        while time.monotonic() < deadline:
            state = self.api.call('GET', path + '/downloads/' + prepared['sessionId'])[1]
            if state['state'] in ('Ready', 'Streaming'):
                break
            if state['state'] in ('Failed', 'Cancelled'):
                raise FixtureFailure('Download preparation failed.')
            time.sleep(0.5)
        digest, size = hashlib.sha256(), 0
        with self.api.opener.open(urllib.request.Request(dev.SERVER + prepared['downloadUrl']), timeout=900) as response:
            while chunk := response.read(256 * 1024):
                digest.update(chunk)
                size += len(chunk)
        return digest.hexdigest(), size

    def delete(self, artifact_id):
        path = f'{self.tenant}/backup-artifacts/{artifact_id}'
        self.api.call('DELETE', path, expect=(200, 202))
        deadline = time.monotonic() + 300
        while time.monotonic() < deadline:
            detail = self.api.call('GET', path)[1]
            if detail['state'] == 'Deleted':
                return detail
            if detail['state'] == 'DeleteFailed':
                raise FixtureFailure('Physical deletion failed.')
            time.sleep(2)
        raise FixtureFailure('Deletion timed out.')

    def states(self, detail):
        return [(a['number'], a['status'], self.artifact(x['id'])['state']) for a in detail['attempts'] for x in a['artifacts']]


def staging():
    return win.powershell(f"Get-ChildItem -LiteralPath '{STAGING}' -Recurse -File -ErrorAction SilentlyContinue | ForEach-Object {{ $_.Name }}")


def restart_service():
    win.powershell('Restart-Service BackupAgent -Force; (Get-Service BackupAgent).Status')


def main():
    rows = int(os.environ.get('A1_DRIVE_BIG_ROWS', '19000'))
    drive = Drive()
    suffix = uuid.uuid4().hex[:12]
    small, big = f'bp_a1_win_{suffix}', f'bp_a1_win_big_{suffix}'
    created, artifacts = [], []
    try:
        with dev.unlimited_while_unsized():
            win.create_small(small)
            created.append(small)
            try:
                run_id = drive.start(drive.job(small, 'A1 Windows agent to Drive'))
                detail = drive.wait_run(run_id, lambda d: d['status'] in TERMINAL, 600)
                if detail['status'] != 'Succeeded':
                    raise FixtureFailure(f'Backup ended as {detail["status"]}.')
                artifact = drive.good_artifact(detail)
                full = drive.artifact(artifact['id'])
                artifacts.append(artifact['id'])
                replica = full['replicas'][0]
                ok = replica['storageProvider'] == 'google-drive' and replica['verificationState'] == 'Verified' and replica['checksum'] == artifact['artifactChecksum']
                win.record('windows-agent-drive-backup-verified', ok, sizeBytes=artifact['sizeBytes'], sha256=artifact['artifactChecksum'],
                           verification=replica['verificationMethod'], stagingLeftovers=staging())
                sha, size = drive.download_sha(artifact['id'])
                win.record('windows-agent-drive-download', sha == artifact['artifactChecksum'].lower() and size == artifact['sizeBytes'], bytes=size)
                restored = small + '_restored'
                precheck = drive.restore(artifact['id'], 'restore-precheck', restored)
                result = drive.restore(artifact['id'], 'restore', restored)
                source, copy = win.content(small), win.content(restored)
                win.record('windows-agent-restore-from-drive', precheck['status'] == 'Succeeded' and result['status'] == 'Succeeded' and source == copy and copy[0] == 'ONLINE',
                           precheck=precheck['status'], restore=result['status'], source=source, restored=copy)
            except (FixtureFailure, KeyError) as failure:
                win.record('windows-agent-drive-small-flow', False, error=str(failure)[:300])
            win.create_big(big, rows)
            created.append(big)
            try:
                run_id = drive.start(drive.job(big, 'A1 Windows agent restart during Drive upload'))
                drive.wait_run(run_id, lambda d: d['status'] == 'Running', 180, 0.3)
                time.sleep(float(os.environ.get('A1_UPLOAD_HEAD_START', '15')))
                restart_service()
                detail = drive.wait_run(run_id, lambda d: d['status'] in TERMINAL, 1800, 3)
                if detail['status'] == 'Succeeded':
                    good = drive.good_artifact(detail)
                    artifacts.append(good['id'])
                    sha, size = drive.download_sha(good['id'])
                    win.record('windows-agent-drive-restart-recovery', len(detail['attempts']) >= 2 and sha == good['artifactChecksum'].lower(),
                               attempts=drive.states(detail), bytes=size, stagingLeftovers=staging())
                else:
                    win.record('windows-agent-drive-restart-recovery', False, status=detail['status'], attempts=drive.states(detail))
            except (FixtureFailure, KeyError) as failure:
                win.record('windows-agent-drive-restart-recovery', False, error=str(failure)[:300])
            try:
                run_id = drive.start(drive.job(big, 'A1 Windows agent cancel during Drive upload'))
                drive.wait_run(run_id, lambda d: d['status'] == 'Running', 180, 0.3)
                time.sleep(float(os.environ.get('A1_UPLOAD_HEAD_START', '15')))
                drive.api.call('POST', f'{drive.tenant}/backup-runs/{run_id}/cancel', {}, headers={'Idempotency-Key': 'a1d-' + uuid.uuid4().hex}, expect=(200, 202, 204))
                detail = drive.wait_run(run_id, lambda d: d['status'] in TERMINAL, 300)
                time.sleep(20)
                states = drive.states(detail)
                leftovers = staging()
                win.record('windows-agent-drive-cancel-upload', detail['status'] == 'Cancelled' and all(s[2] == 'Deleted' for s in states) and not leftovers,
                           status=detail['status'], attempts=states, stagingLeftovers=leftovers)
            except (FixtureFailure, KeyError) as failure:
                win.record('windows-agent-drive-cancel-upload', False, error=str(failure)[:300])
            try:
                deleted = [drive.delete(artifact_id)['state'] for artifact_id in artifacts]
                artifacts.clear()
                win.record('windows-agent-drive-delete-cleanup', all(state == 'Deleted' for state in deleted), artifacts=deleted)
            except (FixtureFailure, KeyError) as failure:
                win.record('windows-agent-drive-delete-cleanup', False, error=str(failure)[:300])
    finally:
        for artifact_id in artifacts:
            try:
                drive.delete(artifact_id)
            except (FixtureFailure, KeyError):
                print('cleanup warning: artifact ' + artifact_id[:8] + ' remains', file=sys.stderr)
        for name in reversed(created):
            for target in (name + '_restored', name):
                try:
                    win.drop(target)
                except FixtureFailure as failure:
                    print('cleanup warning: ' + str(failure)[:200], file=sys.stderr)
    return 0 if all(ok for _, ok in win.results) else 1


if __name__ == '__main__':
    sys.exit(main())
