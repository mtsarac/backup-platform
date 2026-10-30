import json
import runpy
import sys
import tempfile
import unittest
import uuid
from pathlib import Path
from types import ModuleType, SimpleNamespace
from unittest.mock import Mock, patch

HERE = Path(__file__).resolve().parent


def module(name, **attributes):
    result = ModuleType(name)
    result.__dict__.update(attributes)
    return result


def scenario(name):
    def decorate(fn):
        fn.scenario = name
        return fn
    return decorate


class QuotaRuns(unittest.TestCase):
    def setUp(self):
        self.lifecycle = Mock()
        modules = {
            'lib': module('lib', ST={}, scenario=scenario),
            'flows': module('flows'), 'ops': module('ops', lc=self.lifecycle),
            'api': module('api'),
        }
        with patch.dict(sys.modules, modules):
            namespace = runpy.run_path(str(HERE / 'quota.py'))
            self.run_once = namespace['run_with_headroom']
            self.quota = namespace['quota']

    def test_failed_run_is_returned_without_retry(self):
        response = SimpleNamespace(status_code=202, json=lambda: {'id': 'run'})
        tenant = SimpleNamespace(raw=Mock(return_value=response))
        failure = {'status': 'Failed', 'attempts': [{'reasonCode': 'QuotaExceeded'}]}
        self.lifecycle.return_value = failure
        self.assertEqual(self.run_once(tenant, {'id': 'job'}), (response, failure))
        tenant.raw.assert_called_once_with('POST', '/backup-jobs/job/runs', {})
        self.lifecycle.assert_called_once_with(tenant, 'run', 300)

    def test_admission_refusal_is_returned_without_polling_or_retry(self):
        response = SimpleNamespace(status_code=409)
        tenant = SimpleNamespace(raw=Mock(return_value=response))
        self.assertEqual(self.run_once(tenant, {'id': 'job'}), (response, None))
        tenant.raw.assert_called_once()
        self.lifecycle.assert_not_called()

    def test_unrelated_failure_is_not_accepted_as_quota_block(self):
        size = 64 * 1024 * 1024
        seed = {'id': 'seed', 'sizeBytes': size}
        artifacts = [{'id': str(i), 'sizeBytes': size} for i in range(2)]
        ops = self.quota.__globals__['ops']
        self.quota.__globals__['flows'].file_job_config = Mock(return_value={})
        ops.start = Mock(return_value='seed-run')
        ops.artifacts_of = Mock(side_effect=[[seed], [artifacts[0]], [artifacts[1]]])
        ops.lc.return_value = {'status': 'Succeeded', 'attempts': [{}]}
        self.quota.__globals__['flows'].local_dest = Mock()
        self.quota.__globals__['override'] = Mock()
        self.quota.__globals__['time'] = SimpleNamespace(sleep=Mock())
        success = {'status': 'Succeeded', 'attempts': [{}]}
        failed = {'status': 'Failed', 'attempts': [{'reasonCode': 'StorageUnavailable', 'reason': 'offline'}]}
        self.quota.__globals__['run_with_headroom'] = Mock(side_effect=[(None, success), (None, success), (None, failed)])
        tenant = SimpleNamespace(agents=lambda: {'fedora-e2e': {'agentId': 'agent'}},
                                 call=Mock(side_effect=[{'id': 'job'}, None, {'usedBytes': 0}, {'usedBytes': 2 * size}]))
        with self.assertRaisesRegex(AssertionError, 'other than quota'):
            self.quota(tenant)
        self.assertEqual(self.quota.__globals__['run_with_headroom'].call_count, 3)
        self.quota.__globals__['override'].assert_called_with({'kind': 'none'})



class BreakGlassAdmin(unittest.TestCase):
    def test_stale_admin_is_recreated_before_login(self):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            state = root / 'docs/test-environment/state.json'
            state.parent.mkdir(parents=True)
            state.write_text(json.dumps({'admin2': {'userId': str(uuid.uuid4()), 'login': 'old', 'password': 'old'}}))
            uid = str(uuid.uuid4())
            response = SimpleNamespace(status_code=201, json=lambda: {'id': uid})
            session = SimpleNamespace(post=Mock(return_value=response))
            login = Mock(side_effect=[session, 'new-session'])
            api = module('api', ROOT=root, BASE='https://example.invalid', login=login)
            modules = {'api': api, 'lib': module('lib', ST={}, scenario=scenario), 'flows': module('flows')}
            with patch.dict(sys.modules, modules):
                namespace = runpy.run_path(str(HERE / 'breakglass.py'))
            result = SimpleNamespace(returncode=0, stdout='f\n', stderr='')
            with patch.object(namespace['subprocess'], 'run', return_value=result) as run:
                self.assertEqual(namespace['second_admin'](), 'new-session')
            admin = json.loads(state.read_text())['admin2']
            self.assertEqual(admin['userId'], uid)
            self.assertNotEqual(admin['login'], 'old')
            login.assert_called_with(admin['login'], admin['password'])
            self.assertEqual(run.call_count, 2)


class AcceptanceRouting(unittest.TestCase):
    def invoke(self, *args):
        large = Mock()
        large.scenario = 'matrix/large/fedora-pg/sftp/~1GB'
        matrix_main = Mock()
        tenant = Mock()
        modules = {'api': module('api', BASE='https://example.invalid'),
                   'lib': module('lib', scenario=scenario, Tenant=Mock(return_value=tenant)),
                   'matrix': module('matrix', main=matrix_main),
                   'large': module('large', large_cell=large)}
        for name, function in [('mtls', 'mtls'), ('central', 'central'),
                               ('breakglass', 'break_glass'), ('webrtc', 'webrtc_direct'),
                               ('selfupdate', 'self_update')]:
            fn = Mock()
            fn.scenario = 'other/' + name
            modules[name] = module(name, **{function: fn})
        with patch.dict(sys.modules, modules), patch.object(sys, 'argv', ['acceptance.py', *args]):
            runpy.run_path(str(HERE / 'acceptance.py'), run_name='__main__')
        return large, matrix_main

    def test_large_filter_runs_large_and_is_forwarded_to_matrix(self):
        large, matrix_main = self.invoke('large')
        large.assert_called_once_with()
        matrix_main.assert_called_once_with('large')

    def test_unrelated_filter_skips_large(self):
        large, matrix_main = self.invoke('not-a-scenario')
        large.assert_not_called()
        matrix_main.assert_called_once_with('not-a-scenario')

    def test_matrix_filter_includes_large(self):
        large, matrix_main = self.invoke('matrix')
        large.assert_called_once_with()
        matrix_main.assert_called_once_with('matrix')


if __name__ == '__main__':
    unittest.main()
