import fs from 'node:fs/promises';
import path from 'node:path';
import os from 'node:os';
import {createHash} from 'node:crypto';

export const limits = Object.freeze({files: 16, decoded: 32 * 1024 * 1024, envelope: 48 * 1024 * 1024});
const bindingKeys = ['experiment_id', 'repository', 'run_attempt', 'run_id', 'source_sha', 'stage'];
const sha = value => createHash('sha256').update(value).digest('hex');
const fail = message => { throw new Error(message); };
const exactKeys = (value, keys) => value && typeof value === 'object' && !Array.isArray(value) &&
  Object.keys(value).sort().join(',') === [...keys].sort().join(',');

export function safeRefusal(error) {
  // Closed vocabulary only: client errors can contain signed URLs or tokens.
  const codes = new Map([
    ['Preparation reported failure', 'prepare-failed'],
    ['Missing or invalid artifact digest', 'metadata-digest'],
    ['Invalid artifact metadata', 'metadata-shape'],
    ['Unsafe directory', 'directory'],
    ['Artifact client did not confirm matching digest', 'client-digest'],
    ['Expected one raw envelope', 'raw-inventory'],
    ['Unsafe or wrong-size raw envelope', 'raw-size'],
    ['Downloaded envelope digest mismatch', 'raw-digest'],
    ['Envelope binding or schema mismatch', 'envelope-binding'],
    ['Envelope content mismatch', 'envelope-content'],
    ['Artifact wait timed out', 'wait-timeout'],
  ]);
  return codes.get(error?.message) ?? 'unclassified';
}

export function validateBinding(binding, env = process.env) {
  if (!exactKeys(binding, bindingKeys)) fail('Invalid binding fields');
  if (!/^[A-Za-z0-9_.-]+\/[A-Za-z0-9_.-]+$/.test(binding.repository) ||
      !/^[1-9][0-9]*$/.test(binding.run_id) || !/^[1-9][0-9]*$/.test(binding.run_attempt) ||
      !/^[0-9a-f]{40}$/.test(binding.source_sha) ||
      !/^[A-Za-z0-9_-]{1,64}$/.test(binding.experiment_id) ||
      !/^[a-z][a-z0-9-]{0,31}$/.test(binding.stage) ||
      Object.values(binding).some(value => typeof value !== 'string')) fail('Invalid binding values');
  const expected = {repository: env.GITHUB_REPOSITORY, run_id: env.GITHUB_RUN_ID,
    run_attempt: env.GITHUB_RUN_ATTEMPT, source_sha: env.GITHUB_SHA};
  for (const [key, value] of Object.entries(expected)) {
    if (!value || binding[key] !== value) fail(`Current-run binding mismatch: ${key}`);
  }
  return binding;
}

export function artifactName(binding) {
  return `ppds-compat-${binding.run_id}-${binding.run_attempt}-${binding.source_sha}-${binding.experiment_id}-${binding.stage}`;
}

export function validatePath(value) {
  if (typeof value !== 'string' || value.length > 200 || !value || /[^A-Za-z0-9._/-]/.test(value)) fail('Unsafe inventory path');
  const parts = value.split('/');
  if (parts.some(part => !part || part === '.' || part === '..' || /[. ]$/.test(part) ||
      /^(con|prn|aux|nul|com[0-9]|lpt[0-9])(?:\.|$)/i.test(part))) fail('Unsafe inventory path');
  if (/\.(snk|pfx|p12|pem|key)$/i.test(value) || /(?:^|[/._-])private(?:[/._-]|$)/i.test(value)) fail('Private-key filename refused');
  return value;
}

function validateNames(names) {
  if (!Array.isArray(names) || !names.length || names.length > limits.files) fail('Invalid inventory count');
  const lowered = new Set();
  for (const name of names) {
    validatePath(name);
    if (lowered.has(name.toLowerCase())) fail('Duplicate or case-colliding inventory path');
    lowered.add(name.toLowerCase());
  }
  for (const name of lowered) {
    for (const other of lowered) if (other.startsWith(`${name}/`)) fail('File/directory inventory collision');
  }
}

function normalizeDigest(value) {
  if (typeof value !== 'string' || !/^(?:sha256:)?[0-9a-f]{64}$/.test(value)) fail('Missing or invalid artifact digest');
  return value.startsWith('sha256:') ? value : `sha256:${value}`;
}

async function safeDirectory(directory) {
  const absolute = path.resolve(directory);
  const info = await fs.lstat(absolute);
  if (!info.isDirectory() || info.isSymbolicLink() || (await fs.realpath(absolute)).toLowerCase() !== absolute.toLowerCase()) fail('Unsafe directory');
  return absolute;
}

async function fileInventory(root, prefix = '') {
  const found = [];
  for (const entry of await fs.readdir(root, {withFileTypes: true})) {
    const name = prefix + entry.name;
    validatePath(name);
    const stat = await fs.lstat(path.join(root, entry.name));
    if (stat.isSymbolicLink()) fail('Symlink or junction refused');
    if (stat.isDirectory()) found.push(...await fileInventory(path.join(root, entry.name), `${name}/`));
    else if (stat.isFile()) found.push(name);
    else fail('Non-regular file refused');
    if (found.length > limits.files) fail('Inventory file limit exceeded');
  }
  return found;
}

function metadata(value, name) {
  if (!value || value.name !== name || !Number.isSafeInteger(value.id) || value.id <= 0 ||
      !Number.isSafeInteger(value.size) || value.size < 1 || value.size > limits.envelope) fail('Invalid artifact metadata');
  return {id: value.id, name, digest: normalizeDigest(value.digest), size: value.size};
}

async function matches(client, name) {
  // latest:false is deliberate: a duplicate must be refused, never silently selected.
  const response = await client.listArtifacts({latest: false});
  if (!response || !Array.isArray(response.artifacts)) fail('Invalid artifact list');
  const found = response.artifacts.filter(item => item.name === name);
  if (found.length > 1) fail('Duplicate named artifact');
  return found;
}

export async function upload({client, name, root, inventory, binding, env = process.env}) {
  validateBinding(binding, env);
  if (name !== artifactName(binding)) fail('Artifact name mismatch');
  validateNames(inventory);
  root = await safeDirectory(root);
  const actual = await fileInventory(root);
  if ([...actual].sort().join('\n') !== [...inventory].sort().join('\n')) fail('Root differs from explicit upload inventory');
  if ((await matches(client, name)).length) fail('Artifact already exists; upload is not repeatable');
  let decoded = 0;
  const files = [];
  for (const relative of inventory) {
    const source = path.join(root, relative);
    const info = await fs.lstat(source);
    if (!info.isFile() || info.isSymbolicLink() || info.nlink !== 1) fail('Non-regular or multiply-linked source refused');
    decoded += info.size;
    if (decoded > limits.decoded) fail('Decoded size limit exceeded');
    const bytes = await fs.readFile(source);
    if (bytes.length !== info.size) fail('Source changed during read');
    files.push({path: relative, size: bytes.length, sha256: sha(bytes), data: bytes.toString('base64')});
  }
  const bytes = Buffer.from(JSON.stringify({schema_version: 1, binding, files}));
  if (bytes.length > limits.envelope) fail('Envelope size limit exceeded');
  const temporary = await fs.mkdtemp(path.join(os.tmpdir(), 'ppds-public-upload-'));
  try {
    // skipArchive names the artifact from this basename. No archive or directory upload.
    const payload = path.join(temporary, name);
    await fs.writeFile(payload, bytes, {flag: 'wx'});
    const response = await client.uploadArtifact(name, [payload], temporary, {skipArchive: true, retentionDays: 7});
    const artifact = metadata({...response, name}, name);
    if (artifact.digest !== `sha256:${sha(bytes)}` || artifact.size !== bytes.length) fail('Uploaded artifact digest/size mismatch');
    const final = await matches(client, name);
    if (final.length !== 1 || metadata(final[0], name).id !== artifact.id ||
        metadata(final[0], name).digest !== artifact.digest ||
        metadata(final[0], name).size !== artifact.size) fail('Uploaded artifact identity not confirmed');
    return {schema_version: 1, operation: 'upload', artifact, binding,
      inventory: files.map(({data, ...item}) => item)};
  } finally {
    // Only the freshly created local public-envelope directory is removed.
    await fs.rm(temporary, {recursive: true, force: true});
  }
}

export function decodeEnvelope(bytes, binding) {
  if (bytes.length > limits.envelope) fail('Envelope size limit exceeded');
  const envelope = JSON.parse(bytes.toString('utf8'));
  if (!exactKeys(envelope, ['schema_version', 'binding', 'files']) || envelope.schema_version !== 1 ||
      !exactKeys(envelope.binding, bindingKeys) || bindingKeys.some(key => envelope.binding[key] !== binding[key]) ||
      !Array.isArray(envelope.files)) fail('Envelope binding or schema mismatch');
  validateNames(envelope.files.map(file => file.path));
  let decoded = 0;
  return envelope.files.map(file => {
    if (!exactKeys(file, ['path', 'size', 'sha256', 'data']) || !Number.isSafeInteger(file.size) || file.size < 0 ||
        !/^[0-9a-f]{64}$/.test(file.sha256) || typeof file.data !== 'string') fail('Invalid envelope file');
    decoded += file.size;
    if (decoded > limits.decoded || file.data.length > Math.ceil(file.size / 3) * 4) fail('Decoded size limit exceeded');
    const data = Buffer.from(file.data, 'base64');
    if (data.toString('base64') !== file.data || data.length !== file.size || sha(data) !== file.sha256) fail('Envelope content mismatch');
    return {...file, data};
  });
}

export async function download({client, name, dest, binding, timeoutSeconds, expectedId, expectedDigest,
  env = process.env, now = Date.now, sleep = ms => new Promise(resolve => setTimeout(resolve, ms))}) {
  validateBinding(binding, env);
  if (name !== artifactName(binding)) fail('Artifact name mismatch');
  if (!Number.isInteger(timeoutSeconds) || timeoutSeconds < 1 || timeoutSeconds > 3900) fail('Invalid timeout');
  if (expectedId !== undefined && (!Number.isSafeInteger(expectedId) || expectedId < 1)) fail('Invalid expected artifact ID');
  if (expectedDigest !== undefined) expectedDigest = normalizeDigest(expectedDigest);
  const deadline = now() + timeoutSeconds * 1000;
  let found;
  while (now() < deadline) {
    if (binding.stage === 'prepare') {
      // A same-run, same-source failure signal can only abort. It can never
      // admit data or produce a passing signing receipt.
      const failedName = artifactName({...binding, stage: 'prepare-failed'});
      const failed = await matches(client, failedName);
      if (failed.length) { metadata(failed[0], failedName); fail('Preparation reported failure'); }
    }
    found = await matches(client, name);
    if (found.length) break;
    await sleep(Math.min(5000, Math.max(0, deadline - now())));
  }
  if (!found?.length || now() >= deadline) fail('Artifact wait timed out');
  const artifact = metadata(found[0], name);
  if (expectedId !== undefined && artifact.id !== expectedId) fail('Artifact ID mismatch');
  if (expectedDigest !== undefined && artifact.digest !== expectedDigest) fail('Artifact digest mismatch');
  dest = path.resolve(dest);
  await safeDirectory(path.dirname(dest));
  try { await fs.lstat(dest); fail('Download destination already exists'); }
  catch (error) { if (error.code !== 'ENOENT') throw error; }
  const quarantine = await fs.mkdtemp(path.join(os.tmpdir(), 'ppds-public-download-'));
  try {
    const response = await client.downloadArtifact(artifact.id, {
      path: quarantine, expectedHash: artifact.digest, skipDecompress: true
    });
    if (response.digestMismatch !== false) fail('Artifact client did not confirm matching digest');
    const rawNames = await fs.readdir(quarantine);
    if (rawNames.length !== 1) fail('Expected one raw envelope');
    validatePath(rawNames[0]);
    const rawPath = path.join(quarantine, rawNames[0]);
    const info = await fs.lstat(rawPath);
    if (!info.isFile() || info.isSymbolicLink() || info.nlink !== 1 || info.size !== artifact.size) fail('Unsafe or wrong-size raw envelope');
    const bytes = await fs.readFile(rawPath);
    if (`sha256:${sha(bytes)}` !== artifact.digest) fail('Downloaded envelope digest mismatch');
    const files = decodeEnvelope(bytes, binding);
    if (now() >= deadline) fail('Artifact transfer timed out');
    // All files are decoded and verified before any destination is created.
    await fs.mkdir(dest, {recursive: false});
    for (const file of files) {
      const target = path.join(dest, file.path);
      await fs.mkdir(path.dirname(target), {recursive: true});
      await fs.writeFile(target, file.data, {flag: 'wx'});
    }
    if (now() >= deadline) fail('Artifact transfer timed out');
    return {schema_version: 1, operation: 'download', artifact, binding,
      inventory: files.map(({data, ...item}) => item), destination: dest};
  } finally {
    await fs.rm(quarantine, {recursive: true, force: true});
  }
}
