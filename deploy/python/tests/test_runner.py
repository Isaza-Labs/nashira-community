"""End-to-end shape tests for the runner. We don't open SSH connections
here — instead we drive `_run_netmiko` / `_run_direct` indirectly by
asserting that the dispatch + normalization in `_run` plumbs the right
fields, and that the response shape matches the documented contract."""

import json
import subprocess
import sys

import pytest
from pathlib import Path

DEPLOY_PY = Path(__file__).resolve().parent.parent
RUNNER = DEPLOY_PY / "nashira_ssh_runner.py"


def test_list_platforms_subcommand_returns_json():
    """`python nashira_ssh_runner.py --list-platforms` must produce
    valid JSON with the expected shape so the C# endpoint can cache it."""
    proc = subprocess.run(
        [sys.executable, str(RUNNER), "--list-platforms"],
        capture_output=True, text=True, timeout=30,
        env={"PYTHONPATH": str(DEPLOY_PY)},
    )
    assert proc.returncode == 0, proc.stderr
    payload = json.loads(proc.stdout)
    assert "platforms" in payload
    assert isinstance(payload["platforms"], list)
    sr = next(p for p in payload["platforms"] if p["value"] == "nokia_srl")
    assert sr["vendor"] == "nokia"
    assert sr["has_local_templates"] is True


def test_runner_rejects_empty_stdin():
    proc = subprocess.run(
        [sys.executable, str(RUNNER)],
        input="", capture_output=True, text=True, timeout=10,
        env={"PYTHONPATH": str(DEPLOY_PY)},
    )
    assert proc.returncode == 1
    assert "empty stdin" in proc.stderr


def test_runner_rejects_malformed_input():
    proc = subprocess.run(
        [sys.executable, str(RUNNER)],
        input="not json", capture_output=True, text=True, timeout=10,
        env={"PYTHONPATH": str(DEPLOY_PY)},
    )
    assert proc.returncode == 1
    assert "input parse error" in proc.stderr


# --- session preparation -------------------------------------------------------
#
# The runner opens the Netmiko connection by hand so it can install the host-key
# policy first. That path does not run Netmiko's per-vendor session preparation
# unless the runner asks for it, and for a while it did not: paging stayed on and
# a switch that greets with "Press any key to continue" never reached a prompt.

class _FakeConn:
    def __init__(self, calls, prep_raises=None):
        self.calls, self.prep_raises, self.remote_conn_pre = calls, prep_raises, None

    def establish_connection(self):
        self.calls.append("connect")

    def session_preparation(self):
        self.calls.append("prepare")
        if self.prep_raises:
            raise self.prep_raises

    def send_command(self, cmd, **kwargs):
        self.calls.append(f"send:{cmd}")
        return "ok"

    def disconnect(self):
        self.calls.append("disconnect")


def _run_with_fake(monkeypatch, prep_raises=None):
    netmiko = pytest.importorskip("netmiko")
    pytest.importorskip("paramiko")
    import nashira_ssh_runner as runner

    calls: list[str] = []
    monkeypatch.setattr(netmiko, "ConnectHandler", lambda **kw: _FakeConn(calls, prep_raises))
    result = runner._run_netmiko({
        "host": "192.0.2.10", "username": "netops", "password": "x",
        "device_type": "hp_procurve", "commands": ["show version"],
    })
    return calls, result


def test_netmiko_session_is_prepared_before_the_first_command(monkeypatch):
    calls, result = _run_with_fake(monkeypatch)
    assert calls[:3] == ["connect", "prepare", "send:show version"]
    assert result["session_preparation_error"] is None
    assert result["results"][0]["ok"] is True


def test_a_failed_session_preparation_is_recorded_and_the_commands_still_run(monkeypatch):
    calls, result = _run_with_fake(monkeypatch, prep_raises=RuntimeError("no prompt"))
    assert "send:show version" in calls                      # the run was not abandoned
    assert result["session_preparation_error"] == "RuntimeError: no prompt"
    assert result["results"][0]["ok"] is True
