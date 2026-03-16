const esbuild = require('esbuild');
const production = process.argv.includes('--production');

// ── Build definitions ────────────────────────────────────────────────────────
// To add a new panel: add JS + CSS entries below. No other changes needed.

const builds = [
    // Extension host (Node.js, CJS)
    {
        entryPoints: ['src/extension.ts'],
        bundle: true,
        format: 'cjs',
        minify: production,
        sourcemap: !production,
        sourcesContent: false,
        platform: 'node',
        outfile: 'dist/extension.js',
        external: ['vscode'],
        logLevel: 'warning',
    },
    // Monaco editor bundle (browser, IIFE)
    {
        entryPoints: ['src/panels/monaco-entry.ts'],
        bundle: true,
        format: 'iife',
        minify: production,
        sourcemap: !production,
        sourcesContent: false,
        platform: 'browser',
        outfile: 'dist/monaco-editor.js',
        logLevel: 'warning',
        loader: { '.ttf': 'file' },
    },
    // Monaco editor worker (browser, IIFE)
    {
        entryPoints: ['src/panels/monaco-worker.ts'],
        bundle: true,
        format: 'iife',
        minify: production,
        sourcemap: false,
        sourcesContent: false,
        platform: 'browser',
        outfile: 'dist/editor.worker.js',
        logLevel: 'warning',
    },
    // ── Panel webview scripts (browser, IIFE) ────────────────────────────────
    {
        entryPoints: ['src/panels/webview/query-panel.ts'],
        bundle: true,
        format: 'iife',
        minify: production,
        sourcemap: !production,
        sourcesContent: false,
        platform: 'browser',
        outfile: 'dist/query-panel.js',
        logLevel: 'warning',
    },
    {
        entryPoints: ['src/panels/webview/solutions-panel.ts'],
        bundle: true,
        format: 'iife',
        minify: production,
        sourcemap: !production,
        sourcesContent: false,
        platform: 'browser',
        outfile: 'dist/solutions-panel.js',
        logLevel: 'warning',
    },
    // Import Jobs panel webview (browser, IIFE)
    {
        entryPoints: ['src/panels/webview/import-jobs-panel.ts'],
        bundle: true,
        format: 'iife',
        minify: production,
        sourcemap: !production,
        sourcesContent: false,
        platform: 'browser',
        outfile: 'dist/import-jobs-panel.js',
        logLevel: 'warning',
    },
    // ── Panel CSS bundles ────────────────────────────────────────────────────
    {
        entryPoints: ['src/panels/styles/query-panel.css'],
        bundle: true,
        minify: production,
        outfile: 'dist/query-panel.css',
        logLevel: 'warning',
    },
    {
        entryPoints: ['src/panels/styles/solutions-panel.css'],
        bundle: true,
        minify: production,
        outfile: 'dist/solutions-panel.css',
        logLevel: 'warning',
    },
    // Import Jobs panel CSS
    {
        entryPoints: ['src/panels/styles/import-jobs-panel.css'],
        bundle: true,
        minify: production,
        outfile: 'dist/import-jobs-panel.css',
        logLevel: 'warning',
    },
];

async function main() {
    const contexts = await Promise.all(builds.map(b => esbuild.context(b)));

    if (process.argv.includes('--watch')) {
        await Promise.all(contexts.map(c => c.watch()));
    } else {
        await Promise.all(contexts.map(c => c.rebuild()));
        await Promise.all(contexts.map(c => c.dispose()));
    }
}

main().catch(e => { console.error(e); process.exit(1); });
