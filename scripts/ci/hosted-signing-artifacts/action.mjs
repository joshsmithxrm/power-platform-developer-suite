import path from 'node:path';
import fs from 'node:fs/promises';
import {spawn} from 'node:child_process';

try {
  const python = process.env['INPUT_PYTHON-PATH'];
  const coordinator = process.env['INPUT_COORDINATOR-PATH'];
  const workspace = process.env.GITHUB_WORKSPACE;
  const args = JSON.parse(process.env['INPUT_ARGUMENTS-JSON'] ?? 'null');
  if (!python || !path.isAbsolute(python) || !workspace || !coordinator ||
      path.resolve(coordinator) !== path.resolve(workspace, 'scripts/ci/hosted_signing_compatibility.py') ||
      !Array.isArray(args) || args.some(arg => typeof arg !== 'string' || arg.includes('\0'))) throw new Error('Invalid coordinator input');
  if (!(await fs.stat(python)).isFile() || !(await fs.stat(coordinator)).isFile()) throw new Error('Missing admitted executable or coordinator');
  const child = spawn(python, ['-I', '-B', coordinator, ...args], {shell: false, stdio: 'inherit', cwd: workspace,
    env: {...process.env, PPDS_ARTIFACT_NODE: process.execPath}});
  const timer = setTimeout(() => { child.kill(); process.exitCode = 1; }, 65 * 60 * 1000);
  child.on('error', () => { clearTimeout(timer); process.stderr.write('Compatibility coordinator could not start\n'); process.exitCode = 1; });
  child.on('exit', code => { clearTimeout(timer); process.exitCode = code === 0 ? 0 : 1; });
} catch {
  process.stderr.write('Compatibility action input validation failed\n');
  process.exitCode = 1;
}
