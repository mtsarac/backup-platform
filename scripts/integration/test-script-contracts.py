#!/usr/bin/env python3
"""Exercise evidence rejection and, optionally, the real Windows SSH wrappers."""
import argparse
import hashlib
import json
import os
import subprocess
import tempfile
import unittest
import uuid
from pathlib import Path

REPO = Path(__file__).resolve().parents[2]
CHECKER = REPO / 'scripts/lib/assert-test-results.py'


class TestEvidence(unittest.TestCase):
    def check_result(self, total, executed, passed, outcomes, expected_success):
        rows = ''.join(f'<UnitTestResult outcome="{outcome}" />' for outcome in outcomes)
        content = f'''<TestRun xmlns="http://microsoft.com/schemas/VisualStudio/TeamTest/2010"><Results>{rows}</Results><ResultSummary><Counters total="{total}" executed="{executed}" passed="{passed}" /></ResultSummary></TestRun>'''
        with tempfile.TemporaryDirectory(prefix='bp-script-evidence-') as directory:
            path = Path(directory) / 'results.trx'
            path.write_text(content)
            result = subprocess.run(['python3', str(CHECKER), str(path)], capture_output=True, text=True, timeout=10, check=False)
        self.assertEqual(result.returncode == 0, expected_success, result.stdout + result.stderr)

    def test_executed_passing_selection_is_accepted(self):
        self.check_result(2, 2, 2, ['Passed', 'Passed'], True)

    def test_zero_selected_tests_is_not_success(self):
        self.check_result(0, 0, 0, [], False)

    def test_all_skipped_tests_is_not_success(self):
        self.check_result(2, 0, 0, ['NotExecuted', 'NotExecuted'], False)

    def test_partially_skipped_selection_is_not_full_acceptance(self):
        self.check_result(2, 1, 1, ['Passed', 'NotExecuted'], False)

    def test_failed_test_is_not_success(self):
        self.check_result(2, 2, 1, ['Passed', 'Failed'], False)

    def test_incomplete_evidence_is_rejected(self):
        self.check_result(2, 2, 2, ['Passed'], False)

    def test_missing_evidence_is_rejected(self):
        with tempfile.TemporaryDirectory(prefix='bp-script-evidence-') as directory:
            result = subprocess.run(['python3', str(CHECKER), str(Path(directory) / 'absent.trx')], capture_output=True, timeout=10, check=False)
        self.assertNotEqual(result.returncode, 0)


class RuntimeCleanup(unittest.TestCase):
    def test_original_failure_survives_successful_preceding_cleanup(self):
        with tempfile.TemporaryDirectory(prefix='bp-cleanup-contract-') as directory:
            owned = Path(directory) / 'owned-runtime'
            owned.mkdir()
            (owned / 'credential-fixture').write_text('disposable fixture')
            result = subprocess.run([
                'bash', '-c',
                'source "$1"; register_cleanup_dir "$2"; true; cleanup_runtime 7',
                'cleanup-contract', str(REPO / 'scripts/lib/common.sh'), str(owned),
            ], capture_output=True, text=True, timeout=10, check=False)
            self.assertEqual(result.returncode, 7)
            self.assertFalse(owned.exists())


class BackendRunners(unittest.TestCase):
    """Exercise real runner routing and fail-closed evidence without building .NET."""

    def setUp(self):
        self.runtime = tempfile.TemporaryDirectory(prefix='bp-runner-contract-')
        self.addCleanup(self.runtime.cleanup)
        self.root = Path(self.runtime.name)
        self.bin = self.root / 'bin'
        self.bin.mkdir()
        self.log = self.root / 'dotnet-calls.jsonl'
        fake_dotnet = self.bin / 'dotnet'
        fake_dotnet.write_text('''#!/usr/bin/env python3
import json
import os
from pathlib import Path
import sys

arguments = sys.argv[1:]
with open(os.environ['BP_CONTRACT_CALLS'], 'a') as calls:
    calls.write(json.dumps(arguments) + '\\n')
failure = int(os.environ.get('BP_CONTRACT_DOTNET_EXIT', '0'))
if failure:
    sys.exit(failure)
directory = Path(arguments[arguments.index('--results-directory') + 1])
outcome = os.environ.get('BP_CONTRACT_OUTCOME', 'Passed')
total = 0 if outcome == 'Empty' else 1
executed = 0 if outcome in ('Empty', 'NotExecuted') else 1
passed = 1 if outcome == 'Passed' else 0
rows = '' if outcome == 'Empty' else f'<UnitTestResult outcome=\"{outcome}\" />'
(directory / 'result.trx').write_text(f'<TestRun xmlns=\"http://microsoft.com/schemas/VisualStudio/TeamTest/2010\"><Results>{rows}</Results><ResultSummary><Counters total=\"{total}\" executed=\"{executed}\" passed=\"{passed}\" /></ResultSummary></TestRun>')
''')
        fake_dotnet.chmod(0o700)
        self.environment = {
            **os.environ,
            'PATH': str(self.bin) + os.pathsep + os.environ['PATH'],
            'BP_CREDENTIAL_FILE': str(self.root / 'absent-credentials'),
            'BP_CONTRACT_CALLS': str(self.log),
            'BACKUP_PLATFORM_TEST_CONNECTION_STRING': 'Host=contract-not-used',
            'TMPDIR': str(self.root),
        }
        self.environment.pop('BP_COMMON_LOADED', None)

    def invoke(self, runner, *arguments, **overrides):
        return subprocess.run(
            [str(REPO / 'scripts/integration' / runner), *arguments],
            env={**self.environment, **overrides}, capture_output=True, text=True, timeout=10, check=False)

    def calls(self):
        return [json.loads(line) for line in self.log.read_text().splitlines()] if self.log.exists() else []

    def test_help_requires_neither_dotnet_execution_nor_database_credentials(self):
        for runner in ('run-backend-tests.sh', 'run-postgres-tests.sh'):
            with self.subTest(runner=runner):
                result = self.invoke(runner, '--help', BACKUP_PLATFORM_TEST_CONNECTION_STRING='')
                self.assertEqual(result.returncode, 0, result.stderr)
                self.assertIn('Usage:', result.stdout)
        self.assertEqual(self.calls(), [])

    def test_invalid_or_ambiguous_selections_fail_before_running_tests(self):
        options = [
            ('run-backend-tests.sh', '--project'),
            ('run-backend-tests.sh', '--project', 'integration'),
            ('run-backend-tests.sh', '--filter', 'FullyQualifiedName~Retention'),
            ('run-backend-tests.sh', '--project', 'unit', '--filter'),
            ('run-backend-tests.sh', '--project', 'unit', '--filter', ''),
            ('run-backend-tests.sh', '--project', 'agent', '--connectivity'),
            ('run-postgres-tests.sh', '--filter'),
            ('run-postgres-tests.sh', '--filter', ''),
            ('run-postgres-tests.sh', '--filter', '--no-build'),
        ]
        for runner, *arguments in options:
            with self.subTest(arguments=arguments):
                self.assertNotEqual(self.invoke(runner, *arguments).returncode, 0)
        self.assertEqual(self.calls(), [])

    def test_default_is_unit_architecture_and_filtered_agent_without_postgres(self):
        result = self.invoke('run-backend-tests.sh')
        self.assertEqual(result.returncode, 0, result.stdout + result.stderr)
        calls = self.calls()
        self.assertEqual([next(a for a in call if a.startswith('tests/')) for call in calls],
                         ['tests/unit-tests', 'tests/architecture-tests', 'tests/agent-tests'])
        self.assertTrue(all('--no-build' not in call for call in calls))
        self.assertTrue(all('--filter' not in call for call in calls[:2]))
        self.assertEqual(calls[2][calls[2].index('--filter') + 1],
                         'FullyQualifiedName!~ConnectivityServiceTests')

    def test_full_backend_selection_keeps_connectivity(self):
        result = self.invoke('run-backend-tests.sh', '--connectivity')
        self.assertEqual(result.returncode, 0, result.stdout + result.stderr)
        self.assertEqual(len(self.calls()), 3)
        self.assertTrue(all('--filter' not in call for call in self.calls()))

    def test_named_filtered_project_routes_one_build_and_reuses_only_explicitly(self):
        expression = 'FullyQualifiedName~Retention|FullyQualifiedName~DeleteReplica'
        for no_build in (False, True):
            with self.subTest(no_build=no_build):
                self.log.unlink(missing_ok=True)
                options = ['--project', 'unit', '--filter', expression]
                if no_build:
                    options.append('--no-build')
                result = self.invoke('run-backend-tests.sh', *options)
                self.assertEqual(result.returncode, 0, result.stdout + result.stderr)
                calls = self.calls()
                self.assertEqual(len(calls), 1)
                self.assertIn('tests/unit-tests', calls[0])
                self.assertEqual(calls[0][calls[0].index('--filter') + 1], expression)
                self.assertEqual('--no-build' in calls[0], no_build)

    def test_named_agent_project_does_not_silently_exclude_connectivity(self):
        result = self.invoke('run-backend-tests.sh', '--project', 'agent')
        self.assertEqual(result.returncode, 0, result.stdout + result.stderr)
        self.assertEqual(len(self.calls()), 1)
        self.assertIn('tests/agent-tests', self.calls()[0])
        self.assertNotIn('--filter', self.calls()[0])

    def test_postgres_filter_narrows_the_existing_acceptance_selection(self):
        expression = 'FullyQualifiedName~RetentionAudit|FullyQualifiedName~AgentReplicaDeletionAudit'
        result = self.invoke('run-postgres-tests.sh', '--filter', expression, '--no-build')
        self.assertEqual(result.returncode, 0, result.stdout + result.stderr)
        calls = self.calls()
        self.assertEqual(len(calls), 1)
        self.assertIn('tests/integration-tests', calls[0])
        self.assertIn('--no-build', calls[0])
        self.assertEqual(calls[0][calls[0].index('--filter') + 1],
                         f'(FullyQualifiedName!~MsSql&FullyQualifiedName!~BackupPipelineSmoke)&({expression})')

    def test_unfiltered_postgres_keeps_the_full_existing_acceptance_selection(self):
        result = self.invoke('run-postgres-tests.sh')
        self.assertEqual(result.returncode, 0, result.stdout + result.stderr)
        calls = self.calls()
        self.assertEqual(len(calls), 1)
        self.assertIn('tests/integration-tests', calls[0])
        self.assertNotIn('--no-build', calls[0])
        self.assertEqual(calls[0][calls[0].index('--filter') + 1],
                         'FullyQualifiedName!~MsSql&FullyQualifiedName!~BackupPipelineSmoke')

    def test_original_failure_stops_the_selection_and_is_preserved(self):
        result = self.invoke('run-backend-tests.sh', BP_CONTRACT_DOTNET_EXIT='7')
        self.assertEqual(result.returncode, 7, result.stdout + result.stderr)
        self.assertEqual(len(self.calls()), 1)

    def test_empty_skipped_and_failed_evidence_remain_failures(self):
        for outcome in ('Empty', 'NotExecuted', 'Failed'):
            with self.subTest(outcome=outcome):
                result = self.invoke('run-backend-tests.sh', '--project', 'unit', BP_CONTRACT_OUTCOME=outcome)
                self.assertNotEqual(result.returncode, 0, result.stdout + result.stderr)


class WindowsTransport(unittest.TestCase):
    def invoke(self, *arguments):
        return subprocess.run([str(REPO / 'scripts/windows/run-powershell.sh'), *arguments], capture_output=True, text=True, timeout=30, check=False)

    def test_remote_command_failures_propagate(self):
        result = self.invoke('--command', "throw 'Expected transport contract failure.'")
        self.assertNotEqual(result.returncode, 0)
        self.assertIn('Expected transport contract failure.', result.stdout + result.stderr)

    def test_file_payload_completes_without_waiting_for_stdin_eof(self):
        # A read-only existing script: no install, database edits or disposable account.
        result = self.invoke('--file', str(REPO / 'scripts/windows/Agent-Service.ps1'), '--', '-Action', 'status')
        self.assertEqual(result.returncode, 0)

    def test_file_failures_and_literal_arguments_propagate(self):
        sentinel = 'C:\\ProgramData\\BackupLiteralSmoke-' + uuid.uuid4().hex + '.txt'
        absent = self.invoke('--command', f"if (Test-Path -LiteralPath '{sentinel}') {{ throw 'Sentinel already exists.' }}")
        self.assertEqual(absent.returncode, 0)
        literal = f"C:\\invalid '$([IO.File]::WriteAllText('{sentinel}', 'unexpected'))' ; $null & path"
        try:
            result = self.invoke('--file', str(REPO / 'scripts/windows/Onboarding-Smoke.ps1'), '--', '-Action', 'prepare', '-RootDirectory', literal)
            self.assertNotEqual(result.returncode, 0)
            untouched = self.invoke('--command', f"if (Test-Path -LiteralPath '{sentinel}') {{ throw 'A literal argument executed.' }}")
            self.assertEqual(untouched.returncode, 0)
        finally:
            self.invoke('--command', f"if (Test-Path -LiteralPath '{sentinel}') {{ Remove-Item -LiteralPath '{sentinel}' -Force }}")

    def test_native_binary_transfer_checks_bytes_and_explicit_overwrite(self):
        root = 'C:\\ProgramData\\BackupTransportSmoke-' + uuid.uuid4().hex
        destination = root + '\\payload.bin'
        created = self.invoke('--command', f"New-Item -ItemType Directory -Path '{root}' | Out-Null")
        self.assertEqual(created.returncode, 0)
        try:
            with tempfile.TemporaryDirectory(prefix='bp-transport-contract-') as directory:
                path = Path(directory) / 'payload.bin'
                original = os.urandom(2 * 1024 * 1024) + bytes([0, 255, 13, 10])
                path.write_bytes(original)
                wrapper = str(REPO / 'scripts/windows/copy-to-vm.sh')
                copied = subprocess.run([wrapper, str(path), destination], capture_output=True, text=True, timeout=30, check=False)
                self.assertEqual(copied.returncode, 0, copied.stdout + copied.stderr)
                original_hash = hashlib.sha256(original).hexdigest().upper()
                observed = self.invoke('--command', f"(Get-FileHash -LiteralPath '{destination}' -Algorithm SHA256).Hash")
                self.assertIn(original_hash, observed.stdout)
                replacement = b'explicit replacement only' + bytes([0])
                path.write_bytes(replacement)
                refused = subprocess.run([wrapper, str(path), destination], capture_output=True, text=True, timeout=30, check=False)
                self.assertNotEqual(refused.returncode, 0)
                observed = self.invoke('--command', f"(Get-FileHash -LiteralPath '{destination}' -Algorithm SHA256).Hash")
                self.assertIn(original_hash, observed.stdout)
                replaced = subprocess.run([wrapper, '--force', str(path), destination], capture_output=True, text=True, timeout=30, check=False)
                self.assertEqual(replaced.returncode, 0, replaced.stdout + replaced.stderr)
                observed = self.invoke('--command', f"(Get-FileHash -LiteralPath '{destination}' -Algorithm SHA256).Hash")
                self.assertIn(hashlib.sha256(replacement).hexdigest().upper(), observed.stdout)
        finally:
            removed = self.invoke('--command', f"Remove-Item -LiteralPath '{root}' -Recurse -Force")
            self.assertEqual(removed.returncode, 0)


if __name__ == '__main__':
    parser = argparse.ArgumentParser()
    parser.add_argument('--windows', action='store_true', help='Also exercise the configured real Windows SSH VM (no installation).')
    options = parser.parse_args()
    suite = unittest.defaultTestLoader.loadTestsFromTestCase(TestEvidence)
    suite.addTests(unittest.defaultTestLoader.loadTestsFromTestCase(RuntimeCleanup))
    suite.addTests(unittest.defaultTestLoader.loadTestsFromTestCase(BackendRunners))
    if options.windows:
        suite.addTests(unittest.defaultTestLoader.loadTestsFromTestCase(WindowsTransport))
    result = unittest.TextTestRunner(verbosity=2).run(suite)
    raise SystemExit(0 if result.wasSuccessful() else 1)
