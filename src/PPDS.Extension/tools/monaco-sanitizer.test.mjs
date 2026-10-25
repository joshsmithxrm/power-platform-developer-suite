import { mkdtempSync, mkdirSync, rmdirSync, unlinkSync, writeFileSync } from 'node:fs';
import { createRequire } from 'node:module';
import { tmpdir } from 'node:os';
import path from 'node:path';
import { fileURLToPath } from 'node:url';
import { build } from 'esbuild';
import DOMPurify from 'dompurify';
import { describe, expect, it } from 'vitest';

const require = createRequire(import.meta.url);
const { builds } = require('../esbuild.js');
const extensionRoot = fileURLToPath(new URL('..', import.meta.url));
const monacoBuild = builds.find(options => options.outfile === 'dist/monaco-editor.js');

describe('Monaco sanitizer bundle', () => {
    it('bundles the npm sanitizer instead of Monaco\'s vendored copy', async () => {
        const result = await build({
            ...monacoBuild,
            absWorkingDir: extensionRoot,
            minify: true,
            sourcemap: false,
            metafile: true,
            write: false,
        });
        const sanitizerInputs = Object.keys(result.metafile.inputs)
            .filter(input => input.includes('/dompurify/'));
        expect(sanitizerInputs).toEqual(['node_modules/dompurify/dist/purify.es.mjs']);

        const bundle = result.outputFiles.find(output => output.path.endsWith('monaco-editor.js')).text;
        expect(bundle).toContain(`@license DOMPurify ${DOMPurify.version}`);
        expect(bundle).not.toContain('@license DOMPurify 3.2.7');
    });

    it('leaves the same relative import outside Monaco unchanged', async () => {
        const fixture = mkdtempSync(path.join(tmpdir(), 'ppds-monaco-sanitizer-'));
        const dependencyDirectory = path.join(fixture, 'dompurify');
        const entry = path.join(fixture, 'domSanitize.js');
        const dependency = path.join(dependencyDirectory, 'dompurify.js');
        mkdirSync(dependencyDirectory);
        writeFileSync(entry, "export { default } from './dompurify/dompurify.js';");
        writeFileSync(dependency, "export default 'unrelated sanitizer';");
        try {
            const result = await build({
                entryPoints: [entry],
                bundle: true,
                write: false,
                plugins: monacoBuild.plugins,
                metafile: true,
            });
            expect(result.outputFiles[0].text).toContain('unrelated sanitizer');
            expect(Object.keys(result.metafile.inputs)).not.toContain('node_modules/dompurify/dist/purify.es.mjs');
        } finally {
            unlinkSync(entry);
            unlinkSync(dependency);
            rmdirSync(dependencyDirectory);
            rmdirSync(fixture);
        }
    });
});
