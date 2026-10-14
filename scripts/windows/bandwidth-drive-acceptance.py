#!/usr/bin/env python3
"""Faz 2A live acceptance: upload bandwidth limits on the dev stack, Windows VM Agent (+ Linux dev Agent) -> BackendRelayed -> real Google Drive.

Reuses the A1 tooling (a1-windows-drive-acceptance.py: Drive connection, run/artifact helpers, VM SQL + service access). Every scenario prints one JSON
line (PASS / FAIL / NOT RUN + measured numbers); the script ends with a summary and exit code 0 (no FAIL, nothing skipped), 1 (any FAIL) or 2 (no FAIL, some NOT RUN).

  baseline  unlimited Drive upload of the big DB: artifact size + upload window
  low       job limit (BW_LOW_LIMIT) on the small DB:   window >= (size - burst)/limit - tol, <= factor * size/limit + overhead; replica Verified/ReadBack
  medium    job limit (BW_MEDIUM_LIMIT) on the big DB:  same assertions
  tenant    tenant limit (BW_TENANT_LIMIT), Windows + Linux Agent upload at once: overlap, combined rate <= limit*1.1, grants <= limit (NOT RUN without the Linux Agent)
  cancel    cancel a paced upload: Cancelled, partial artifact Deleted, staging clean, no grant row / no extra advisory lock or transfer lease left
  retry     restart the Windows service during a paced upload: attempt 1 AgentLost/Failed, the retry attempt succeeds paced, checksum + download SHA-256 correct
  cleanup   always: limits restored, artifacts deleted, jobs DISABLED (the API has no job delete), isolated bp_bw_* databases dropped

"Upload window" = destination outcome StartedAt..CompletedAt (backup_destination_outcomes, read-only SELECT through the postgres container). It includes the ReadBack
verification, so it can only exceed the pure upload: the lower bound is exact, the upper bound has BW_OVERHEAD seconds of slack. Only bp_bw_* databases, jobs named
'BW accept *' and artifacts created here are touched; leftovers of an interrupted run are swept (databases dropped, jobs disabled and un-limited) at the next start.
Run through run-bandwidth-acceptance-drive.sh (Windows SQL profile in the environment). --preflight changes nothing and runs no backup.
"""
import argparse
import contextlib
import importlib.util
import json
import os
from pathlib import Path
import re
import subprocess
import sys
import threading
import time
import uuid

HERE = Path(__file__).resolve().parent
SCENARIOS = ('baseline', 'low', 'medium', 'tenant', 'cancel', 'retry')
DB_NAME = re.compile(r'bp_bw_[wl]_[0-9a-f]{12}')
JOB_PREFIX = 'BW accept '
HOME = Path(os.environ.get('BP_DEV_HOME', Path.home() / '.local/share/backup-platform-dev'))
results, label = [], 'scenario'
a1d = dev = win = FixtureFailure = None


class NotRun(Exception):
    pass


class Warn(Exception):
    pass


def record(name, status, **detail):
    results.append((name, status))
    print(json.dumps({label: name, 'result': status, **detail}, default=str), flush=True)


def need(condition, message):
    if not condition:
        raise FixtureFailure(message)


def load_a1():
    global a1d, dev, win, FixtureFailure
    spec = importlib.util.spec_from_file_location('a1_drive', HERE / 'a1-windows-drive-acceptance.py')
    a1d = importlib.util.module_from_spec(spec)
    sys.modules['a1_drive'] = a1d
    spec.loader.exec_module(a1d)
    dev, win, FixtureFailure = a1d.dev, a1d.win, a1d.FixtureFailure


def parse():
    env = os.environ.get
    parser = argparse.ArgumentParser(description=__doc__.split('\n\n')[0], formatter_class=argparse.RawDescriptionHelpFormatter,
                                     epilog='Every option can also be set as BW_<OPTION> (e.g. BW_LOW_LIMIT, BW_BIG_MB). Sizes are decimal MB of random (incompressible) rows; limits are bytes/s (min 16000).')
    parser.add_argument('--preflight', action='store_true', help='verify the dev stack, VM, agents, Drive connection and limits API; run nothing')
    parser.add_argument('--scenarios', default=env('BW_SCENARIOS', ','.join(SCENARIOS)), help='comma list of: ' + ', '.join(SCENARIOS))
    for flag, default, kind, text in (
            ('small-mb', 4, float, 'small source DB (low, cancel, retry)'), ('big-mb', 24, float, 'big source DB (baseline, medium)'),
            ('tenant-mb', 12, float, 'each of the two tenant-scenario DBs'),
            ('low-limit', 128000, int, 'job limit of the low scenario, B/s'), ('medium-limit', 1500000, int, 'job limit of the medium scenario, B/s'),
            ('tenant-limit', 1000000, int, 'tenant limit of the tenant scenario, B/s'), ('cancel-limit', 128000, int, 'job limit of the cancel scenario, B/s'),
            ('retry-limit', 128000, int, 'job limit of the retry scenario, B/s'),
            ('burst', 1000000, int, 'burst allowance in bytes (lower bound = (size - burst)/limit)'), ('tolerance', 1.5, float, 'lower-bound slack, seconds'),
            ('upper-factor', 3.0, float, 'upper bound = factor * size/limit + overhead'), ('overhead', 8.0, float, 'upper-bound slack (handshake, ReadBack), seconds'),
            ('head-start', 8.0, float, 'seconds into the upload before cancel / service restart'), ('min-overlap', 5.0, float, 'minimum overlap of the two tenant uploads, seconds'),
            ('start-timeout', 180.0, float, 'seconds for a run to be claimed (Running)'), ('run-timeout', 1200.0, float, 'seconds for a run to finish'),
            ('settle-timeout', 45.0, float, 'seconds for grants / locks to disappear after a cancel')):
        parser.add_argument('--' + flag, type=kind, default=kind(env('BW_' + flag.upper().replace('-', '_'), default)), help=f'{text} (default {default})')
    parser.add_argument('--allow-no-grants', action='store_true', default=env('BW_ALLOW_NO_GRANTS') == '1',
                        help='tenant scenario: do not FAIL when no upload_bandwidth_grants row was ever observed')
    args = parser.parse_args()
    args.scenarios = [s.strip() for s in args.scenarios.split(',') if s.strip()]
    unknown = set(args.scenarios) - set(SCENARIOS)
    if unknown:
        parser.error('unknown scenario(s): ' + ', '.join(sorted(unknown)))
    for limit in ('low_limit', 'medium_limit', 'tenant_limit', 'cancel_limit', 'retry_limit'):
        if not 16000 <= getattr(args, limit) <= 100_000_000_000:
            parser.error(f'--{limit.replace("_", "-")} must be 16000..100000000000 B/s')
    return args


# --- read-only PostgreSQL / SQL Server / local filesystem probes ---------------------------------------------------------------------------------------------
def pg(query):
    """Read-only SELECT through the postgres container (the session is forced read-only)."""
    done = subprocess.run(['docker', 'exec', '-e', 'PGOPTIONS=-c default_transaction_read_only=on', os.environ.get('POSTGRES_CONTAINER', 'postgres'), 'psql', '-U', 'postgres',
                           '-d', os.environ.get('BP_DEV_DATABASE', 'backup_platform_dev'), '-AtX', '-F', '|', '-c', query], capture_output=True, text=True, timeout=30)
    if done.returncode:
        raise FixtureFailure('psql failed: ' + done.stderr.replace('\n', ' ')[-200:])
    return [line.split('|') for line in done.stdout.splitlines() if line]


def sa_password():
    for line in (HOME / 'secrets.env').read_text().splitlines():
        if line.startswith('BP_DEV_MSSQL_SA_PASSWORD='):
            return line.split('=', 1)[1]
    raise FixtureFailure('The dev SQL Server password is not available.')


def linux_sql(script, timeout=900):
    done = subprocess.run(['docker', 'exec', '-i', '-e', 'SQLCMDPASSWORD', 'bp-dev-mssql', '/opt/mssql-tools18/bin/sqlcmd', '-S', 'localhost', '-U', 'sa', '-C', '-b', '-h', '-1', '-W', '-t', str(timeout)],
                          input=script, text=True, capture_output=True, env=dict(os.environ, SQLCMDPASSWORD=sa_password()), timeout=timeout + 30)
    if done.returncode:
        raise FixtureFailure('Linux SQL failed: ' + (done.stdout + done.stderr).replace('\n', ' ')[-300:])
    return [line.strip() for line in done.stdout.splitlines() if line.strip()]


def run_sql(kind, script):
    return (win.sql if kind == 'w' else linux_sql)(script)


def linux_staging():
    root = HOME / 'mssql' / 'staging'
    return sorted(p.name for p in root.rglob('*') if p.is_file()) if root.is_dir() else []


def grants(tenant_id):
    """(sum of live slices in B/s, live rows) of the tenant's upload_bandwidth_grants."""
    row = pg(f'SELECT COALESCE(SUM("BytesPerSecond"),0), COUNT(*) FROM upload_bandwidth_grants WHERE "TenantId"=\'{uuid.UUID(tenant_id)}\' AND "ExpiresAt" > now()')[0]
    return int(row[0]), int(row[1])


def locks():
    """(advisory locks, rows of backup_transfer_leases or None while that table does not exist)."""
    advisory = int(pg("SELECT count(*) FROM pg_locks WHERE locktype='advisory'")[0][0])
    table = pg("SELECT to_regclass('public.backup_transfer_leases')")
    return advisory, int(pg('SELECT count(*) FROM backup_transfer_leases')[0][0]) if table and table[0][0] else None


class Sampler(threading.Thread):
    """Samples the tenant's live grants twice a second; the peak sum is the evidence that slices never exceed the tenant limit."""
    def __init__(self, tenant_id):
        super().__init__(daemon=True)
        self.tenant_id, self.halt, self.peak_sum, self.peak_rows, self.samples, self.error = tenant_id, threading.Event(), 0, 0, 0, None

    def run(self):
        while not self.halt.is_set():
            try:
                total, rows = grants(self.tenant_id)
                self.peak_sum, self.peak_rows, self.samples = max(self.peak_sum, total), max(self.peak_rows, rows), self.samples + 1
            except (FixtureFailure, subprocess.SubprocessError) as failure:
                self.error = str(failure)
            self.halt.wait(0.5)

    def stop(self):
        self.halt.set()
        self.join(15)
        need(self.error is None and self.samples > 0, 'The grants sampler failed: ' + str(self.error))
        return {'grantSamples': self.samples, 'peakGrantSumBytesPerSecond': self.peak_sum, 'peakGrantRows': self.peak_rows}


# --- the bench: databases, jobs, limits, runs ----------------------------------------------------------------------------------------------------------------
class Bench:
    def __init__(self, args):
        self.args = args
        self.drive = a1d.Drive()  # tenant session, Drive connection, online dev-windows-agent
        self.api, self.tenant, self.connection = self.drive.api, self.drive.tenant, self.drive.connection
        self.tenant_id = self.tenant.rsplit('/', 1)[1]
        agents = self.api.call('GET', self.tenant + '/agents')[1]
        self.agents = {'w': self.drive.agent_id}
        linux = next((a for a in agents if a['hostName'] == 'dev-linux-agent' and a['connectivity'] == 'online'), None) if isinstance(agents, list) else None
        if linux:
            self.agents['l'] = linux['agentId']
        self.dbs, self.jobs, self.artifacts, self.limits = {}, [], [], []
        self.original_tenant = self.settings()['tenant']['bytesPerSecond']

    def settings(self):
        return self.api.call('GET', self.tenant + '/upload-bandwidth')[1]

    # databases: ~8192 B per random row (one page each), so rows = MB / 8192
    def db(self, kind, mb):
        key = (kind, mb)
        if key not in self.dbs:
            name = f'bp_bw_{kind}_{uuid.uuid4().hex[:12]}'
            need(DB_NAME.fullmatch(name), 'Refusing a database outside the bandwidth naming scheme.')
            rows = int(mb * 1e6 / 8192) + 1
            self.dbs[key] = name
            run_sql(kind, f"SET NOCOUNT ON; CREATE DATABASE [{name}];\nGO\nUSE [{name}]; CREATE TABLE dbo.Items (Id int IDENTITY PRIMARY KEY, Payload varbinary(8000) NOT NULL);\n"
                          f"INSERT dbo.Items (Payload) SELECT TOP ({rows}) CRYPT_GEN_RANDOM(8000) FROM sys.all_objects a CROSS JOIN sys.all_objects b;\nGO\n")
        return self.dbs[key]

    def drop(self, kind, name):
        need(DB_NAME.fullmatch(name), 'Refusing a database outside the bandwidth naming scheme.')
        run_sql(kind, f"IF DB_ID(N'{name}') IS NOT NULL BEGIN ALTER DATABASE [{name}] SET SINGLE_USER WITH ROLLBACK IMMEDIATE; DROP DATABASE [{name}]; END\nGO\n")

    def job(self, kind, database, name, limit=None):
        configuration = {'engine': 'mssql', 'trustServerCertificate': True, 'database': database, 'stagingReference': 'default', 'storageTarget': 'default', 'storagePrefix': '',
                         'copyOnly': True, 'checksumPolicy': 'Required', 'compressionPolicy': 'Preferred', 'requiredVerifiedReplicaCount': 1,
                         'destinations': [{'storageProvider': 'google-drive', 'storageConnectionId': self.connection['id'], 'storageExecutionLocation': 'Backend', 'isPrimary': True}]}
        if kind == 'w':
            configuration.update(host=a1d.HOST, port=a1d.PORT, databaseCredentialReference='sql_smoke')
        else:
            configuration.update(host='127.0.0.1', port=dev.SQL_PORT, databaseCredentialReference=dev.SQL_REFERENCE, integratedSecurity=False, encrypt=True)
        job_id = self.api.call('POST', self.tenant + '/backup-jobs', {'name': f'{JOB_PREFIX}{name} {uuid.uuid4().hex[:6]}', 'agentId': self.agents[kind], 'overlapPolicy': 'Skip',
                                                                      'configuration': configuration})[1]['id']
        self.jobs.append(job_id)
        if limit:
            self.set_limit('jobs', job_id, limit)
        return job_id

    def set_limit(self, scope, scope_id, bytes_per_second):
        path = f'{self.tenant}/upload-bandwidth/' + ('tenant' if scope == 'tenant' else f'{scope}/{scope_id}')
        self.api.call('PUT', path, {'bytesPerSecond': bytes_per_second}, expect=(200,))
        if bytes_per_second is not None:
            self.limits.append((scope, scope_id))
        document = self.settings()
        shown = document['tenant']['bytesPerSecond'] if scope == 'tenant' else next((x['bytesPerSecond'] for x in document[scope] if x[scope[:-1] + 'Id'] == scope_id), None)
        need(shown == bytes_per_second, f'The {scope} limit reads back as {shown}, not {bytes_per_second}.')

    def start(self, job_id):
        return self.drive.start(job_id)

    def running(self, run_id):
        return self.drive.wait_run(run_id, lambda d: d['status'] == 'Running' or d['status'] in a1d.TERMINAL, self.args.start_timeout, 0.3)

    def upload_started(self, run_id, d):
        """Running, then (best effort) the Uploading stage, then head-start seconds into the paced upload; the run must still be Running."""
        need(self.running(run_id)['status'] == 'Running', 'The run ended before its upload started.')
        deadline, seen = time.monotonic() + 60, False
        while time.monotonic() < deadline and not seen:
            seen = any('upload' in (a.get('progressStage') or '').lower() for a in self.drive.run(run_id)['attempts'])
            time.sleep(0 if seen else 0.3)
        d['uploadStageObserved'] = seen
        time.sleep(self.args.head_start)
        need(self.drive.run(run_id)['status'] == 'Running', 'The run finished before the head start elapsed: raise the size or lower the limit.')

    def finish(self, run_id):
        detail = self.drive.wait_run(run_id, lambda x: x['status'] in a1d.TERMINAL, self.args.run_timeout, 1)
        self.artifacts += [x['id'] for a in detail['attempts'] if a['status'] == 'Succeeded' for x in a['artifacts']]  # cleaned up even if an assertion fails
        return detail

    def window(self, attempt_id):
        rows = pg(f'SELECT extract(epoch from "StartedAt"), extract(epoch from "CompletedAt") FROM backup_destination_outcomes WHERE "AttemptId"=\'{uuid.UUID(attempt_id)}\' AND "StorageProvider"=\'google-drive\'')
        need(rows, 'No destination outcome row for the attempt.')
        return float(rows[0][0]), float(rows[0][1])

    def measure(self, detail):
        """Facts of the succeeded attempt: size, upload window, effective rate, replica proof. Registers the artifact for cleanup."""
        need(detail['status'] == 'Succeeded', f'The run ended as {detail["status"]}.')
        attempt = next(a for a in detail['attempts'] if a['status'] == 'Succeeded')
        artifact = self.drive.good_artifact(detail)
        replica = next(r for r in self.drive.artifact(artifact['id'])['replicas'] if r['storageProvider'] == 'google-drive')
        start, end = self.window(attempt['id'])
        measured = {'attempt': attempt['number'], 'sizeBytes': artifact['sizeBytes'], 'uploadSeconds': round(end - start, 2), 'startEpoch': start, 'endEpoch': end,
                    'effectiveBytesPerSecond': round(artifact['sizeBytes'] / max(end - start, 1e-6)), 'verification': f'{replica["verificationState"]}/{replica["verificationMethod"]}'}
        need(replica['verificationState'] == 'Verified' and replica['verificationMethod'] == 'ReadBack', f'The replica is {measured["verification"]}, not Verified/ReadBack.')
        need(replica['checksum'].lower() == artifact['artifactChecksum'].lower(), 'The replica checksum differs from the artifact checksum.')
        return measured

    def assert_paced(self, d, limit):
        a = self.args
        size, seconds = d['sizeBytes'], d['uploadSeconds']
        low, high = (size - a.burst) / limit - a.tolerance, a.upper_factor * size / limit + a.overhead
        d.update(limitBytesPerSecond=limit, percentOfLimit=round(100 * d['effectiveBytesPerSecond'] / limit, 1), minSeconds=round(low, 2), maxSeconds=round(high, 2))
        need(seconds >= low, f'The upload took {seconds}s, faster than the limit allows (>= {low:.1f}s): the pace was not enforced.')
        need(seconds <= high, f'The upload took {seconds}s, more than {a.upper_factor}x the ideal (<= {high:.1f}s).')

    # --- scenarios ---
    def baseline(self, d):
        job = self.job('w', self.db('w', self.args.big_mb), 'baseline')
        d.update(self.measure(self.finish(self.start(job))))

    def limited(self, d, mb, limit, tag):
        job = self.job('w', self.db('w', mb), tag, limit)
        d.update(self.measure(self.finish(self.start(job))))
        self.assert_paced(d, limit)

    @contextlib.contextmanager
    def tenant_limit(self, bytes_per_second):
        self.set_limit('tenant', self.tenant_id, bytes_per_second)
        try:
            yield
        finally:
            self.set_limit('tenant', self.tenant_id, self.original_tenant)

    def tenant(self, d):
        a = self.args
        if 'l' not in self.agents:
            raise NotRun('dev-linux-agent is not online in the dev tenant (scripts/dev/agent.sh start).')
        try:
            linux_sql('SELECT 1\nGO\n')
        except (FixtureFailure, subprocess.SubprocessError, OSError) as failure:
            raise NotRun('The Linux Agent source SQL Server (bp-dev-mssql) is not usable: ' + str(failure)[:150]) from failure
        jobs = [self.job('w', self.db('w', a.tenant_mb), 'tenant-win'), self.job('l', self.db('l', a.tenant_mb), 'tenant-lin')]
        sampler = Sampler(self.tenant_id)
        with self.tenant_limit(a.tenant_limit):
            sampler.start()
            try:
                runs = [self.start(j) for j in jobs]
                for run in runs:
                    need(self.running(run)['status'] == 'Running', 'A tenant-scenario run ended before it started uploading.')
                details = [self.finish(r) for r in runs]
            finally:
                d.update(sampler.stop())
        measured = [self.measure(x) for x in details]
        start, end = min(m['startEpoch'] for m in measured), max(m['endEpoch'] for m in measured)
        overlap = min(m['endEpoch'] for m in measured) - max(m['startEpoch'] for m in measured)
        total = sum(m['sizeBytes'] for m in measured)
        combined = (total - a.burst) / (end - start)
        leftovers = {'windows': a1d.staging(), 'linux': linux_staging()}
        d.update(sizesBytes=[m['sizeBytes'] for m in measured], uploadSeconds=[m['uploadSeconds'] for m in measured], overlapSeconds=round(overlap, 2),
                 totalBytes=total, wallSeconds=round(end - start, 2), combinedBytesPerSecond=round(total / (end - start)), combinedMinusBurstBytesPerSecond=round(combined),
                 limitBytesPerSecond=a.tenant_limit, percentOfLimit=round(100 * total / (end - start) / a.tenant_limit, 1), stagingLeftovers=leftovers)
        need(overlap >= a.min_overlap, f'The uploads overlapped only {overlap:.1f}s (< {a.min_overlap}s): the shared limit was not exercised.')
        need(combined <= a.tenant_limit * 1.1, f'Combined {combined:.0f} B/s exceeds 110% of the tenant limit {a.tenant_limit}.')
        need(d['peakGrantSumBytesPerSecond'] <= a.tenant_limit, f'The sum of live grants reached {d["peakGrantSumBytesPerSecond"]} B/s, above the tenant limit {a.tenant_limit}.')
        need(d['peakGrantRows'] > 0 or a.allow_no_grants, 'No upload_bandwidth_grants row was ever observed: the tenant slices were not exercised (BW_ALLOW_NO_GRANTS=1 relaxes this).')
        need(not leftovers['windows'] and not leftovers['linux'], 'Staging files remain on an Agent.')

    def cancel(self, d):
        a = self.args
        job = self.job('w', self.db('w', a.small_mb), 'cancel', a.cancel_limit)
        with self.tenant_limit(a.cancel_limit * 2):  # exercises a grant row; the job limit stays the tighter one
            before = locks()
            run = self.start(job)
            self.upload_started(run, d)
            d['grantsDuringUpload'] = grants(self.tenant_id)[1]
            self.api.call('POST', f'{self.tenant}/backup-runs/{run}/cancel', {}, headers={'Idempotency-Key': 'bw-' + uuid.uuid4().hex}, expect=(200, 202, 204))
            detail = self.finish(run)
            deadline = time.monotonic() + a.settle_timeout
            while True:  # grants (TTL 10 s), advisory locks and leases are released asynchronously
                rows, after, states = grants(self.tenant_id)[1], locks(), self.drive.states(self.drive.run(run))
                settled = rows == 0 and after[0] <= before[0] and (after[1] is None or after[1] <= (before[1] or 0)) and all(s[2] == 'Deleted' for s in states)
                if settled or time.monotonic() > deadline:
                    break
                time.sleep(1)
        leftovers = a1d.staging()
        d.update(status=detail['status'], attempts=states, grantRowsAfter=rows, advisoryLocksBeforeAfter=[before[0], after[0]], transferLeasesBeforeAfter=[before[1], after[1]], stagingLeftovers=leftovers)
        need(detail['status'] == 'Cancelled', f'The run ended as {detail["status"]}, not Cancelled.')
        need(all(s[2] == 'Deleted' for s in states), 'A partial artifact was not Deleted.')
        need(not leftovers, 'Staging files remain on the Windows Agent.')
        need(rows == 0, 'A grant row remains after the cancel.')
        need(after[0] <= before[0], 'An advisory lock is still held after the cancel.')
        need(after[1] is None or after[1] <= (before[1] or 0), 'A backup_transfer_leases row remains after the cancel.')

    def retry(self, d):
        a = self.args
        job = self.job('w', self.db('w', a.small_mb), 'retry', a.retry_limit)
        run = self.start(job)
        self.upload_started(run, d)
        a1d.restart_service()  # Restart-Service (not a bare stop): the Agent must come back for the retry attempt
        d['interruption'] = 'Restart-Service BackupAgent -Force'
        detail = self.finish(run)
        states = self.drive.states(detail)
        d.update(status=detail['status'], attempts=states)
        need(detail['status'] == 'Succeeded', f'The run ended as {detail["status"]} after the interruption.')
        need(len(detail['attempts']) >= 2 and detail['attempts'][0]['status'] in ('AgentLost', 'Failed'), 'Attempt 1 was not interrupted (AgentLost/Failed) with a retry attempt after it.')
        need(all(s[2] == 'Deleted' for s in states if s[1] != 'Succeeded'), 'The interrupted attempt left an artifact that is not Deleted.')
        d.update(self.measure(detail))
        self.assert_paced(d, a.retry_limit)
        good = self.drive.good_artifact(detail)
        sha, size = self.drive.download_sha(good['id'])
        d['downloadBytes'] = size
        need(sha == good['artifactChecksum'].lower() and size == d['sizeBytes'], 'The backend download differs from the artifact checksum/size.')
        need(not a1d.staging(), 'Staging files remain on the Windows Agent.')

    # --- cleanup, also after failures / Ctrl-C ---
    def sweep_leftovers(self):
        """Interrupted earlier run: drop bp_bw_* databases, disable + un-limit 'BW accept *' jobs. Artifacts of such jobs are not touched."""
        swept = {'databases': 0, 'jobs': 0}
        for kind in ('w', 'l'):
            try:
                for name in run_sql(kind, "SET NOCOUNT ON; SELECT name FROM sys.databases WHERE name LIKE N'bp[_]bw[_]%';\nGO\n"):
                    if DB_NAME.fullmatch(name):
                        self.drop(kind, name)
                        swept['databases'] += 1
            except (FixtureFailure, subprocess.SubprocessError, OSError):
                pass
        jobs = self.api.call('GET', self.tenant + '/backup-jobs')[1]
        for job in jobs['items'] if isinstance(jobs, dict) else jobs:
            if job['name'].startswith(JOB_PREFIX):
                self.set_limit('jobs', job['id'], None)
                if job.get('enabled', True):
                    self.api.call('POST', f'{self.tenant}/backup-jobs/{job["id"]}/disable', {}, expect=(200,))
                swept['jobs'] += 1
        return swept

    def cleanup(self):
        problems = []

        def attempt(label, action):
            try:
                action()
            except (FixtureFailure, KeyError, OSError, subprocess.SubprocessError) as failure:
                problems.append(f'{label}: {str(failure)[:150]}')
        for scope, scope_id in dict.fromkeys(self.limits):
            attempt(f'limit {scope}', lambda s=scope, i=scope_id: self.set_limit(s, i, self.original_tenant if s == 'tenant' else None))
        for artifact in self.artifacts:
            attempt('artifact ' + artifact[:8], lambda x=artifact: self.drive.delete(x))
        for job in self.jobs:
            attempt('job ' + job[:8], lambda j=job: self.api.call('POST', f'{self.tenant}/backup-jobs/{j}/disable', {}, expect=(200,)))
        for (kind, _), name in self.dbs.items():
            attempt('database ' + name, lambda k=kind, n=name: self.drop(k, n))
        remaining = []
        attempt('verify', lambda: remaining.extend([self.settings()['tenant']['bytesPerSecond'], grants(self.tenant_id)[1]]))
        if remaining:
            if remaining[0] != self.original_tenant:
                problems.append(f'tenant limit is {remaining[0]}, expected {self.original_tenant}')
            if remaining[1]:
                problems.append(f'{remaining[1]} grant row(s) remain')
        record('cleanup', 'FAIL' if problems else 'PASS', limitsRemoved=len(set(self.limits)), artifactsDeleted=len(self.artifacts), jobsDisabled=len(self.jobs), databasesDropped=len(self.dbs),
               note='jobs cannot be deleted through the API; they stay disabled', problems=problems)


def scenario(name, function):
    detail = {}
    try:
        function(detail)
        record(name, 'PASS', **detail)
    except NotRun as reason:
        record(name, 'NOT RUN', reason=str(reason), **detail)
    except (FixtureFailure, KeyError, StopIteration, ValueError, OSError, subprocess.SubprocessError) as failure:
        record(name, 'FAIL', reason=type(failure).__name__ + ': ' + str(failure)[:400], **detail)


def summary():
    counts = {status: sum(1 for _, s in results if s == status) for status in ('PASS', 'FAIL', 'NOT RUN')}
    print(json.dumps({'summary': counts, 'scenarios': dict(results)}), flush=True)
    return 1 if counts['FAIL'] else 2 if counts['NOT RUN'] else 0


# --- preflight -------------------------------------------------------------------------------------------------------------------------------------------------
def preflight():
    global label
    label, state = 'check', {}

    def check(name, function):
        try:
            detail = function() or {}
            record(name, 'PASS', **detail)
        except Warn as warning:
            record(name, 'WARN', reason=str(warning))
        except (FixtureFailure, KeyError, StopIteration, ValueError, OSError, subprocess.SubprocessError) as failure:
            record(name, 'FAIL', reason=type(failure).__name__ + ': ' + str(failure)[:400])

    def stack():
        load_a1()
        state['api'], state['tenant'] = dev.session()
        return {'tenant': state['tenant']}

    def vm():
        done = subprocess.run([str(HERE / 'check-vm.sh'), '--require-agent'], capture_output=True, text=True, timeout=240)
        need(done.returncode == 0, f'check-vm.sh --require-agent failed (exit {done.returncode}): ' + ' '.join((done.stdout + done.stderr).split())[-250:])
        return {'checkVm': 'ok'}

    def agents():
        listed = state['api'].call('GET', state['tenant'] + '/agents')[1]
        listed = listed if isinstance(listed, list) else listed['items']
        found = {a['hostName']: a for a in listed if a['hostName'] in ('dev-windows-agent', 'dev-linux-agent')}
        capability = 'backup.upload-bandwidth-limit'
        info = {n: {'connectivity': a['connectivity'], 'version': a.get('agentVersion'), 'uploadBandwidthCapability': capability in a.get('capabilities', [])} for n, a in found.items()}
        need(found.get('dev-windows-agent', {}).get('connectivity') == 'online', 'dev-windows-agent is not online (VM service / enrollment).')
        state['agents'] = info
        missing = [n for n, i in info.items() if i['connectivity'] == 'online' and not i['uploadBandwidthCapability']]
        if found.get('dev-linux-agent', {}).get('connectivity') != 'online':
            raise Warn(f'{info}: dev-linux-agent is not online; the tenant scenario would be NOT RUN (scripts/dev/agent.sh start).')
        if missing:
            raise Warn(f'{info}: {missing} do not report backup.upload-bandwidth-limit (rebuild/restart them if the backend still gates limited runs on it).')
        return info

    def drive_connection():
        connection = dev.pick_connection(state['api'], state['tenant'], 'google-drive')
        return {'connection': connection['name'], 'effectiveEnabled': connection['effectiveEnabled'], 'hasSecret': connection['hasSecret']}

    def limits_api():
        status, document = state['api'].call('GET', state['tenant'] + '/upload-bandwidth', expect=(200, 404))
        need(status == 200, 'GET upload-bandwidth returned HTTP 404: the running dev API has no Faz 2A endpoints (rebuild and restart it: scripts/dev/start.sh).')
        return {'min': document['minimumBytesPerSecond'], 'max': document['maximumBytesPerSecond'], 'tenantLimit': document['tenant']['bytesPerSecond'],
                'agentLimits': len(document['agents']), 'jobLimits': len(document['jobs'])}

    def postgres():
        tables = [r[0] for r in pg("SELECT relname FROM pg_class WHERE relname IN ('upload_bandwidth_grants','upload_bandwidth_limits','backup_destination_outcomes','backup_transfer_leases') AND relkind='r'")]
        required = {'upload_bandwidth_grants', 'upload_bandwidth_limits', 'backup_destination_outcomes'}
        need(required <= set(tables), 'Missing table(s) in the dev database (migration UploadBandwidthLimits not applied?): ' + ', '.join(sorted(required - set(tables))))
        return {'tables': sorted(tables), 'backupTransferLeases': 'backup_transfer_leases' in tables}

    def sql_servers():
        info = {'windowsVmSql': win.sql('SET NOCOUNT ON; SELECT 1;\nGO\n')[-1] == '1'}
        info['linuxSql'] = linux_sql('SET NOCOUNT ON; SELECT 1;\nGO\n')[-1] == '1'
        need(info['windowsVmSql'], 'The Windows VM SQL Server did not answer.')
        if not info['linuxSql']:
            raise Warn('The Linux Agent source SQL Server did not answer; the tenant scenario would be NOT RUN.')
        return info

    check('dev-stack-api-login', stack)
    if 'api' in state:
        check('dev-database-tables', postgres)
        check('agents', agents)
        check('google-drive-connection', drive_connection)
        check('upload-bandwidth-api', limits_api)
        check('sql-servers', sql_servers)
    check('windows-vm', vm)
    counts = {s: sum(1 for _, x in results if x == s) for s in ('PASS', 'WARN', 'FAIL')}
    print(json.dumps({'preflight': counts}), flush=True)
    return 1 if counts['FAIL'] else 0


def main():
    args = parse()
    if args.preflight:
        return preflight()
    load_a1()
    try:
        bench = Bench(args)
    except (FixtureFailure, KeyError) as failure:
        record('setup', 'FAIL', reason=f'{type(failure).__name__}: {str(failure)[:300]} (run --preflight)')
        return 1
    with dev.unlimited_while_unsized():
        try:
            record('sweep-leftovers', 'PASS', **bench.sweep_leftovers())
            table = {'baseline': bench.baseline, 'low': lambda d: bench.limited(d, args.small_mb, args.low_limit, 'low'),
                     'medium': lambda d: bench.limited(d, args.big_mb, args.medium_limit, 'medium'), 'tenant': bench.tenant, 'cancel': bench.cancel, 'retry': bench.retry}
            for name in args.scenarios:
                scenario(name, table[name])
        finally:
            bench.cleanup()
    return summary()


if __name__ == '__main__':
    sys.exit(main())
