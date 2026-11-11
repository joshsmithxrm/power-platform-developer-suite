"""Actual data verifier exercised with temporary packages and recording GETs only."""
from copy import deepcopy
import ast
import hashlib
import json
from pathlib import Path
import zipfile
import pytest
import yaml
from scripts.ci import verify_nuget_handoff as h
from tests.ci.test_nuget_oidc_build_isolation import assert_upload_only, assert_closed_publish_routing, artifact_selection

ROOT=Path(__file__).resolve().parents[2]
SOURCE='b'*40
TAG='Auth-v1.2.1'
ARTIFACT_DIGEST='a'*64

def package(path, *, package_id='PPDS.Auth', version='1.2.1', source=SOURCE, member='lib/net8.0/PPDS.Auth.dll'):
    with zipfile.ZipFile(path,'w') as z:
        z.writestr('PPDS.Auth.nuspec',f'<package><metadata><id>{package_id}</id><version>{version}</version><repository commit="{source}"/></metadata></package>')
        entry=zipfile.ZipInfo(member)
        # Windows ZipInfo normalizes string backslashes. Override both names so the negative archive really contains them.
        entry.filename=entry.orig_filename=member
        z.writestr(entry,b'NOT-EXECUTABLE-SYNTHETIC-MARKER')

@pytest.fixture
def fixture(tmp_path):
    package(tmp_path/'PPDS.Auth.1.2.1.nupkg')
    manifest=h.create(tmp_path,TAG,SOURCE,'17','1')
    artifact={'id':71,'name':f'nuget-package-17-1-{SOURCE}','digest':'sha256:'+ARTIFACT_DIGEST,'expired':False,'size_in_bytes':100,
        'workflow_run':{'id':17,'head_sha':SOURCE,'repository_id':h.REPO_ID,'head_repository_id':h.REPO_ID},'created_at':'2026-11-09T01:01:00Z'}
    job={'id':91,'name':h.JOB_NAME,'head_sha':SOURCE,'run_id':17,'run_attempt':1,'status':'completed','conclusion':'success','started_at':'2026-11-09T01:00:00Z','completed_at':'2026-11-09T01:02:00Z'}
    responses={'artifact':artifact,'jobs':{'total_count':1,'jobs':[job]},'ref':{'ref':'refs/tags/'+TAG,'object':{'type':'commit','sha':SOURCE}},'registry':{'versions':['1.0.0','1.2.0']}}
    calls=[]
    def fetch(url):
        calls.append(url)
        if '/actions/artifacts/' in url:return responses['artifact']
        if '/jobs?' in url:return responses['jobs']
        if '/git/ref/' in url:return responses['ref']
        if '/git/tags/' in url:return responses['annotated']
        assert url=='https://api.nuget.org/v3-flatcontainer/ppds.auth/index.json'
        return responses['registry']
    def verify():return h.verify(tmp_path,TAG,SOURCE,'17','1','71',ARTIFACT_DIGEST,manifest,fetch=fetch)
    return tmp_path,responses,calls,verify

def test_verified_package_binds_source_run_physical_job_and_hash_without_execution(fixture):
    directory,_,calls,verify=fixture
    result=verify()
    assert result['source']==SOURCE and result['producer_job_id']==91 and result['artifact_id']==71
    assert result['packages']==[{'name':'PPDS.Auth.1.2.1.nupkg','bytes':(directory/'PPDS.Auth.1.2.1.nupkg').stat().st_size,'sha256':hashlib.sha256((directory/'PPDS.Auth.1.2.1.nupkg').read_bytes()).hexdigest()}]
    assert result['package_code_assembly_MSBuild_or_OIDC_executed'] is False and len(calls)==4

@pytest.mark.parametrize(('area','key','value'),[
    ('artifact','id',72),('artifact','name','other-job'),('artifact','digest','sha256:'+'c'*64),
    ('artifact','expired',True),('artifact','size_in_bytes',0),('artifact','created_at','2026-11-09T02:00:00Z'),
    ('workflow_run','id',18),('workflow_run','head_sha','c'*40),('workflow_run','repository_id',12),('workflow_run','head_repository_id',12),
    ('job','status','in_progress'),('job','conclusion','failure'),('job','head_sha','c'*40),('job','run_id',18),('job','run_attempt',2),('job','id',True),
    ('jobs','total_count',2),('ref','ref','refs/heads/main'),('registry','versions',[]),('registry','versions',['1.2.1']),('registry','versions',['not-a-version'])])
def test_wrong_or_incomplete_service_provenance_or_vacancy_refuses(fixture,area,key,value):
    _,responses,_,verify=fixture
    target=responses['artifact']['workflow_run'] if area=='workflow_run' else responses['jobs']['jobs'][0] if area=='job' else responses[area]
    target[key]=value
    with pytest.raises(h.Error):verify()

def test_ambiguous_producer_jobs_refuse(fixture):
    _,responses,_,verify=fixture
    responses['jobs']['jobs'].append(deepcopy(responses['jobs']['jobs'][0]));responses['jobs']['total_count']=2
    with pytest.raises(h.Error,match='ambiguous'):verify()

def test_moved_lightweight_or_annotated_tag_refuses(fixture):
    _,responses,_,verify=fixture
    responses['ref']['object']['sha']='c'*40
    with pytest.raises(h.Error,match='moved'):verify()
    responses['ref']['object']={'type':'tag','sha':'d'*40};responses['annotated']={'object':{'type':'commit','sha':'c'*40}}
    with pytest.raises(h.Error,match='moved'):verify()

def test_annotated_tag_peels_to_exact_source(fixture):
    _,responses,_,verify=fixture
    responses['ref']['object']={'type':'tag','sha':'d'*40};responses['annotated']={'object':{'type':'commit','sha':SOURCE}}
    assert verify()['source']==SOURCE

def test_cyclic_annotated_tag_refuses(fixture):
    _,responses,_,verify=fixture
    responses['ref']['object']={'type':'tag','sha':'d'*40};responses['annotated']={'object':{'type':'tag','sha':'d'*40}}
    with pytest.raises(h.Error,match='cyclic'):verify()

@pytest.mark.parametrize('field',['source','tag','run_id','run_attempt','producer_job','repository','schema'])
def test_manifest_claim_changes_cannot_reach_service_reads(fixture,field):
    directory,_,calls,verify=fixture
    data=json.loads((directory/h.MANIFEST).read_bytes());data[field]='changed'
    (directory/h.MANIFEST).write_text(json.dumps(data))
    with pytest.raises(h.Error,match='hash'):verify()
    assert calls==[]

@pytest.mark.parametrize(('field','value'),[('source','c'*40),('run_id','18'),('run_attempt','2'),('producer_job','different-job'),('repository','foreign/repo'),('schema',True)])
def test_even_a_rehashed_manifest_cannot_change_bound_provenance(fixture,field,value):
    directory,_,calls,_=fixture
    data=json.loads((directory/h.MANIFEST).read_bytes());data[field]=value
    raw=json.dumps(data).encode();(directory/h.MANIFEST).write_bytes(raw)
    with pytest.raises(h.Error,match='source/run/job'):
        h.verify(directory,TAG,SOURCE,'17','1','71',ARTIFACT_DIGEST,h.sha(raw),fetch=lambda url:calls.append(url))
    assert calls==[]

@pytest.mark.parametrize(('artifact_digest','manifest_sha'),[(None,'b'*64),('a'*64,None),('','b'*64),('a'*64,'')])
def test_missing_hash_never_reaches_metadata_or_credentials(fixture,artifact_digest,manifest_sha):
    directory,_,calls,_=fixture
    with pytest.raises(h.Error,match='hash binding'):
        h.verify(directory,TAG,SOURCE,'17','1','71',artifact_digest,manifest_sha,fetch=lambda url:calls.append(url))
    assert calls==[]

def test_changed_package_and_extra_script_refuse_before_metadata_reads(fixture):
    directory,_,calls,verify=fixture
    (directory/'run-me.py').write_text('raise RuntimeError("MUST-NOT-EXECUTE")')
    with pytest.raises(h.Error):verify()
    assert calls==[]

@pytest.mark.parametrize('kwargs',[{'package_id':'Foreign'},{'version':'1.2.0'},{'source':'c'*40},{'member':'../escape.dll'},{'member':'C:/escape.dll'},{'member':'lib\\escape.dll'}])
def test_package_identity_and_archive_traversal_are_rejected(tmp_path,kwargs):
    package(tmp_path/'PPDS.Auth.1.2.1.nupkg',**kwargs)
    with pytest.raises(h.Error):h.create(tmp_path,TAG,SOURCE,'17','1')

def test_same_hash_manifest_cannot_be_clobbered(fixture):
    directory,_,_,_=fixture
    original=(directory/h.MANIFEST).read_bytes()
    with pytest.raises(FileExistsError):h.create(directory,TAG,SOURCE,'17','1')
    assert (directory/h.MANIFEST).read_bytes()==original

@pytest.mark.parametrize('tag',[p+'-v1.2.1' for p in h.PACKAGES])
def test_all_seven_canonical_routes_preserved(tag):
    assert h.identity(tag,SOURCE,'17','1')==(h.PACKAGES[tag.split('-v')[0]],'1.2.1')


@pytest.mark.parametrize('component',[name for name in h.PACKAGES if name != 'Plugins'])
def test_nonplugins_keep_build_package_schema1_without_signing_chain(tmp_path,component):
    package_id=h.PACKAGES[component]
    tag=component+'-v1.2.1'
    package(tmp_path/(package_id+'.1.2.1.nupkg'),package_id=package_id,
            member='lib/net8.0/'+package_id+'.dll')
    manifest_sha=h.create(tmp_path,tag,SOURCE,'17','1')
    raw=(tmp_path/h.MANIFEST).read_bytes()
    data=json.loads(raw)
    assert manifest_sha==h.sha(raw)
    assert data['schema']==1 and data['producer_job']=='build-package'
    assert 'signing_chain' not in data


def test_plugins_cannot_create_build_only_handoff(tmp_path):
    with pytest.raises(h.Error,match='independently verified signing chain'):
        h.create(tmp_path,'Plugins-v3.1.1',SOURCE,'17','1')
    assert not (tmp_path/h.MANIFEST).exists()

@pytest.mark.parametrize('command',['dotnet restore','dotnet build','dotnet pack','dotnet msbuild','dotnet tool restore'])
def test_policy_refuses_build_execution_when_oidc_is_available(command):
    workflow=yaml.safe_load((ROOT/'.github/workflows/publish-nuget.yml').read_text())
    workflow['jobs']['publish']['steps'].insert(0,{'run':command})
    with pytest.raises(AssertionError):assert_upload_only(workflow)

def test_oidc_data_guard_has_no_subprocess_dynamic_import_or_assembly_loader():
    tree=ast.parse((ROOT/'scripts/ci/verify_nuget_handoff.py').read_text())
    imports={a.name.split('.')[0] for n in ast.walk(tree) if isinstance(n,(ast.Import,ast.ImportFrom)) for a in (n.names if isinstance(n,ast.Import) else [ast.alias(name=n.module or '')])}
    assert not imports&{'subprocess','importlib','runpy','clr','requests'}
    assert not any(isinstance(n,ast.Call) and isinstance(n.func,ast.Name) and n.func.id in {'eval','exec','compile'} for n in ast.walk(tree))

def test_fresh_publisher_checkout_download_and_login_order_is_source_pinned():
    workflow=yaml.safe_load((ROOT/'.github/workflows/publish-nuget.yml').read_text());steps=workflow['jobs']['publish']['steps']
    checkout=next(s for s in steps if s.get('uses','').startswith('actions/checkout@'))
    assert checkout['with']['ref']=='${{ github.sha }}' and checkout['with']['persist-credentials'] is False
    download=next(s for s in steps if s.get('uses','').startswith('actions/download-artifact@'))
    assert download['with']['artifact-ids']==artifact_selection('artifact-id')
    assert_closed_publish_routing(workflow)
    assert set(checkout['with']['sparse-checkout'].splitlines())=={
        'scripts/ci/verify_nuget_handoff.py', 'scripts/ci/plugin-signing-policy.json'}
    gate=next(i for i,s in enumerate(steps) if s.get('id')=='verify-handoff');login=next(i for i,s in enumerate(steps) if s.get('id')=='nuget-login')
    assert gate<login and all('secrets.' not in str(s) for s in steps)
    assert 'python -I scripts/ci/verify_nuget_handoff.py verify' in steps[gate]['run']


@pytest.mark.parametrize('field',['artifact-id','artifact-digest','manifest-sha256'])
def test_plugins_output_cannot_fall_back_to_keyless_build(field):
    workflow=yaml.safe_load((ROOT/'.github/workflows/publish-nuget.yml').read_text())
    publisher=workflow['jobs']['publish']
    guard=next(s for s in publisher['steps'] if s.get('id')=='verify-handoff')
    variable={'artifact-id':'HANDOFF_ARTIFACT_ID','artifact-digest':'HANDOFF_ARTIFACT_DIGEST',
              'manifest-sha256':'HANDOFF_MANIFEST_SHA256'}[field]
    guard['env'][variable]="${{ needs.verify-signed-plugins.outputs."+field+" || needs.build-package.outputs."+field+" }}"
    with pytest.raises(AssertionError):assert_closed_publish_routing(workflow)


@pytest.mark.parametrize('weakening',[
    "needs.build-package.result == 'success' && ",
    "needs.verify-signed-plugins.result == 'success' && ",
    "needs.verify-signed-plugins.result == 'skipped' && ",
    "needs.verify-signed-plugins.outputs.artifact-id != ''",
])
def test_publisher_result_gate_cannot_accept_failed_or_missing_plugin_output(weakening):
    workflow=yaml.safe_load((ROOT/'.github/workflows/publish-nuget.yml').read_text())
    original=workflow['jobs']['publish']['if']
    assert weakening in original
    workflow['jobs']['publish']['if']=original.replace(weakening,'true' if weakening.endswith("!= ''") else '',1)
    with pytest.raises(AssertionError):assert_closed_publish_routing(workflow)

def assert_reviewed_publisher_actions(workflow):
    allowed={'actions/checkout@v6','actions/setup-dotnet@v6','actions/setup-python@v7',
        'actions/download-artifact@v8','NuGet/login@8d196754b4036150537f80ac539e15c2f1028841'}
    steps=workflow['jobs']['publish']['steps']
    assert all(step['uses'] in allowed for step in steps if 'uses' in step)
    assert all(step.get('name') in {'Verify source, producer job, package hashes, tag and vacancy without MSBuild',
        'Validate trusted publishing identity','Push packages to NuGet','Push symbols to NuGet'} for step in steps if 'run' in step)

def test_only_reviewed_actions_and_data_upload_commands_have_oidc_authority():
    assert_reviewed_publisher_actions(yaml.safe_load((ROOT/'.github/workflows/publish-nuget.yml').read_text()))

@pytest.mark.parametrize('step',[{'uses':'./restored-package/action'},{'uses':'docker://foreign/publisher'},
    {'uses':'foreign/build-action@v1'},{'name':'Untrusted package code','run':'python downloaded-package/run.py'},
    {'name':'Assembly inspector','run':'pwsh -File inspect_release_assemblies.ps1'}])
def test_unreviewed_code_entry_points_refuse_in_oidc_job(step):
    workflow=yaml.safe_load((ROOT/'.github/workflows/publish-nuget.yml').read_text());workflow['jobs']['publish']['steps'].insert(0,step)
    with pytest.raises(AssertionError):assert_reviewed_publisher_actions(workflow)

@pytest.mark.parametrize('side',['build','upload'])
@pytest.mark.parametrize(('versions','outcome'),[
    (['1.2.2.5'],'vacant'), (['1.2.1.0'],'occupied'), (['01.02.1.0'],'occupied'),
    (['1.2.1+legacy'],'occupied'), (['1.2.1.1'],'vacant'),
    (['1.2.3.4.5'],'invalid'), ([None],'invalid'), (['1.2.1-'],'invalid'),
    (['1.2.1+'],'invalid'), (['2147483648.0.0'],'invalid'), (['1.2.1-01'],'invalid'),
])
def test_legacy_registry_versions_preserve_vacancy_failclosed(fixture,side,versions,outcome):
    from scripts.ci import verify_release_inputs as build
    _,responses,_,upload=fixture;responses['registry']={'versions':versions}
    action=(lambda:build.nuget_vacant('PPDS.Migration','1.2.1',fetch=lambda *args:json.dumps({'versions':versions}).encode())) if side=='build' else upload
    if outcome=='vacant':action()
    else:
        with pytest.raises((h.Error,build.Error),match='occupied' if outcome=='occupied' else None):action()

@pytest.mark.parametrize('side',['build','upload'])
def test_current_migration_registry_is_parsed_without_invented_four_part(fixture,side):
    from scripts.ci import verify_release_inputs as build
    # Anonymous public index read: SHAad2d1d07...,15versions, no four-part entry. Synthetic legacy cases above are deliberately separate.
    versions=['1.0.0-alpha.1','1.0.0-alpha1','1.0.0-beta.1','1.0.0-beta.2','1.0.0-beta.3',
        '1.0.0-beta.4','1.0.0-beta.5','1.0.0-beta.6','1.0.0-beta.7','1.0.0-beta.8',
        '1.0.0','1.1.0','1.2.0','1.2.1','1.2.2']
    if side=='build':build.nuget_vacant('PPDS.Migration','1.3.0',fetch=lambda *args:json.dumps({'versions':versions}).encode())
    else:
        _,responses,_,upload=fixture;responses['registry']={'versions':versions}
        with pytest.raises(h.Error,match='occupied'):upload()

@pytest.mark.parametrize('value',['1.3.0.0','01.3.0'])
def test_proposed_release_tags_are_still_three_part_canonical(value):
    from scripts.ci import verify_release_inputs as build
    with pytest.raises(h.Error):h.identity('Migration-v'+value,SOURCE,'17','1')
    with pytest.raises(build.Error):build.strict_version(value)
