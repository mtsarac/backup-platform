#!/usr/bin/env python3
"""Exercise evidence rejection and, optionally, the real Windows SSH wrappers."""
import argparse
import hashlib
import os
from pathlib import Path
import subprocess
import tempfile
import unittest
import uuid

REPO = Path(__file__).resolve().parents[2]
CHECKER = REPO / 'scripts/lib/assert-test-results.py'


class TestEvidence(unittest.TestCase):
    def check_result(self, total, executed, passed, outcomes, expected_success):
        rows = ''.join(f'<UnitTestResult outcome="{outcome}" />' for outcome in outcomes)
        content = f'''<TestRun xmlns="http://microsoft.com/schemas/VisualStudio/TeamTest/2010"><Results>{rows}</Results><ResultSummary><Counters total="{total}" executed="{executed}" passed="{passed}" /></ResultSummary></TestRun>'''
        with tempfile.TemporaryDirectory(prefix='bp-script-evidence-') as directory:
            path = Path(directory) / 'results.trx'
            path.write_text(content)
            result = subprocess.run(['python3', str(CHECKER), str(path)], capture_output=True, text=True, timeout=10)
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
            result = subprocess.run(['python3', str(CHECKER), str(Path(directory) / 'absent.trx')], capture_output=True, timeout=10)
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
            ], capture_output=True, text=True, timeout=10)
            self.assertEqual(result.returncode, 7)
            self.assertFalse(owned.exists())


class WindowsTransport(unittest.TestCase):
    def invoke(self, *arguments):
        return subprocess.run([str(REPO / 'scripts/windows/run-powershell.sh'), *arguments], capture_output=True, text=True, timeout=30)

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
                copied = subprocess.run([wrapper, str(path), destination], capture_output=True, text=True, timeout=30)
                self.assertEqual(copied.returncode, 0, copied.stdout + copied.stderr)
                original_hash = hashlib.sha256(original).hexdigest().upper()
                observed = self.invoke('--command', f"(Get-FileHash -LiteralPath '{destination}' -Algorithm SHA256).Hash")
                self.assertIn(original_hash, observed.stdout)
                replacement = b'explicit replacement only' + bytes([0])
                path.write_bytes(replacement)
                refused = subprocess.run([wrapper, str(path), destination], capture_output=True, text=True, timeout=30)
                self.assertNotEqual(refused.returncode, 0)
                observed = self.invoke('--command', f"(Get-FileHash -LiteralPath '{destination}' -Algorithm SHA256).Hash")
                self.assertIn(original_hash, observed.stdout)
                replaced = subprocess.run([wrapper, '--force', str(path), destination], capture_output=True, text=True, timeout=30)
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
    if options.windows:
        suite.addTests(unittest.defaultTestLoader.loadTestsFromTestCase(WindowsTransport))
    result = unittest.TextTestRunner(verbosity=2).run(suite)
    raise SystemExit(0 if result.wasSuccessful() else 1)
