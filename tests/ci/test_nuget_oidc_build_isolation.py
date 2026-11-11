"""A dependency's build target must never inherit publishing/OIDC authority."""
from pathlib import Path
import re
import yaml

ROOT = Path(__file__).resolve().parents[2]


def artifact_selection(field):
    """Plugins may never fall back to the keyless build when output is absent."""
    return ("${{ startsWith(github.ref, 'refs/tags/Plugins-v') && "
            f"needs.verify-signed-plugins.outputs.{field} || "
            "(!startsWith(github.ref, 'refs/tags/Plugins-v') && "
            f"needs.build-package.outputs.{field} || '') " + "}}")


def assert_closed_publish_routing(workflow):
    publisher = workflow['jobs']['publish']
    assert publisher['needs'] == ['build-package', 'verify-signed-plugins']
    expected_gate = (
        "always() && needs.build-package.result == 'success' && "
        "((startsWith(github.ref, 'refs/tags/Plugins-v') && "
        "needs.verify-signed-plugins.result == 'success' && "
        "needs.verify-signed-plugins.outputs.artifact-id != '') || "
        "(!startsWith(github.ref, 'refs/tags/Plugins-v') && "
        "needs.verify-signed-plugins.result == 'skipped' && "
        "needs.build-package.outputs.artifact-id != '')) && "
    )
    # Compare the complete result gate, not isolated substrings that an OR can bypass.
    assert ' '.join(publisher['if'].split('github.repository', 1)[0].split()) == expected_gate.strip()
    steps = publisher['steps']
    downloads = [s for s in steps if s.get('uses', '').startswith('actions/download-artifact@')]
    assert len(downloads) == 1
    assert downloads[0]['with']['artifact-ids'] == artifact_selection('artifact-id')
    guard = next(s for s in steps if s.get('id') == 'verify-handoff')
    for variable, field in (
        ('HANDOFF_ARTIFACT_ID', 'artifact-id'),
        ('HANDOFF_ARTIFACT_DIGEST', 'artifact-digest'),
        ('HANDOFF_MANIFEST_SHA256', 'manifest-sha256'),
    ):
        assert guard['env'][variable] == artifact_selection(field)


def assert_upload_only(workflow):
    publisher = workflow['jobs']['publish']
    assert publisher['permissions'].get('id-token') == 'write'
    for step in publisher['steps']:
        command = step.get('run', '')
        assert not re.search(r'\bdotnet\s+(restore|build|pack|test|publish|msbuild|tool)\b', command)
        assert 'PLUGINS_SNK_BASE64' not in str(step)
        assert not str(step.get('uses', '')).startswith(('./', 'docker://'))
    assert_closed_publish_routing(workflow)
    assert workflow['jobs']['build-package']['permissions'] == {'contents': 'read'}
    assert 'secrets.' not in str(workflow['jobs']['build-package'])
    for name in ('sign-plugins', 'verify-signed-plugins'):
        assert workflow['jobs'][name]['permissions'] == {'contents': 'read', 'actions': 'read'}
    assert workflow['jobs']['sign-plugins']['needs'] == 'build-package'
    assert workflow['jobs']['verify-signed-plugins']['needs'] == ['build-package', 'sign-plugins']

def test_oidc_publisher_has_no_build_or_package_execution():
    assert_upload_only(yaml.safe_load((ROOT/'.github/workflows/publish-nuget.yml').read_text(encoding='utf-8')))
