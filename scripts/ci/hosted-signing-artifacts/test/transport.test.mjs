import test from 'node:test';
import assert from 'node:assert/strict';
import fs from 'node:fs/promises';
import os from 'node:os';
import path from 'node:path';
import {createHash} from 'node:crypto';
import {artifactName, decodeEnvelope, download, limits, upload, validateBinding, validatePath} from '../transport.mjs';

const hash = bytes => createHash('sha256').update(bytes).digest('hex');
const binding = {repository: 'owner/repo', run_id: '123', run_attempt: '1', source_sha: 'a'.repeat(40), experiment_id: 'test-01', stage: 'plan'};
const env = {GITHUB_REPOSITORY: binding.repository, GITHUB_RUN_ID: binding.run_id,
  GITHUB_RUN_ATTEMPT: binding.run_attempt, GITHUB_SHA: binding.source_sha};
const name = artifactName(binding);

function envelope(files = {'plan.json': '{}', 'public.bin': 'public-only'}) {
  return Buffer.from(JSON.stringify({schema_version: 1, binding, files: Object.entries(files).map(([file, data]) => {
    const bytes = Buffer.from(data);
    return {path: file, size: bytes.length, sha256: hash(bytes), data: bytes.toString('base64')};
  })}));
}

class FakeClient {
  records = [];
  bytes = new Map();
  calls = [];
  async listArtifacts(options) {
    assert.deepEqual(options, {latest: false});
    this.calls.push('list');
    return {artifacts: this.records.map(item => ({...item}))};
  }
  async uploadArtifact(actualName, files, root, options) {
    assert.equal(files.length, 1);
    assert.equal(path.basename(files[0]), actualName);
    assert.equal(path.dirname(files[0]), root);
    assert.deepEqual(options, {skipArchive: true, retentionDays: 7});
    this.calls.push('upload');
    const bytes = await fs.readFile(files[0]);
    const record = this.add(bytes, actualName);
    // Actual artifact6.3.1 upload returns bare hex; list/download use sha256: prefix.
    return {...record, digest: record.digest.slice('sha256:'.length)};
  }
  add(bytes, artifactName = name) {
    const record = {id: this.records.length + 1, name: artifactName, size: bytes.length, digest: `sha256:${hash(bytes)}`};
    this.records.push(record);
    this.bytes.set(record.id, bytes);
    return {...record};
  }
  async downloadArtifact(id, options) {
    this.calls.push('download');
    assert.equal(options.skipDecompress, true);
    assert.equal(options.expectedHash, this.records.find(item => item.id === id).digest);
    assert.equal(Object.hasOwn(options, 'findBy'), false);
    await fs.writeFile(path.join(options.path, 'raw-envelope'), this.bytes.get(id), {flag: 'wx'});
    return {downloadPath: options.path, digestMismatch: false};
  }
}

async function area(t) {
  const root = await fs.mkdtemp(path.join(os.tmpdir(), 'ppds-artifact-test-'));
  t.after(() => fs.rm(root, {recursive: true, force: true}));
  return root;
}

function request(client, dest, overrides = {}) {
  return {client, name, dest, binding, env, timeoutSeconds: 10, ...overrides};
}

test('public allowlist round-trips through one raw envelope and returns immutable identity', async t => {
  const root = await area(t);
  const source = path.join(root, 'source');
  await fs.mkdir(source);
  await fs.writeFile(path.join(source, 'plan.json'), '{}');
  await fs.writeFile(path.join(source, 'public.bin'), Buffer.from([0, 1, 2]));
  const client = new FakeClient();
  const receipt = await upload({client, name, root: source, inventory: ['plan.json', 'public.bin'], binding, env});
  const result = await download(request(client, path.join(root, 'received'), {
    expectedId: receipt.artifact.id, expectedDigest: receipt.artifact.digest
  }));
  assert.deepEqual(result.inventory, receipt.inventory);
  assert.deepEqual(await fs.readFile(path.join(result.destination, 'public.bin')), Buffer.from([0, 1, 2]));
  assert.equal(client.calls.filter(call => call === 'upload').length, 1);
  await assert.rejects(upload({client, name, root: source, inventory: ['plan.json', 'public.bin'], binding, env}), /already exists/);
});

test('current run, attempt, repository and source must match before transport', async t => {
  const root = await area(t);
  for (const key of ['repository', 'run_id', 'run_attempt', 'source_sha']) {
    const changed = {...binding, [key]: key === 'source_sha' ? 'b'.repeat(40) : key === 'repository' ? 'other/repo' : '2'};
    assert.throws(() => validateBinding(changed, env), /binding mismatch/);
  }
  const client = new FakeClient();
  await assert.rejects(download(request(client, path.join(root, 'out'), {name: name + '-old'})), /name mismatch/);
  assert.deepEqual(client.calls, []);
});

test('unsafe, platform-ambiguous and private-key filenames are refused', () => {
  for (const value of ['../x', '/x', 'C:/x', 'a\\b', 'a//b', 'x/./y', 'CON', 'nul.txt', 'x.', 'x:stream', 'a.snk', 'p.pem', 'private-key.bin']) {
    assert.throws(() => validatePath(value), /Unsafe|Private/);
  }
  assert.equal(validatePath('nested/PPDS.Plugins.dll'), 'nested/PPDS.Plugins.dll');
});

test('root must exactly match the explicit allowlist', async t => {
  const root = await area(t);
  await fs.writeFile(path.join(root, 'plan.json'), '{}');
  await fs.writeFile(path.join(root, 'unlisted.txt'), 'not approved');
  const client = new FakeClient();
  await assert.rejects(upload({client, name, root, inventory: ['plan.json'], binding, env}), /explicit upload inventory/);
  assert.deepEqual(client.calls, []);
});

test('hardlinked public source is refused before upload', async t => {
  const root = await area(t);
  const outside = path.join(root, 'original');
  const source = path.join(root, 'source');
  await fs.mkdir(source);
  await fs.writeFile(outside, '{}');
  await fs.link(outside, path.join(source, 'plan.json'));
  const client = new FakeClient();
  await assert.rejects(upload({client, name, root: source, inventory: ['plan.json'], binding, env}), /multiply-linked/);
  assert.equal(client.calls.includes('upload'), false);
});

test('directory junction or symlink is refused rather than followed', async t => {
  const root = await area(t);
  const source = path.join(root, 'source');
  const external = path.join(root, 'outside');
  await fs.mkdir(source); await fs.mkdir(external);
  await fs.writeFile(path.join(external, 'plan.json'), '{}');
  try {
    await fs.symlink(external, path.join(source, 'linked'), process.platform === 'win32' ? 'junction' : 'dir');
  } catch (error) {
    if (error.code === 'EPERM' || error.code === 'EACCES') {
      t.skip('Host denies creating the test-only directory link');
      return;
    }
    throw error;
  }
  const client = new FakeClient();
  await assert.rejects(upload({client, name, root: source, inventory: ['linked/plan.json'], binding, env}), /Symlink|junction/);
  assert.deepEqual(client.calls, []);
});

test('duplicates are refused instead of latest selection', async t => {
  const root = await area(t);
  const client = new FakeClient();
  client.add(envelope());
  client.add(envelope());
  await assert.rejects(download(request(client, path.join(root, 'out'))), /Duplicate/);
  assert.equal(client.calls.includes('download'), false);
});

test('missing digest, unexpected ID and unexpected digest fail before download', async t => {
  const root = await area(t);
  for (const change of ['missing', 'id', 'digest']) {
    const client = new FakeClient();
    client.add(envelope());
    if (change === 'missing') delete client.records[0].digest;
    await assert.rejects(download(request(client, path.join(root, change), {
      ...(change === 'id' ? {expectedId: 99} : {}),
      ...(change === 'digest' ? {expectedDigest: 'b'.repeat(64)} : {})
    })), /digest|ID mismatch/);
    assert.equal(client.calls.includes('download'), false);
  }
});

test('wait is bounded and can discover an artifact published by the concurrent job', async t => {
  const root = await area(t);
  let now = 0;
  const missing = new FakeClient();
  await assert.rejects(download(request(missing, path.join(root, 'missing'), {
    timeoutSeconds: 1, now: () => now, sleep: async ms => { now += ms; }
  })), /timed out/);
  assert.equal(now, 1000);
  now = 0;
  const arriving = new FakeClient();
  const result = await download(request(arriving, path.join(root, 'arrived'), {
    now: () => now, sleep: async ms => { now += ms; arriving.add(envelope()); }
  }));
  assert.equal(result.artifact.id, 1);
});

test('client digest mismatch and independent raw hash mismatch create no destination', async t => {
  const root = await area(t);
  for (const failure of ['client', 'raw']) {
    const client = new FakeClient();
    client.add(envelope());
    const original = client.downloadArtifact.bind(client);
    client.downloadArtifact = async (...args) => {
      const response = await original(...args);
      if (failure === 'client') return {...response, digestMismatch: true};
      const target = path.join(args[1].path, 'raw-envelope');
      const bytes = await fs.readFile(target); bytes[0] ^= 1; await fs.writeFile(target, bytes);
      return response;
    };
    const dest = path.join(root, failure);
    await assert.rejects(download(request(client, dest)), /digest/);
    await assert.rejects(fs.stat(dest), {code: 'ENOENT'});
  }
});

test('mismatched embedded binding is refused after outer integrity succeeds', async t => {
  const root = await area(t);
  const client = new FakeClient();
  const value = JSON.parse(envelope()); value.binding.run_attempt = '2';
  client.add(Buffer.from(JSON.stringify(value)));
  const dest = path.join(root, 'out');
  await assert.rejects(download(request(client, dest)), /binding/);
  await assert.rejects(fs.stat(dest), {code: 'ENOENT'});
});

test('all embedded paths and hashes are checked before destination writes', () => {
  for (const mutation of [
    value => { value.files[1].path = '../escape'; },
    value => { value.files[1].path = 'PLAN.JSON'; },
    value => { value.files[1].path = 'plan.json/child'; },
    value => { value.files[1].sha256 = '0'.repeat(64); },
    value => { value.files[1].data += '\n'; },
    value => { value.files[1].size = limits.decoded + 1; },
    value => { value.files = Array.from({length: 17}, (_, index) => ({...value.files[0], path: `f${index}`})); }
  ]) {
    const value = JSON.parse(envelope()); mutation(value);
    assert.throws(() => decodeEnvelope(Buffer.from(JSON.stringify(value)), binding));
  }
});

test('preexisting destination is never overwritten', async t => {
  const root = await area(t);
  const dest = path.join(root, 'out');
  await fs.mkdir(dest); await fs.writeFile(path.join(dest, 'keep'), 'existing');
  const client = new FakeClient(); client.add(envelope());
  await assert.rejects(download(request(client, dest)), /already exists/);
  assert.equal(await fs.readFile(path.join(dest, 'keep'), 'utf8'), 'existing');
  assert.equal(client.calls.includes('download'), false);
});

test('ZIP bytes are never extracted and cannot produce files', async t => {
  const root = await area(t);
  const client = new FakeClient(); client.add(Buffer.from('PK\x03\x04untrusted archive'));
  const dest = path.join(root, 'out');
  await assert.rejects(download(request(client, dest)), SyntaxError);
  await assert.rejects(fs.stat(dest), {code: 'ENOENT'});
});

test('download finishing after the supplied deadline cannot produce a success receipt', async t => {
  const root = await area(t);
  const client = new FakeClient(); client.add(envelope());
  let now = 0;
  const original = client.downloadArtifact.bind(client);
  client.downloadArtifact = async (...args) => { const result = await original(...args); now = 2000; return result; };
  const dest = path.join(root, 'out');
  await assert.rejects(download(request(client, dest, {timeoutSeconds: 1, now: () => now})), /timed out/);
  await assert.rejects(fs.stat(dest), {code: 'ENOENT'});
});

test('upload digest mismatch is an uncertain upload, never a successful receipt or retry', async t => {
  const root = await area(t); await fs.writeFile(path.join(root, 'plan.json'), '{}');
  const client = new FakeClient(); const original = client.uploadArtifact.bind(client);
  client.uploadArtifact = async (...args) => ({...await original(...args), digest: 'f'.repeat(64)});
  await assert.rejects(upload({client, name, root, inventory: ['plan.json'], binding, env}), /digest\/size mismatch/);
  assert.equal(client.calls.filter(call => call === 'upload').length, 1);
  assert.equal(client.records.length, 1);
});
