import test from 'node:test';
import assert from 'node:assert/strict';
import fs from 'node:fs/promises';
import os from 'node:os';
import path from 'node:path';
import {fileURLToPath} from 'node:url';
import {spawnSync} from 'node:child_process';

const wrapper = fileURLToPath(new URL('../action.mjs', import.meta.url));
const probe = spawnSync(process.env.PPDS_TEST_PYTHON ?? 'python', ['-I', '-B', '-c', 'import sys; print(sys.executable)'], {encoding: 'utf8', shell: false});
const python = probe.status === 0 ? probe.stdout.trim() : null;

test('action forwards runtime context and literal arguments to fixed isolated Python coordinator', {skip: !python}, async t => {
  const workspace = await fs.mkdtemp(path.join(os.tmpdir(), 'ppds-action-test-'));
  t.after(() => fs.rm(workspace, {recursive: true, force: true}));
  const coordinator = path.join(workspace, 'scripts', 'ci', 'hosted_signing_compatibility.py');
  await fs.mkdir(path.dirname(coordinator), {recursive: true});
  await fs.writeFile(coordinator, 'import json, os, sys\nprint(json.dumps({"args": sys.argv[1:], "runtime": os.environ["ACTIONS_RUNTIME_TOKEN"], "node": os.environ["PPDS_ARTIFACT_NODE"], "isolated": sys.flags.isolated, "bytecode": sys.dont_write_bytecode}))\n');
  const args = ['--source', 'a'.repeat(40), '--authorization', 'literal & $(not-a-command)'];
  const result = spawnSync(process.execPath, [wrapper], {encoding: 'utf8', shell: false,
    env: {...process.env, GITHUB_WORKSPACE: workspace, 'INPUT_PYTHON-PATH': python,
      'INPUT_COORDINATOR-PATH': coordinator, 'INPUT_ARGUMENTS-JSON': JSON.stringify(args),
      ACTIONS_RUNTIME_TOKEN: 'FAKE-OFFLINE-RUNTIME-TOKEN'}});
  assert.equal(result.status, 0, result.stderr);
  const receipt = JSON.parse(result.stdout);
  assert.deepEqual(receipt.args, args);
  assert.equal(receipt.runtime, 'FAKE-OFFLINE-RUNTIME-TOKEN');
  assert.equal(receipt.node, process.execPath);
  assert.equal(receipt.isolated, 1);
  assert.equal(receipt.bytecode, true);
});

test('action refuses alternate coordinator paths without spawning them', {skip: !python}, async t => {
  const workspace = await fs.mkdtemp(path.join(os.tmpdir(), 'ppds-action-test-'));
  t.after(() => fs.rm(workspace, {recursive: true, force: true}));
  const wrong = path.join(workspace, 'other.py');
  await fs.writeFile(wrong, 'raise RuntimeError("must not run")\n');
  const result = spawnSync(process.execPath, [wrapper], {encoding: 'utf8', shell: false,
    env: {...process.env, GITHUB_WORKSPACE: workspace, 'INPUT_PYTHON-PATH': python,
      'INPUT_COORDINATOR-PATH': wrong, 'INPUT_ARGUMENTS-JSON': '[]'}});
  assert.equal(result.status, 1);
  assert.match(result.stderr, /input validation failed/);
  assert.doesNotMatch(result.stderr, /must not run/);
});
