"""A dependency's build target must never inherit publishing/OIDC authority."""
from pathlib import Path
import re
import yaml

ROOT = Path(__file__).resolve().parents[2]

def assert_upload_only(workflow):
    publisher = workflow['jobs']['publish']
    assert publisher['permissions'].get('id-token') == 'write'
    for step in publisher['steps']:
        command = step.get('run', '')
        assert not re.search(r'\bdotnet\s+(restore|build|pack|test|publish|msbuild|tool)\b', command)
        assert 'PLUGINS_SNK_BASE64' not in str(step)
        assert not str(step.get('uses', '')).startswith(('./', 'docker://'))
    assert publisher.get('needs') == 'build-package'
    assert workflow['jobs']['build-package']['permissions'] == {'contents': 'read'}

def test_oidc_publisher_has_no_build_or_package_execution():
    assert_upload_only(yaml.safe_load((ROOT/'.github/workflows/publish-nuget.yml').read_text(encoding='utf-8')))
