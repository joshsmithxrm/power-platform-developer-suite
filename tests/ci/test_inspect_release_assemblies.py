"""Failure-shape tests for the release assembly inspector's strong-name step."""
import os
from pathlib import Path
import shutil
import subprocess
import sys

import pytest

ROOT = Path(__file__).resolve().parents[2]
SN = Path(os.environ.get('ProgramFiles(x86)', '')) / 'Microsoft SDKs/Windows/v10.0A/bin/NETFX 4.8 Tools/sn.exe'
pytestmark = pytest.mark.skipif(sys.platform != 'win32' or not shutil.which('pwsh'),
                                reason='Windows PowerShell native-launch contract')
NOT_RUN = 'Strong-name verification tool did not run; the environment may lack PATHEXT'
MISSING = 'Required Windows SDK sn.exe is missing'


def run_function(tmp_path, tool, assembly, *, pathext):
    source = (ROOT / 'scripts/ci/inspect_release_assemblies.ps1').read_text()
    begin = source.index('function Invoke-StrongNameVerification(')
    function = source[begin:source.index('\n}\n', begin) + 3]
    script = tmp_path / 'strong-name.ps1'
    script.write_text(function + "\ntry { Invoke-StrongNameVerification $env:PPDS_TEST_TOOL $env:PPDS_TEST_ASSEMBLY }"
                      " catch { [Console]::Error.WriteLine($_.Exception.Message); exit 1 }\n")
    env = {k: v for k, v in os.environ.items() if k.upper() != 'PATHEXT'}
    if pathext:
        env['PATHEXT'] = '.EXE'
    env.update(PPDS_TEST_TOOL=str(tool), PPDS_TEST_ASSEMBLY=str(assembly))
    return subprocess.run([shutil.which('pwsh'), '-NoProfile', '-NonInteractive', '-File', str(script)],
                          env=env, stdin=subprocess.DEVNULL, capture_output=True, text=True, timeout=60)


def test_missing_tool_fails_with_fixed_message_before_launch(tmp_path):
    result = run_function(tmp_path, tmp_path / 'absent-sn.exe', tmp_path / 'a.dll', pathext=True)
    assert result.returncode == 1
    assert result.stderr.strip() == MISSING


@pytest.mark.skipif(not SN.is_file(), reason='Windows SDK sn.exe not installed')
def test_native_tool_that_never_ran_names_missing_pathext(tmp_path):
    result = run_function(tmp_path, SN, tmp_path / 'a.dll', pathext=False)
    assert result.returncode == 1
    assert result.stderr.startswith(NOT_RUN)
    # Fixed text only: no environment contents or tool output.
    assert str(tmp_path) not in result.stderr and 'PATHEXT=' not in result.stderr


@pytest.mark.skipif(not SN.is_file(), reason='Windows SDK sn.exe not installed')
def test_native_tool_that_ran_reports_verification_failure_not_pathext(tmp_path):
    # A real, non-zero result from the tool keeps the existing diagnostic shape.
    result = run_function(tmp_path, SN, tmp_path / 'a.dll', pathext=True)
    assert result.returncode == 1
    assert result.stderr.startswith('Plugins cryptographic strong-name verification failed:')
    assert NOT_RUN not in result.stderr
