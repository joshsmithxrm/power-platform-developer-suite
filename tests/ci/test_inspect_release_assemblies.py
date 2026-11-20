"""Real-process tests of the release assembly inspector (scripts/ci/inspect_release_assemblies.ps1).

They need Windows PowerShell 7 and the Windows SDK's sn.exe. They skip elsewhere. On a runner that
sets PPDS_REQUIRE_WINDOWS_TOOLS=1 (the windows job in workflow-tests.yml) a missing tool is a
failure instead, so the tests cannot quietly stop running.

Nothing here signs anything: the assembly under test is built delay-signed from the public key only.
"""
import importlib.util
import os
from pathlib import Path
import shutil
import subprocess
import sys
import time

import pytest
from hang_bound import CHILD_PROCESS_HANG_BOUND_SECONDS  # tests/conftest.py puts tests/ on sys.path

ROOT = Path(__file__).resolve().parents[2]
SCRIPT = ROOT / 'scripts/ci/inspect_release_assemblies.ps1'
PUBLIC_KEY = ROOT / 'src/PPDS.Plugins/PPDS.Plugins.PublicKey'
SN = Path(os.environ.get('ProgramFiles(x86)', 'C:/Program Files (x86)')) / 'Microsoft SDKs/Windows/v10.0A/bin/NETFX 4.8 Tools/sn.exe'
SYSTEM_DLL = Path(os.environ.get('WINDIR', 'C:/Windows')) / 'Microsoft.NET/Framework64/v4.0.30319/System.dll'
VERSION = '3.1.1-test'
REQUIRED = os.environ.get('PPDS_REQUIRE_WINDOWS_TOOLS') == '1'
NO_RESULT = 'Strong-name verification tool did not report an exit code'
VERIFICATION_FAILED = 'Plugins cryptographic strong-name verification failed'

pytestmark = pytest.mark.skipif(sys.platform != 'win32', reason='Windows PowerShell native-launch contract')

driver = None
if sys.platform == 'win32':
    _spec = importlib.util.spec_from_file_location(
        'ppds_inspector_test_driver', ROOT / 'tests/compatibility/plugin_signing_compatibility.py')
    driver = importlib.util.module_from_spec(_spec)
    sys.modules[_spec.name] = driver
    _spec.loader.exec_module(driver)


def need(condition, reason):
    if condition:
        return
    if REQUIRED:
        pytest.fail('Required on this runner but missing: ' + reason)
    pytest.skip(reason)


@pytest.fixture(scope='module')
def pwsh():
    need(shutil.which('pwsh'), 'PowerShell 7 (pwsh)')
    return shutil.which('pwsh')


@pytest.fixture(scope='module')
def sn(pwsh):
    need(SN.is_file(), 'Windows SDK sn.exe at the fixed path')
    return SN


@pytest.fixture(scope='module')
def system_dll():
    need(SYSTEM_DLL.is_file(), 'a Microsoft-signed .NET Framework assembly')
    return SYSTEM_DLL


@pytest.fixture(scope='module')
def plugins_dir(pwsh, tmp_path_factory):
    """PPDS.Plugins built delay-signed from the repository public key (no private key, nothing signed)."""
    need(shutil.which('dotnet'), 'the dotnet SDK')
    work = tmp_path_factory.mktemp('plugins')
    try:
        # global.json is copied so the build uses the repository's pinned SDK, not whatever is newest.
        for name in ('Directory.Build.props', 'Directory.Packages.props', 'NuGet.config', 'global.json'):
            shutil.copy2(ROOT / name, work / name)
        project = work / 'src/PPDS.Plugins'
        shutil.copytree(ROOT / 'src/PPDS.Plugins', project, ignore=shutil.ignore_patterns('bin', 'obj'))
        # No build servers are left running, and the version does not depend on any enclosing git tree.
        build = subprocess.run(
            ['dotnet', 'build', str(project / 'PPDS.Plugins.csproj'), '-c', 'Release', '-p:PublicSign=false',
             '-p:DelaySign=true', '-p:MinVerVersionOverride=' + VERSION,
             '-p:IncludeSourceRevisionInInformationalVersion=false', '--disable-build-servers', '-v', 'q'],
            cwd=project, capture_output=True, text=True, timeout=600, stdin=subprocess.DEVNULL)
        assert build.returncode == 0, build.stdout[-2000:]
        output = work / 'packaged'
        output.mkdir()
        shutil.copy2(project / 'bin/Release/net462/PPDS.Plugins.dll', output / 'PPDS.Plugins.dll')
        yield output
    finally:
        shutil.rmtree(work, ignore_errors=True)


@pytest.fixture(scope='module')
def pathext_hides_exit_codes(pwsh, tmp_path_factory):
    """Whether this pwsh hides a native exit code when PATHEXT is absent (the behaviour the inspector guards).

    Tests that need that condition skip when a newer PowerShell stops exhibiting it; the inspector is
    then still correct, and an unrelated change must not be blocked by a PowerShell quirk.
    """
    probe = tmp_path_factory.mktemp('probe') / 'probe.ps1'
    probe.write_text("$global:LASTEXITCODE = $null\n& (Join-Path $env:SystemRoot 'System32\\cmd.exe') /c exit 3\n"
                     "'code=[' + $LASTEXITCODE + ']'\n")
    result = run_pwsh(pwsh, ['-File', str(probe)], env=environment(pathext=False), cwd=probe.parent)
    hidden = result.returncode == 0 and 'code=[]' in result.stdout
    if not hidden:
        pytest.skip('this pwsh reports native exit codes without PATHEXT')
    return True


def environment(*, pathext):
    # The runner's normal environment, with PATHEXT removed only when a test asks for that.
    env = {k: v for k, v in os.environ.items() if k.upper() != 'PATHEXT'}
    if pathext:
        env['PATHEXT'] = os.environ.get('PATHEXT') or '.COM;.EXE;.BAT;.CMD'
    return env


def run_pwsh(pwsh, arguments, *, env, cwd):
    return subprocess.run([pwsh, '-NoProfile', *arguments], cwd=cwd, env=env,
                          stdin=subprocess.DEVNULL, capture_output=True, text=True, timeout=CHILD_PROCESS_HANG_BOUND_SECONDS)


def run_script(pwsh, tmp_path, directory, *extra, env=None):
    env = environment(pathext=True) if env is None else env
    return run_pwsh(pwsh, ['-File', str(SCRIPT), '-Directory', str(directory), *extra], env=env, cwd=tmp_path)


def thrown(result):
    # The inspector's anchored line, as classification reads it.
    lines = [line for line in result.stderr.splitlines() if line.startswith('PPDS_INSPECTOR_MESSAGE=')]
    return lines[0][len('PPDS_INSPECTOR_MESSAGE='):] if lines else None


# --- Invoke-StrongNameVerification, extracted from the real script ---------------------------

def run_function(pwsh, tmp_path, tool, assembly, *, pathext, failing=None):
    source = SCRIPT.read_text(encoding='utf-8')
    begin = source.index('function Invoke-StrongNameVerification(')
    function = source[begin:source.index('\n}\n', begin) + 3]
    script = tmp_path / 'strong-name.ps1'
    # The same error preference as the real script; the function must also write nothing to the
    # output stream, or it would pollute the script's JSON rows. A `failing` script runs first and
    # leaves a stale exit code (7), as an earlier failing command in the same session would.
    before = "\n& $env:PPDS_TEST_FAILING\nif ($LASTEXITCODE -ne 7) { throw 'precondition' }" if failing else ""
    script.write_text("$ErrorActionPreference = 'Stop'\n" + function + before +
                      "\ntry { Invoke-StrongNameVerification $env:PPDS_TEST_TOOL $env:PPDS_TEST_ASSEMBLY }"
                      " catch { [Console]::Error.WriteLine($_.Exception.Message); exit 1 }\n")
    env = environment(pathext=pathext)
    env.update(PPDS_TEST_TOOL=str(tool), PPDS_TEST_ASSEMBLY=str(assembly))
    if failing:
        env['PPDS_TEST_FAILING'] = str(failing)
    return run_pwsh(pwsh, ['-File', str(script)], env=env, cwd=tmp_path)


def test_function_succeeds_silently_for_a_validly_signed_assembly(pwsh, sn, system_dll, tmp_path):
    result = run_function(pwsh, tmp_path, sn, system_dll, pathext=True)
    assert (result.returncode, result.stdout, result.stderr) == (0, '', '')


def test_function_reports_a_missing_pathext_instead_of_a_verification_result(
        pwsh, sn, system_dll, tmp_path, pathext_hides_exit_codes):
    result = run_function(pwsh, tmp_path, sn, system_dll, pathext=False)
    assert result.returncode == 1 and result.stdout == ''
    assert result.stderr.startswith(NO_RESULT)
    # Fixed text only: no environment contents or tool output.
    assert str(tmp_path) not in result.stderr and 'PATHEXT=' not in result.stderr


def test_a_stale_exit_code_from_an_earlier_command_is_not_taken_for_the_verification_result(
        pwsh, sn, system_dll, tmp_path, pathext_hides_exit_codes):
    # A .ps1 runs regardless of PATHEXT and leaves exit code 7 behind, as a failing command earlier
    # in the same session would. Without the clearing line the function would report that stale 7
    # as a verification failure instead of "no result".
    failing = tmp_path / 'fail.ps1'
    failing.write_text('exit 7\n')
    result = run_function(pwsh, tmp_path, sn, system_dll, pathext=False, failing=failing)
    assert result.returncode == 1 and result.stderr.startswith(NO_RESULT)


def test_function_reports_a_real_verification_failure_not_pathext(pwsh, sn, tmp_path):
    # A real, non-zero result from the tool keeps the existing diagnostic shape.
    result = run_function(pwsh, tmp_path, sn, tmp_path / 'absent.dll', pathext=True)
    assert result.returncode == 1 and result.stdout == ''
    assert result.stderr.startswith(VERIFICATION_FAILED + ':')
    assert NO_RESULT not in result.stderr


def test_without_pathext_the_tool_is_started_detached_and_unobserved(pwsh, tmp_path, pathext_hides_exit_codes):
    # What the "no result" message stands for: PowerShell does not skip the executable, it hands it
    # to the shell, which starts it without waiting and without capturing exit code or output. A
    # verification tool would therefore run unobserved. A cmd.exe stand-in leaves a visible trace.
    marker = tmp_path / 'started.txt'
    script = tmp_path / 'detached.ps1'
    script.write_text("$global:LASTEXITCODE = $null\n"
                      "$out = & (Join-Path $env:SystemRoot 'System32\\cmd.exe') /c "
                      "\"ping -n 3 127.0.0.1 >nul & echo started> `\"$env:PPDS_TEST_MARKER`\" & echo captured\"\n"
                      "'exit=[' + $LASTEXITCODE + '] captured=[' + $out + ']'\n"
                      "'marker at return: ' + (Test-Path $env:PPDS_TEST_MARKER)\n")
    env = environment(pathext=False)
    env['PPDS_TEST_MARKER'] = str(marker)
    result = run_pwsh(pwsh, ['-File', str(script)], env=env, cwd=tmp_path)
    assert result.returncode == 0
    assert 'exit=[] captured=[]' in result.stdout and 'marker at return: False' in result.stdout
    deadline = time.monotonic() + 30
    while not marker.exists() and time.monotonic() < deadline:
        time.sleep(0.2)
    if not marker.exists():
        # Characterisation only: a non-interactive session may not start a detached process at all.
        pytest.skip('the detached stand-in did not leave its marker in this session')


# --- The whole script, real call site -------------------------------------------------------

def test_script_emits_the_json_rows_for_a_delay_signed_input(pwsh, sn, plugins_dir, tmp_path):
    result = run_script(pwsh, tmp_path, plugins_dir, '-ExpectedPublicKey', str(PUBLIC_KEY), '-DelaySignedInput')
    assert result.returncode == 0, result.stderr
    assert result.stdout.strip() == '[{"name":"PPDS.Plugins","version":"%s"}]' % VERSION
    assert result.stderr == ''


def test_script_runs_real_sn_at_its_call_site_and_names_the_failed_check(pwsh, sn, plugins_dir, tmp_path):
    # Not signed: sn -vf must run for real, observably, and fail. A malformed call (a comma-separated
    # argument list, say) or a lost exit code would produce a different message.
    result = run_script(pwsh, tmp_path, plugins_dir, '-ExpectedPublicKey', str(PUBLIC_KEY))
    assert result.returncode == 1 and result.stdout == ''
    assert thrown(result).startswith(VERIFICATION_FAILED + ':')
    assert 'delay-signed or test-signed assembly' in thrown(result)
    assert driver.inspector_diagnostic(result.stderr) == 'inspector-sn-verification'


def test_script_without_pathext_names_the_missing_result(pwsh, sn, plugins_dir, tmp_path, pathext_hides_exit_codes):
    result = run_script(pwsh, tmp_path, plugins_dir, '-ExpectedPublicKey', str(PUBLIC_KEY),
                        env=environment(pathext=False))
    assert result.returncode == 1 and result.stdout == ''
    assert thrown(result).startswith(NO_RESULT)
    assert driver.inspector_diagnostic(result.stderr) == 'inspector-sn-no-result'


def test_script_with_a_missing_sn_fails_before_launching_anything(pwsh, plugins_dir, tmp_path):
    env = {k: v for k, v in environment(pathext=True).items() if k.upper() != 'PROGRAMFILES(X86)'}
    env['ProgramFiles(x86)'] = str(tmp_path / 'no-sdk')
    result = run_script(pwsh, tmp_path, plugins_dir, '-ExpectedPublicKey', str(PUBLIC_KEY), env=env)
    assert result.returncode == 1 and result.stdout == ''
    assert thrown(result) == 'Required Windows SDK sn.exe is missing'
    assert driver.inspector_diagnostic(result.stderr) == 'inspector-sn-missing'


def test_script_classifies_the_actual_pwsh_error_output_of_each_reachable_refusal(
        pwsh, plugins_dir, system_dll, tmp_path):
    # Real stderr (PowerShell 7 ConciseView, colour codes and all), not a modelled string.
    foreign = tmp_path / 'foreign'
    foreign.mkdir()
    shutil.copy2(system_dll, foreign / 'System.dll')
    empty = tmp_path / 'empty'
    empty.mkdir()
    wrong_key = tmp_path / 'wrong.key'
    wrong_key.write_bytes(b'not the repository public key')
    key = str(PUBLIC_KEY)
    cases = [
        (foreign, ['-ExpectedPublicKey', key], 'Unexpected assembly for Plugins signing gate', 'inspector-assembly-name'),
        (plugins_dir, ['-ExpectedPublicKey', str(wrong_key)],
         'Packaged Plugins full public key differs from the repository public key', 'inspector-public-key'),
        (plugins_dir, ['-DelaySignedInput'], 'Delay-signed input requires an expected public key',
         'inspector-delay-key'),
        (empty, [], 'No assemblies to inspect', 'inspector-no-assemblies'),
    ]
    for directory, extra, message, code in cases:
        result = run_script(pwsh, tmp_path, directory, *extra)
        assert result.returncode == 1 and result.stdout == '', message
        assert thrown(result) == message
        assert driver.inspector_diagnostic(result.stderr) == code, message


def test_script_errors_that_are_not_its_own_throws_are_unclassified(pwsh, plugins_dir, tmp_path):
    garbage = tmp_path / 'garbage'
    garbage.mkdir()
    (garbage / 'bad.dll').write_bytes(b'this is not a managed assembly')
    for directory, extra in ((tmp_path / 'absent', []), (garbage, []), (garbage, ['-ExpectedPublicKey', str(PUBLIC_KEY)]),
                             (plugins_dir, ['-ExpectedPublicKey', str(tmp_path / 'absent.key')])):
        result = run_script(pwsh, tmp_path, directory, *extra)
        assert result.returncode == 1 and result.stdout == ''
        assert thrown(result) is None
        assert driver.inspector_diagnostic(result.stderr) == driver.INSPECTOR_UNCLASSIFIED
