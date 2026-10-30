import json
import runpy
import sys
import unittest
from pathlib import Path
from types import ModuleType, SimpleNamespace
from unittest.mock import Mock, patch
import requests

HERE = Path(__file__).resolve().parent


def module(name, **attributes):
    result = ModuleType(name)
    result.__dict__.update(attributes)
    return result


class ApiWaitReadyTests(unittest.TestCase):
    def setUp(self):
        self.api = runpy.run_path(str(HERE / "api.py"))

    def test_wait_ready_immediate_success(self):
        mock_response = SimpleNamespace(
            status_code=200,
            json=lambda: {"headerName": "X-CSRF-TOKEN", "requestToken": "tok-123"},
            text='{"headerName":"X-CSRF-TOKEN","requestToken":"tok-123"}',
            headers={"content-type": "application/json"},
        )
        with patch("requests.get", return_value=mock_response) as mock_get:
            result = self.api["wait_ready"](timeout=2, interval=0.01)
            self.assertTrue(result)
            self.assertEqual(mock_get.call_count, 1)

    def test_wait_ready_retries_transient_502_bad_gateway_then_succeeds(self):
        bad_502 = SimpleNamespace(
            status_code=502,
            json=Mock(side_effect=ValueError("bad json")),
            text="<html><head><title>502 Bad Gateway</title></head><body>502 Bad Gateway</body></html>",
            headers={"content-type": "text/html"},
        )
        ok_200 = SimpleNamespace(
            status_code=200,
            json=lambda: {"headerName": "X-CSRF-TOKEN", "requestToken": "tok-123"},
            text='{"headerName":"X-CSRF-TOKEN","requestToken":"tok-123"}',
            headers={"content-type": "application/json"},
        )
        with patch("requests.get", side_effect=[bad_502, ok_200]) as mock_get:
            result = self.api["wait_ready"](timeout=5, interval=0.01)
            self.assertTrue(result)
            self.assertEqual(mock_get.call_count, 2)

    def test_wait_ready_retries_transient_connection_error_then_succeeds(self):
        ok_200 = SimpleNamespace(
            status_code=200,
            json=lambda: {"headerName": "X-CSRF-TOKEN", "requestToken": "tok-123"},
            text='{"headerName":"X-CSRF-TOKEN","requestToken":"tok-123"}',
            headers={"content-type": "application/json"},
        )
        with patch("requests.get", side_effect=[requests.exceptions.ConnectionError("Connection refused"), ok_200]) as mock_get:
            result = self.api["wait_ready"](timeout=5, interval=0.01)
            self.assertTrue(result)
            self.assertEqual(mock_get.call_count, 2)

    def test_wait_ready_retries_non_json_200_then_succeeds(self):
        non_json_200 = SimpleNamespace(
            status_code=200,
            json=Mock(side_effect=ValueError("invalid json")),
            text="<html>default page</html>",
            headers={"content-type": "text/html"},
        )
        ok_200 = SimpleNamespace(
            status_code=200,
            json=lambda: {"headerName": "X-CSRF-TOKEN", "requestToken": "tok-123"},
            text='{"headerName":"X-CSRF-TOKEN","requestToken":"tok-123"}',
            headers={"content-type": "application/json"},
        )
        with patch("requests.get", side_effect=[non_json_200, ok_200]) as mock_get:
            result = self.api["wait_ready"](timeout=5, interval=0.01)
            self.assertTrue(result)
            self.assertEqual(mock_get.call_count, 2)

    def test_wait_ready_timeout_raises_accurate_error_with_status_and_body(self):
        bad_502 = SimpleNamespace(
            status_code=502,
            json=Mock(side_effect=ValueError("bad json")),
            text="<html><head><title>502 Bad Gateway</title></head></html>",
            headers={"content-type": "text/html"},
        )
        with patch("requests.get", return_value=bad_502):
            with self.assertRaises(TimeoutError) as ctx:
                self.api["wait_ready"](timeout=0.02, interval=0.01)
            msg = str(ctx.exception)
            self.assertIn("502", msg)
            self.assertIn("text/html", msg)
            self.assertIn("/api/auth/antiforgery", msg)

    def test_login_fails_immediately_on_502_with_accurate_error(self):
        bad_502 = SimpleNamespace(
            status_code=502,
            json=Mock(side_effect=ValueError("bad json")),
            text="<html><head><title>502 Bad Gateway</title></head></html>",
            headers={"content-type": "text/html"},
        )
        mock_session = Mock()
        mock_session.get.return_value = bad_502
        with patch("requests.Session", return_value=mock_session):
            with self.assertRaises(RuntimeError) as ctx:
                self.api["login"]("admin", "secret")
            msg = str(ctx.exception)
            self.assertIn("HTTP 502", msg)
            self.assertIn("text/html", msg)
            mock_session.get.assert_called_once()
            mock_session.post.assert_not_called()

    def test_login_fails_immediately_on_non_json_200_with_accurate_error(self):
        non_json = SimpleNamespace(
            status_code=200,
            json=Mock(side_effect=ValueError("Expecting value")),
            text="<html>not json</html>",
            headers={"content-type": "text/html"},
        )
        mock_session = Mock()
        mock_session.get.return_value = non_json
        with patch("requests.Session", return_value=mock_session):
            with self.assertRaises(RuntimeError) as ctx:
                self.api["login"]("admin", "secret")
            msg = str(ctx.exception)
            self.assertIn("was not JSON", msg)
            self.assertIn("200", msg)
            mock_session.get.assert_called_once()
            mock_session.post.assert_not_called()


class AcceptanceReadinessHookTests(unittest.TestCase):
    def test_acceptance_runs_wait_ready_before_tenant_and_scenarios(self):
        call_order = []

        def mock_wait_ready(timeout=120):
            call_order.append("wait_ready")
            return True

        def mock_tenant(key="a"):
            call_order.append("Tenant")
            return Mock()

        large = Mock()
        large.scenario = "matrix/large"
        matrix_main = Mock()

        api_mod = module("api", BASE="https://example.invalid", wait_ready=mock_wait_ready)
        def scen(name):
            def deco(f):
                f.scenario = name
                return f
            return deco
        lib_mod = module("lib", scenario=scen, Tenant=mock_tenant, ST={"a": {"tenantId": "t-1"}})
        modules = {
            "api": api_mod,
            "lib": lib_mod,
            "matrix": module("matrix", main=matrix_main),
            "large": module("large", large_cell=large),
        }
        for name, function in [
            ("mtls", "mtls"), ("central", "central"),
            ("breakglass", "break_glass"), ("webrtc", "webrtc_direct"),
            ("selfupdate", "self_update"),
        ]:
            fn = Mock()
            fn.scenario = "other/" + name
            modules[name] = module(name, **{function: fn})

        with patch.dict(sys.modules, modules), patch.object(sys, "argv", ["acceptance.py", "not-a-scenario"]):
            runpy.run_path(str(HERE / "acceptance.py"), run_name="__main__")

        self.assertEqual(call_order, ["wait_ready", "Tenant"])

    def test_acceptance_aborts_when_wait_ready_times_out(self):
        def failing_wait_ready(timeout=120):
            raise TimeoutError("API at /api/auth/antiforgery was not ready within 120s (last error: HTTP 502)")

        tenant_mock = Mock()
        api_mod = module("api", BASE="https://example.invalid", wait_ready=failing_wait_ready)
        def scen(name):
            def deco(f):
                f.scenario = name
                return f
            return deco
        lib_mod = module("lib", scenario=scen, Tenant=tenant_mock, ST={"a": {"tenantId": "t-1"}})
        modules = {
            "api": api_mod,
            "lib": lib_mod,
            "matrix": module("matrix", main=Mock()),
            "large": module("large", large_cell=Mock(scenario="matrix/large")),
        }
        for name, function in [
            ("mtls", "mtls"), ("central", "central"),
            ("breakglass", "break_glass"), ("webrtc", "webrtc_direct"),
            ("selfupdate", "self_update"),
        ]:
            fn = Mock()
            fn.scenario = "other/" + name
            modules[name] = module(name, **{function: fn})

        with patch.dict(sys.modules, modules), patch.object(sys, "argv", ["acceptance.py"]):
            with self.assertRaises(TimeoutError) as ctx:
                runpy.run_path(str(HERE / "acceptance.py"), run_name="__main__")
            self.assertIn("not ready within", str(ctx.exception))

        tenant_mock.assert_not_called()


if __name__ == "__main__":
    unittest.main()
