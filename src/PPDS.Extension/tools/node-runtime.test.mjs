import { execFileSync } from 'node:child_process';
import { createRequire } from 'node:module';
import { transform } from 'esbuild';
import { expect, it } from 'vitest';

const require = createRequire(import.meta.url);
const { builds } = require('../esbuild.js');

it('downlevels new host syntax for the minimum VS Code Node runtime', async () => {
    const host = builds.find(options => options.outfile === 'dist/extension.js');
    expect(host.platform).toBe('node');
    expect(host.target).toBe('node22.22.1');

    const result = await transform(`
        const events = [];
        { using resource = { [Symbol.dispose]() { events.push('disposed'); } }; events.push('used'); }
        console.log(JSON.stringify(events));
    `, { target: host.target, format: host.format });

    // This runs under Node 22 in the compatibility job, including the disposal helper.
    const output = execFileSync(process.execPath, ['--eval', result.code], { encoding: 'utf8' });
    expect(JSON.parse(output.trim())).toEqual(['used', 'disposed']);
    expect(builds.filter(options => options.platform === 'browser').every(options => options.target !== host.target)).toBe(true);
});
