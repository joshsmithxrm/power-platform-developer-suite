import fs from 'node:fs/promises';
import {upload, download} from './transport.mjs';

const [command, ...args] = process.argv.slice(2);
const allowed = new Set(['name', 'root', 'inventory', 'binding', 'result', 'dest', 'timeout-seconds', 'expected-id', 'expected-digest']);
const options = {};
let timer;
try {
  for (let i = 0; i < args.length; i += 2) {
    const key = args[i].slice(2);
    if (!args[i].startsWith('--') || !allowed.has(key) || options[key] !== undefined || !args[i + 1]) throw new Error('Invalid CLI arguments');
    options[key] = args[i + 1];
  }
  if (!['upload', 'download'].includes(command) || !options.name || !options.binding || !options.result) throw new Error('Missing CLI arguments');
  const timeoutSeconds = Number(options['timeout-seconds'] ?? (command === 'upload' ? 300 : NaN));
  if (!Number.isInteger(timeoutSeconds) || timeoutSeconds < 1 || timeoutSeconds > 3900) throw new Error('Invalid timeout');
  // A process deadline bounds even a hung client request or its internal retries.
  timer = setTimeout(() => { process.stderr.write('Artifact transport deadline exceeded\n'); process.exit(1); }, timeoutSeconds * 1000);
  const {DefaultArtifactClient} = await import('@actions/artifact');
  const client = new DefaultArtifactClient();
  const binding = JSON.parse(await fs.readFile(options.binding, 'utf8'));
  let result;
  if (command === 'upload') {
    if (!options.root || !options.inventory || options.dest || options['expected-id'] || options['expected-digest']) throw new Error('Invalid upload arguments');
    result = await upload({client, name: options.name, root: options.root, binding,
      inventory: JSON.parse(await fs.readFile(options.inventory, 'utf8'))});
  } else {
    if (!options.dest || options.root || options.inventory) throw new Error('Invalid download arguments');
    result = await download({client, name: options.name, dest: options.dest, binding, timeoutSeconds,
      expectedId: options['expected-id'] === undefined ? undefined : Number(options['expected-id']),
      expectedDigest: options['expected-digest']});
  }
  await fs.writeFile(options.result, JSON.stringify(result, null, 2) + '\n', {flag: 'wx'});
  clearTimeout(timer);
} catch {
  clearTimeout(timer);
  // Do not surface client exceptions: signed URLs or runtime values can occur in them.
  process.stderr.write('Artifact transport failed; no successful result was written\n');
  process.exitCode = 1;
}
