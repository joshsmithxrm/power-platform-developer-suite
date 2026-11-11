"""Offline NuGet OIDC publisher boundary checks (release AC-39–42)."""
from __future__ import annotations

from pathlib import Path
import os
import shutil
import subprocess

import pytest
import yaml

ROOT = Path(__file__).resolve().parents[2]
PATH = ROOT / '.github/workflows/publish-nuget.yml'
LOGIN = 'NuGet/login@8d196754b4036150537f80ac539e15c2f1028841'
TEMP_KEY = '${{ steps.nuget-login.outputs.NUGET_API_KEY }}'
USER = 'JoshSmithMSFT'
TAGS = ['Plugins-v*', 'Dataverse-v*', 'Migration-v*', 'Auth-v*', 'Cli-v*', 'Query-v*', 'Mcp-v*']


@pytest.fixture(scope='module')
def workflow():
    return yaml.safe_load(PATH.read_text(encoding='utf-8'))


def publishing(workflow):
    return workflow['jobs']['publish']


def test_exact_component_tag_entry_points(workflow):
    assert (workflow.get('on') or workflow.get(True)) == {'push': {'tags': TAGS}}
    condition = publishing(workflow)['if']
    assert "github.repository == 'joshsmithxrm/power-platform-developer-suite'" in condition
    assert "github.event_name == 'push'" in condition
    assert "github.ref_type == 'tag'" in condition
    for tag in TAGS:
        assert f"startsWith(github.ref, 'refs/tags/{tag[:-1]}')" in condition


def test_oidc_is_publishing_job_only(workflow):
    assert workflow['permissions'] == {'contents': 'read'}
    assert publishing(workflow)['permissions'] == {'contents': 'read', 'actions': 'read', 'id-token': 'write'}
    for name, job in workflow['jobs'].items():
        if name != 'publish':
            expected = {'contents': 'read', 'actions': 'read'} if name in {'sign-plugins', 'verify-signed-plugins'} else {'contents': 'read'}
            assert job.get('permissions', workflow['permissions']) == expected
    assert 'environment' not in publishing(workflow)


def test_pinned_login_uses_explicit_creator_configuration(workflow):
    steps = publishing(workflow)['steps']
    login = next(s for s in steps if s.get('id') == 'nuget-login')
    assert login['uses'] == LOGIN
    assert login['with'] == {'user': USER}
    assert not login.get('continue-on-error', False)
    guard = next(s for s in steps if s.get('name') == 'Validate trusted publishing identity')
    assert guard['env'] == {'NUGET_PUBLISH_USER': USER}
    assert 'secrets.' not in str(login) + str(guard)


def test_login_follows_all_preflight_and_immediately_precedes_upload(workflow):
    steps = publishing(workflow)['steps']
    login = next(i for i,s in enumerate(steps) if s.get('id') == 'nuget-login')
    guard = next(i for i,s in enumerate(steps) if s.get('name') == 'Validate trusted publishing identity')
    release = next(i for i,s in enumerate(steps) if s.get('id') == 'verify-handoff')
    build_steps = workflow['jobs']['build-package']['steps']
    build = next(i for i,s in enumerate(build_steps) if 'dotnet build ' in s.get('run', ''))
    pack = next(i for i,s in enumerate(build_steps) if s.get('name') == 'Pack specific projects')
    build_release = next(i for i,s in enumerate(build_steps) if 'verify_release_inputs.py nuget' in s.get('run', ''))
    pushes = [i for i,s in enumerate(steps) if 'dotnet nuget push' in s.get('run', '')]
    assert build < pack < build_release
    assert publishing(workflow)['needs'] == ['build-package', 'verify-signed-plugins']
    assert release < guard < login
    assert build_steps[build_release]['if'] == "${{ !startsWith(github.ref, 'refs/tags/Plugins-v') }}"
    verified_steps = workflow['jobs']['verify-signed-plugins']['steps']
    signed_check = next(i for i, s in enumerate(verified_steps) if 'verify_plugin_signing.py verify' in s.get('run', ''))
    full_check = next(i for i, s in enumerate(verified_steps) if 'verify_release_inputs.py nuget' in s.get('run', ''))
    final_handoff = next(i for i, s in enumerate(verified_steps) if s.get('id') == 'handoff')
    assert signed_check < full_check < final_handoff
    assert '--chain "$RUNNER_TEMP/plugins-chain.json"' in verified_steps[final_handoff]['run']
    assert len(pushes) == 2 and pushes[0] == login + 1 and pushes[1] > pushes[0]


def test_both_uploads_use_ephemeral_output_without_stored_key(workflow):
    steps = publishing(workflow)['steps']
    pushes = [s for s in steps if 'dotnet nuget push' in s.get('run', '')]
    assert len(pushes) == 2
    for step in pushes:
        assert step['env'] == {'NUGET_API_KEY': TEMP_KEY}
        assert '--api-key "$NUGET_API_KEY"' in step['run']
        assert '--source https://api.nuget.org/v3/index.json' in step['run']
    assert '--no-symbols' in pushes[0]['run']
    assert '*.snupkg' in pushes[1]['run']
    assert 'secrets.NUGET_API_KEY' not in PATH.read_text(encoding='utf-8')


@pytest.mark.parametrize(('value', 'exit_code'), [
    ('', 1), ('two users', 1), ('user;echo invalid', 1), ('user\nother', 1),
    ('JoshSmithMSFT', 0), ('individual_creator-1', 0),
])
def test_identity_guard_executes_without_login(workflow, value, exit_code):
    step = next(s for s in publishing(workflow)['steps'] if s.get('name') == 'Validate trusted publishing identity')
    bash = shutil.which('bash')
    assert bash, 'Git Bash is required to execute the checked-in publisher guard offline'
    env = os.environ.copy()
    env['NUGET_PUBLISH_USER'] = value
    result = subprocess.run([bash, '--noprofile', '--norc', '-e', '-s'], input=step['run'],
                            text=True, capture_output=True, env=env, timeout=10)
    assert result.returncode == exit_code
    assert value == '' or value not in result.stdout + result.stderr


def test_signing_secret_is_one_admitted_step_and_public_verification_is_retained(workflow):
    build = workflow['jobs']['build-package']
    assert 'PLUGINS_SNK_BASE64' not in str(build) and 'secrets.' not in str(build)
    steps = workflow['jobs']['sign-plugins']['steps']
    signing = next(s for s in steps if s.get('name') == 'Sign only the admitted assembly')
    assert signing['env'] == {'PLUGINS_SNK_BASE64': '${{ secrets.PLUGINS_SNK_BASE64 }}'}
    assert 'RUNNER_TEMP' in signing['run']
    assert './scripts/ci/sign_plugin_assembly.ps1' in signing['run']
    policy = next(i for i, s in enumerate(steps) if 'verify_plugin_signing.py policy' in s.get('run', ''))
    admission = next(i for i, s in enumerate(steps) if 'verify_plugin_signing.py admit' in s.get('run', ''))
    assert policy < admission < steps.index(signing)
    assert not steps[policy].get('continue-on-error', False)
    assert not steps[admission].get('continue-on-error', False)
    assert not signing.get('continue-on-error', False)
    secret_steps = [(job_name, step) for job_name, job in workflow['jobs'].items()
                    for step in job['steps'] if 'secrets.' in str(step)]
    assert secret_steps == [('sign-plugins', signing)]
    assert 'GITHUB_ENV' not in str(steps)
    assert 'secrets.' not in str(workflow['jobs']['verify-signed-plugins'])
    verification = workflow['jobs']['verify-public-artifact']
    assert verification['needs'] == 'publish'
    assert any('verify_public_release.py' in s.get('run', '') for s in verification['steps'])


def test_runbook_explains_identity_and_activation_boundary():
    text = ' '.join((ROOT / 'docs/RELEASE.md').read_text(encoding='utf-8').split())
    for phrase in ('NUGET_PUBLISH_USER', 'individual policy creator', 'JoshSmithMSFT',
                   'user-reported', 'token exchange', 'final publication approval'):
        assert phrase in text
