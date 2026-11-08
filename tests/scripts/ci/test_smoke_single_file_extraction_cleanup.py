"""Exercise the real smoke fixture's registered cleanup without native CLI execution."""
import errno
import importlib.util
import json
from pathlib import Path
import tempfile
from types import SimpleNamespace
import unittest
import zipfile

import pytest


SCRIPT = Path(__file__).resolve().parents[3] / "scripts/ci/smoke_single_file_extraction.py"


class Clock:
    def __init__(self):
        self.now = 0.0
        self.sleeps = []

    def monotonic(self):
        return self.now

    def sleep(self, delay):
        self.sleeps.append(delay)
        self.now = round(self.now + delay, 12)


def sharing_error(code=32):
    error = PermissionError(errno.EACCES, "synthetic sharing failure", "owned ppds.exe")
    if code is not None:
        error.winerror = code
    return error


def run_registered_cleanup(tmp_path, monkeypatch, errors, *, windows=True, first_call_time=0.0):
    spec = importlib.util.spec_from_file_location("smoke_cleanup_subject", SCRIPT)
    smoke = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(smoke)
    clock = Clock()
    sandbox = tempfile.TemporaryDirectory(prefix="owned-smoke-", dir=tmp_path)
    actual_cleanup = sandbox.cleanup
    attempts = []

    def interrupted_cleanup():
        attempts.append(clock.now)
        if len(attempts) == 1:
            clock.now = first_call_time
        error = errors[min(len(attempts) - 1, len(errors) - 1)]
        if error is not None:
            raise error
        actual_cleanup()

    monkeypatch.setattr(sandbox, "cleanup", interrupted_cleanup)
    # Replace only this module's bindings, never global os.name/pathlib behavior.
    monkeypatch.setattr(smoke, "os", SimpleNamespace(name="nt" if windows else "posix"))
    monkeypatch.setattr(smoke, "time", clock, raising=False)
    monkeypatch.setattr(smoke, "tempfile", SimpleNamespace(TemporaryDirectory=lambda **_: sandbox))
    local_data = tmp_path / "local-data"
    local_data.mkdir()
    monkeypatch.setattr(smoke, "subprocess", SimpleNamespace(run=lambda *_, **__: SimpleNamespace(
        stdout=json.dumps({"localApplicationData": str(local_data)}), stderr="")))
    archive = tmp_path / "references.zip"
    with zipfile.ZipFile(archive, "w") as output:
        output.writestr("mscorlib.dll", b"inert test fixture")
        output.writestr("System.Runtime.dll", b"inert test fixture")
    binary = tmp_path / "ppds.exe"
    binary.write_bytes(b"inert: never executed")

    class RegisteredCleanupCase(smoke.SingleFileExtractionTests):
        def setUp(self):
            # Only class setup and class cleanup are under test.
            pass

        def test_owned_sandbox(self):
            self.assertTrue(self.binary.is_file())

    RegisteredCleanupCase.binary_source = binary
    RegisteredCleanupCase.reference_archive = archive
    RegisteredCleanupCase.cache_probe = tmp_path / "unused-probe.dll"
    RegisteredCleanupCase.allow_existing_cache = False
    result = unittest.TestResult()
    try:
        unittest.TestSuite([RegisteredCleanupCase("test_owned_sandbox")]).run(result)
        assert result.testsRun == 1, result.errors
        assert not result.failures, result.failures
        observed_errors = [item[1] for item in RegisteredCleanupCase.tearDown_exceptions]
        return SimpleNamespace(result=result, errors=observed_errors, attempts=attempts,
                               sleeps=clock.sleeps, elapsed=clock.now,
                               removed=not Path(sandbox.name).exists())
    finally:
        # Delete only this test's inert owned sandbox, even for expected failures.
        actual_cleanup()


def test_registered_cleanup_succeeds_without_retry(tmp_path, monkeypatch):
    observed = run_registered_cleanup(tmp_path, monkeypatch, [None])
    assert observed.result.wasSuccessful()
    assert observed.removed
    assert observed.attempts == [0.0]
    assert observed.sleeps == []


@pytest.mark.parametrize("failures", [1, 2, 3])
def test_registered_cleanup_recovers_transient_sharing_violation(tmp_path, monkeypatch, failures):
    error = sharing_error()
    observed = run_registered_cleanup(tmp_path, monkeypatch, [error] * failures + [None])
    assert observed.result.wasSuccessful(), f"Registered cleanup failed: {observed.errors!r}"
    assert observed.removed
    assert len(observed.attempts) == failures + 1
    assert len(observed.sleeps) == failures
    assert all(0 < delay <= 0.1 for delay in observed.sleeps)


def test_registered_cleanup_preserves_error_at_deadline(tmp_path, monkeypatch):
    error = sharing_error()
    observed = run_registered_cleanup(tmp_path, monkeypatch, [error])
    assert not observed.result.wasSuccessful()
    assert observed.errors == [error]
    assert observed.errors[0].filename == "owned ppds.exe"
    assert observed.elapsed == pytest.approx(5.0)
    assert all(attempt < 5.0 for attempt in observed.attempts)
    assert all(0 < delay <= 0.1 for delay in observed.sleeps)
    assert len(observed.attempts) <= 51
    assert not observed.removed


def test_registered_cleanup_caps_last_sleep_at_remaining_deadline(tmp_path, monkeypatch):
    error = sharing_error()
    observed = run_registered_cleanup(tmp_path, monkeypatch, [error], first_call_time=4.95)
    assert observed.errors == [error]
    assert observed.attempts == [0.0]
    assert observed.sleeps == pytest.approx([0.05])
    assert observed.elapsed == pytest.approx(5.0)


def test_registered_cleanup_does_not_sleep_after_budget_used_by_cleanup(tmp_path, monkeypatch):
    error = sharing_error()
    observed = run_registered_cleanup(tmp_path, monkeypatch, [error], first_call_time=5.0)
    assert observed.errors == [error]
    assert observed.attempts == [0.0]
    assert observed.sleeps == []


@pytest.mark.parametrize("code", [5, 33, None])
def test_registered_cleanup_propagates_nonsharing_errors(tmp_path, monkeypatch, code):
    error = sharing_error(code)
    observed = run_registered_cleanup(tmp_path, monkeypatch, [error])
    assert observed.errors == [error]
    assert observed.attempts == [0.0]
    assert observed.sleeps == []


def test_registered_cleanup_does_not_retry_off_windows(tmp_path, monkeypatch):
    error = sharing_error()
    observed = run_registered_cleanup(tmp_path, monkeypatch, [error], windows=False)
    assert observed.errors == [error]
    assert observed.attempts == [0.0]
    assert observed.sleeps == []


def test_registered_cleanup_stops_on_unrelated_error_after_retry(tmp_path, monkeypatch):
    first = sharing_error()
    second = sharing_error(5)
    observed = run_registered_cleanup(tmp_path, monkeypatch, [first, second])
    assert observed.errors == [second]
    assert observed.attempts == [0.0, 0.1]
    assert observed.sleeps == [0.1]
