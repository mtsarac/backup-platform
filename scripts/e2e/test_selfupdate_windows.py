import base64
import io
import re
import runpy
import sys
import tempfile
import unittest
import zipfile
from pathlib import Path
from types import ModuleType, SimpleNamespace
from unittest.mock import patch

from cryptography.hazmat.primitives import hashes
from cryptography.hazmat.primitives.asymmetric import ec, utils


def module(name, **attributes):
    result = ModuleType(name)
    result.__dict__.update(attributes)
    return result


def load():
    root = Path(__file__).resolve().parents[2]
    modules = {'api': module('api', ROOT=root, BASE='https://example.invalid'),
               'lib': module('lib', scenario=lambda name: lambda fn: fn),
               'lab-selfupdate': module('lab-selfupdate')}
    with patch.dict(sys.modules, modules):
        return runpy.run_path(str(Path(__file__).with_name('selfupdate.py')))


class WindowsUpdatePackage(unittest.TestCase):
    def test_package_contains_binary_but_not_runtime_settings(self):
        with tempfile.TemporaryDirectory() as tmp:
            root = Path(tmp)
            (root / 'BackupPlatform.Agent.Host.exe').write_bytes(b'agent')
            (root / 'appsettings.Production.json').write_text('{}')
            (root / 'nested').mkdir()
            (root / 'nested' / 'library.dll').write_bytes(b'library')
            package = load()['windows_package'](root)
        with zipfile.ZipFile(io.BytesIO(package)) as archive:
            self.assertEqual(set(archive.namelist()), {'BackupPlatform.Agent.Host.exe', 'nested/library.dll'})
            self.assertEqual(archive.read('nested/library.dll'), b'library')

    def test_windows_manifest_is_signed_for_windows_package_url(self):
        sign = load()['sign']
        key = ec.generate_private_key(ec.SECP256R1())
        manifest = sign('1.0.1', b'package', key, rid='win-x64')
        payload = manifest['payload']
        self.assertEqual(payload['platform'], 'win-x64')
        self.assertEqual(payload['downloadUrl'], 'https://example.invalid/api/agent/updates/packages/win-x64/1.0.1')
        from datetime import datetime
        timestamp = int(datetime.fromisoformat(payload['publishedAt']).timestamp())
        canonical = f"win-x64:1.0.1:1.0.0:{payload['packageSha256']}:7:{payload['downloadUrl']}:{timestamp}".encode()
        signature = base64.b64decode(manifest['signatureBase64'])
        self.assertEqual(len(signature), 64)
        key.public_key().verify(utils.encode_dss_signature(int.from_bytes(signature[:32], 'big'), int.from_bytes(signature[32:], 'big')), canonical, ec.ECDSA(hashes.SHA256()))

    def test_remote_script_is_run_to_completion_and_secrets_are_redacted(self):
        windows_ps = load()['windows_ps']
        sent = {}

        def finished(command, input, **_):
            sent['input'] = input
            return SimpleNamespace(returncode=0, stdout='out\n' + re.search(r"Write-Output '([0-9a-f]{32})'", input).group(1) + '\n', stderr='')

        with patch('subprocess.run', finished):
            self.assertEqual(windows_ps('host', 'Write-Output out'), 'out')
        # PowerShell holds a trailing try/catch back from stdin until a blank line; without it the script never runs.
        self.assertTrue(sent['input'].endswith('}\n\n'))
        with patch('subprocess.run', lambda *a, **k: SimpleNamespace(returncode=0, stdout='', stderr='')):
            with self.assertRaisesRegex(AssertionError, 'Windows command failed'):
                windows_ps('host', 'x')
        with patch('subprocess.run', lambda *a, **k: SimpleNamespace(returncode=1, stdout='', stderr='bad token s3cret')):
            with self.assertRaises(AssertionError) as raised:
                windows_ps('host', 'x', secrets=('s3cret',))
        self.assertIn('[redacted]', str(raised.exception))
        self.assertNotIn('s3cret', str(raised.exception))


if __name__ == '__main__':
    unittest.main()
