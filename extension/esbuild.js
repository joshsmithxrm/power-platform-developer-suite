const esbuild = require('esbuild');
const production = process.argv.includes('--production');

async function main() {
    // Build 1: Extension host (Node.js, CJS) — existing
    const extCtx = await esbuild.context({
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
    });

    // Build 2: Monaco editor bundle (browser, IIFE) — for webview
    const monacoCtx = await esbuild.context({
        entryPoints: ['src/panels/monaco-entry.ts'],
        bundle: true,
        format: 'iife',
        minify: production,
        sourcemap: !production,
        sourcesContent: false,
        platform: 'browser',
        outfile: 'dist/monaco-editor.js',
        logLevel: 'warning',
        loader: {
            '.ttf': 'file',
        },
    });

    // Build 3: Monaco editor worker (browser, IIFE) — loaded via blob URL
    const workerCtx = await esbuild.context({
        entryPoints: ['src/panels/monaco-worker.ts'],
        bundle: true,
        format: 'iife',
        minify: production,
        sourcemap: false,
        sourcesContent: false,
        platform: 'browser',
        outfile: 'dist/editor.worker.js',
        logLevel: 'warning',
    });

    // Build 4: Query panel webview script (browser, IIFE)
    // Extracted from inline <script> to avoid VS Code's ~32KB inline script limit.
    const queryPanelCtx = await esbuild.context({
        entryPoints: ['src/panels/query-panel-webview.js'],
        bundle: true,
        format: 'iife',
        minify: production,
        sourcemap: !production,
        sourcesContent: false,
        platform: 'browser',
        outfile: 'dist/query-panel.js',
        logLevel: 'warning',
    });

    if (process.argv.includes('--watch')) {
        await Promise.all([extCtx.watch(), monacoCtx.watch(), workerCtx.watch(), queryPanelCtx.watch()]);
    } else {
        await Promise.all([extCtx.rebuild(), monacoCtx.rebuild(), workerCtx.rebuild(), queryPanelCtx.rebuild()]);
        await Promise.all([extCtx.dispose(), monacoCtx.dispose(), workerCtx.dispose(), queryPanelCtx.dispose()]);
    }
}
main().catch(e => { console.error(e); process.exit(1); });
